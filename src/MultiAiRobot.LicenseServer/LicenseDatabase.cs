using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MultiAiRobot.LicenseServer;

/// <summary>All application SQL is parameterized. Keys and bearer tokens are stored only as SHA-256 hashes.</summary>
public sealed partial class LicenseDatabase
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ServerOptions _options;
    private readonly PasswordHasher<string> _passwordHasher;
    private readonly string _dummyPasswordHash;
    private readonly SemaphoreSlim _loginGate = new(4, 4);

    public LicenseDatabase(string dataDirectory, ServerOptions options)
    {
        _options = options;
        Directory.CreateDirectory(dataDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "license-server.sqlite3");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = true, DefaultTimeout = 10
        }.ToString();
        _passwordHasher = new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
        _dummyPasswordHash = _passwordHasher.HashPassword("dummy", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public void Initialize()
    {
        using var db = Open();
        Execute(db, null, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version;"));
        if (version is not (0 or 1)) throw new InvalidOperationException("Unsupported license database schema version.");
        using var tx = db.BeginTransaction(deferred: false);
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY, username TEXT NOT NULL, normalized_username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL, is_admin INTEGER NOT NULL CHECK(is_admin IN (0,1)),
                enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)), created_at INTEGER NOT NULL,
                failed_attempts INTEGER NOT NULL DEFAULT 0, locked_until INTEGER
            );
            CREATE TABLE IF NOT EXISTS licenses (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id),
                key_hash TEXT NOT NULL UNIQUE, key_prefix TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)), expires_at INTEGER NOT NULL,
                max_devices INTEGER NOT NULL CHECK(max_devices BETWEEN 1 AND 100), created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS devices (
                license_id TEXT NOT NULL REFERENCES licenses(id) ON DELETE CASCADE,
                instance_id TEXT NOT NULL, bound_at INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                online INTEGER NOT NULL DEFAULT 0 CHECK(online IN (0,1)),
                PRIMARY KEY(license_id, instance_id)
            );
            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('admin','bot')),
                user_id TEXT NOT NULL REFERENCES users(id), license_id TEXT REFERENCES licenses(id),
                instance_id TEXT, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, revoked_at INTEGER,
                FOREIGN KEY(license_id,instance_id) REFERENCES devices(license_id,instance_id) ON DELETE CASCADE,
                CHECK((kind='admin' AND license_id IS NULL AND instance_id IS NULL) OR
                      (kind='bot' AND license_id IS NOT NULL AND instance_id IS NOT NULL))
            );
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at INTEGER NOT NULL,
                action TEXT NOT NULL, subject TEXT NOT NULL, actor TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_license_user ON licenses(user_id);
            CREATE INDEX IF NOT EXISTS ix_session_user ON sessions(user_id);
            CREATE INDEX IF NOT EXISTS ix_session_device ON sessions(license_id,instance_id);
            CREATE INDEX IF NOT EXISTS ix_session_expiry ON sessions(expires_at);
            CREATE INDEX IF NOT EXISTS ix_session_revoked ON sessions(revoked_at);
            CREATE INDEX IF NOT EXISTS ix_audit_time ON audit(occurred_at DESC);
            PRAGMA user_version=1;
            """);
        tx.Commit();
        PruneOldSessions(db, null);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public bool RequiresSetup()
    {
        using var db = Open();
        return Convert.ToInt64(Scalar(db, null, "SELECT COUNT(*) FROM users WHERE is_admin=1;")) == 0;
    }

    public void CreateInitialAdministrator(string username, string password)
    {
        username = ValidateUsername(username);
        ValidatePassword(password);
        var hash = _passwordHasher.HashPassword(username, password);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM users WHERE is_admin=1;")) != 0)
            throw new InvalidOperationException("An administrator already exists. No account was changed.");
        Execute(db, tx, """
            INSERT INTO users(id,username,normalized_username,password_hash,is_admin,enabled,created_at)
            VALUES($id,$name,$normalized,$hash,1,1,$now);
            """, ("$id", NewId()), ("$name", username), ("$normalized", NormalizeUsername(username)),
            ("$hash", hash), ("$now", Now));
        Audit(db, tx, "admin.initialized", username, "local-console");
        tx.Commit();
    }

    public AdminLoginDto LoginAdmin(AdminLoginRequest request)
    {
        ValidateCredentialShape(request.Username, request.Password);
        using var loginLease = EnterLogin();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var user = CheckCredentials(db, tx, request.Username, request.Password, requireAdmin: true);
        var token = NewSecret("adm_");
        var expires = Now + _options.AdminSessionHours * 3600L;
        InsertSession(db, tx, token, "admin", user.Id, null, null, expires);
        Audit(db, tx, "admin.login", user.Username, user.Username);
        tx.Commit();
        return new(token, user.Username, Utc(expires));
    }

    public AdminPrincipal AuthenticateAdmin(string token)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT s.user_id,u.username,s.expires_at,s.revoked_at,u.enabled,u.is_admin
            FROM sessions s JOIN users u ON u.id=s.user_id
            WHERE s.token_hash=$hash AND s.kind='admin';
            """, ("$hash", HashToken(token)));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(2) <= Now || !reader.IsDBNull(3) ||
            reader.GetInt64(4) != 1 || reader.GetInt64(5) != 1)
            throw Unauthorized();
        return new(HashToken(token), reader.GetString(0), reader.GetString(1));
    }

    public void LogoutAdmin(AdminPrincipal admin)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        Execute(db, tx, "UPDATE sessions SET revoked_at=$now WHERE token_hash=$hash;",
            ("$now", Now), ("$hash", admin.SessionHash));
        Audit(db, tx, "admin.logout", admin.Username, admin.Username);
        tx.Commit();
    }

    public IReadOnlyList<UserDto> GetUsers()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,username,enabled,created_at FROM users WHERE is_admin=0 ORDER BY username COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<UserDto>();
        while (reader.Read()) result.Add(ReadUser(reader));
        return result;
    }

    public UserDto CreateUser(AdminPrincipal admin, CreateUserRequest request)
    {
        var username = ValidateUsername(request.Username);
        ValidatePassword(request.Password);
        var hash = _passwordHasher.HashPassword(username, request.Password!);
        var id = NewId();
        var now = Now;
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (Scalar(db, tx, "SELECT id FROM users WHERE normalized_username=$name;", ("$name", NormalizeUsername(username))) is not null)
            throw new ApiException(409, "username_exists", "That username already exists.");
        Execute(db, tx, """
            INSERT INTO users(id,username,normalized_username,password_hash,is_admin,enabled,created_at)
            VALUES($id,$name,$normalized,$hash,0,1,$now);
            """, ("$id", id), ("$name", username), ("$normalized", NormalizeUsername(username)), ("$hash", hash), ("$now", now));
        Audit(db, tx, "user.created", username, admin.Username);
        tx.Commit();
        return new(id, username, true, Utc(now));
    }

    public UserDto UpdateUser(AdminPrincipal admin, string id, UpdateUserRequest request)
    {
        if (request.Enabled is null && request.Password is null)
            throw Invalid("Supply enabled or password.");
        if (request.Password is not null) ValidatePassword(request.Password);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = GetUser(db, tx, id);
        var enabled = request.Enabled ?? current.Enabled;
        Execute(db, tx, "UPDATE users SET enabled=$enabled WHERE id=$id;", ("$enabled", enabled), ("$id", id));
        if (request.Password is not null)
        {
            Execute(db, tx, "UPDATE users SET password_hash=$hash,failed_attempts=0,locked_until=NULL WHERE id=$id;",
                ("$hash", _passwordHasher.HashPassword(current.Username, request.Password)), ("$id", id));
            Audit(db, tx, "user.password_changed", current.Username, admin.Username);
        }
        if (!enabled || request.Password is not null)
        {
            Execute(db, tx, "UPDATE sessions SET revoked_at=$now WHERE user_id=$id AND revoked_at IS NULL;", ("$now", Now), ("$id", id));
            Execute(db, tx, "UPDATE devices SET online=0 WHERE license_id IN (SELECT id FROM licenses WHERE user_id=$id);", ("$id", id));
        }
        if (request.Enabled is not null) Audit(db, tx, enabled ? "user.enabled" : "user.disabled", current.Username, admin.Username);
        tx.Commit();
        return current with { Enabled = enabled };
    }

    public IReadOnlyList<LicenseDto> GetLicenses()
    {
        using var db = Open();
        var ids = new List<string>();
        using (var command = Command(db, null, "SELECT id FROM licenses ORDER BY created_at DESC,id;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => GetLicense(db, null, id)).ToList();
    }

    public LicenseIssueDto IssueLicense(AdminPrincipal admin, CreateLicenseRequest request)
    {
        var username = ValidateUsername(request.Username);
        if (request.Days is < 1 or > 3650) throw Invalid("Days must be between 1 and 3650.");
        ValidateDeviceLimit(request.MaxDevices);
        var key = "MAR-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var id = NewId();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var userId = Scalar(db, tx, "SELECT id FROM users WHERE normalized_username=$name AND is_admin=0 AND enabled=1;",
            ("$name", NormalizeUsername(username))) as string;
        if (userId is null) throw Invalid("Select an enabled application user.");
        var now = Now;
        Execute(db, tx, """
            INSERT INTO licenses(id,user_id,key_hash,key_prefix,enabled,expires_at,max_devices,created_at)
            VALUES($id,$user,$hash,$prefix,1,$expiry,$max,$now);
            """, ("$id", id), ("$user", userId), ("$hash", HashLicense(key)), ("$prefix", key[..12]),
            ("$expiry", now + request.Days * 86400L), ("$max", request.MaxDevices), ("$now", now));
        Audit(db, tx, "license.issued", id, admin.Username);
        var license = GetLicense(db, tx, id);
        tx.Commit();
        return new(license, key);
    }

    public LicenseDto UpdateLicense(AdminPrincipal admin, string id, UpdateLicenseRequest request)
    {
        if (request.Enabled is null && request.ExpiresAtUtc is null && request.MaxDevices is null)
            throw Invalid("Supply enabled, expiresAtUtc, or maxDevices.");
        if (request.MaxDevices is not null) ValidateDeviceLimit(request.MaxDevices.Value);
        if (request.ExpiresAtUtc is not null && (request.ExpiresAtUtc.Value < DateTimeOffset.UnixEpoch ||
            request.ExpiresAtUtc.Value > DateTimeOffset.UtcNow.AddYears(20)))
            throw Invalid("The expiry must be a valid date within 20 years.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = GetLicense(db, tx, id);
        var maxDevices = request.MaxDevices ?? current.MaxDevices;
        if (maxDevices < current.Devices.Count)
            throw new ApiException(409, "device_limit_conflict", "Reset devices before reducing the limit below the current number of bindings.");
        var enabled = request.Enabled ?? current.Enabled;
        var expires = (request.ExpiresAtUtc ?? current.ExpiresAtUtc).ToUnixTimeSeconds();
        Execute(db, tx, "UPDATE licenses SET enabled=$enabled,expires_at=$expires,max_devices=$max WHERE id=$id;",
            ("$enabled", enabled), ("$expires", expires), ("$max", maxDevices), ("$id", id));
        if (!enabled || expires <= Now)
        {
            Execute(db, tx, "UPDATE sessions SET revoked_at=$now WHERE license_id=$id AND revoked_at IS NULL;", ("$now", Now), ("$id", id));
            Execute(db, tx, "UPDATE devices SET online=0 WHERE license_id=$id;", ("$id", id));
        }
        Audit(db, tx, "license.updated", id, admin.Username);
        var result = GetLicense(db, tx, id);
        tx.Commit();
        return result;
    }

    public void ResetDevice(AdminPrincipal admin, string licenseId, string instanceId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        _ = GetLicense(db, tx, licenseId);
        var changed = Execute(db, tx, "DELETE FROM devices WHERE license_id=$id AND instance_id=$instance;",
            ("$id", licenseId), ("$instance", instanceId));
        if (changed == 0) throw new ApiException(404, "device_not_found", "That device binding was not found.");
        // The composite foreign key deletes every session for this binding.
        // Reusing the same instance ID later never revives a deleted token.
        Audit(db, tx, "device.reset", licenseId + "/" + instanceId, admin.Username);
        tx.Commit();
    }

    public OverviewDto GetOverview()
    {
        using var db = Open();
        int Count(string sql, params (string, object?)[] args) => Convert.ToInt32(Scalar(db, null, sql, args));
        return new(
            Count("SELECT COUNT(*) FROM users WHERE is_admin=0;"),
            Count("SELECT COUNT(*) FROM licenses;"),
            Count("SELECT COUNT(*) FROM licenses l JOIN users u ON u.id=l.user_id WHERE l.enabled=1 AND u.enabled=1 AND l.expires_at>$now;", ("$now", Now)),
            Count("""
                SELECT COUNT(*) FROM devices d JOIN licenses l ON l.id=d.license_id JOIN users u ON u.id=l.user_id
                WHERE d.online=1 AND d.last_seen>$cutoff AND l.enabled=1 AND u.enabled=1 AND l.expires_at>$now
                AND EXISTS(SELECT 1 FROM sessions s WHERE s.license_id=d.license_id AND s.instance_id=d.instance_id
                    AND s.kind='bot' AND s.revoked_at IS NULL AND s.expires_at>$now);
                """, ("$cutoff", Now - _options.OnlineTimeoutSeconds), ("$now", Now)));
    }

    public IReadOnlyList<AuditDto> GetAudit()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT occurred_at,action,subject,actor FROM audit ORDER BY id DESC LIMIT 100;");
        using var reader = command.ExecuteReader();
        var result = new List<AuditDto>();
        while (reader.Read()) result.Add(new(Utc(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public BotLoginDto LoginBot(BotLoginRequest request)
    {
        ValidateCredentialShape(request.Username, request.Password);
        var instance = ValidateInstanceId(request.InstanceId);
        var suppliedKeyHash = HashLicense(request.LicenseKey);
        using var loginLease = EnterLogin();
        using var db = Open();
        // Immediate transaction serializes allocation + session creation. Two
        // parallel first-time devices cannot both take the final available slot.
        using var tx = db.BeginTransaction(deferred: false);
        var user = CheckCredentials(db, tx, request.Username, request.Password, requireAdmin: false);
        string licenseId;
        long expires;
        int maxDevices;
        bool enabled;
        using (var command = Command(db, tx, "SELECT id,enabled,expires_at,max_devices FROM licenses WHERE key_hash=$hash AND user_id=$user;",
                   ("$hash", suppliedKeyHash), ("$user", user.Id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                reader.Close();
                Audit(db, tx, "bot.login_denied", user.Username, "anonymous");
                tx.Commit();
                throw new ApiException(401, "invalid_credentials", "The account credentials or license key were not accepted.");
            }
            licenseId = reader.GetString(0); enabled = reader.GetInt64(1) == 1;
            expires = reader.GetInt64(2); maxDevices = reader.GetInt32(3);
        }
        if (!enabled) throw new ApiException(403, "license_disabled", "This license is disabled.");
        if (expires <= Now) throw new ApiException(403, "license_expired", "This license has expired.");
        var exists = Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM devices WHERE license_id=$license AND instance_id=$instance;",
            ("$license", licenseId), ("$instance", instance))) != 0;
        if (!exists)
        {
            var count = Convert.ToInt32(Scalar(db, tx, "SELECT COUNT(*) FROM devices WHERE license_id=$license;", ("$license", licenseId)));
            if (count >= maxDevices) throw new ApiException(409, "device_limit", "The device limit has been reached. Ask the owner to reset an unused device.");
            Execute(db, tx, "INSERT INTO devices(license_id,instance_id,bound_at,last_seen,online) VALUES($license,$instance,$now,$now,1);",
                ("$license", licenseId), ("$instance", instance), ("$now", Now));
        }
        else Execute(db, tx, "UPDATE devices SET last_seen=$now,online=1 WHERE license_id=$license AND instance_id=$instance;",
            ("$license", licenseId), ("$instance", instance), ("$now", Now));
        // One live bearer session per bound device. Re-login replaces its old token.
        Execute(db, tx, "UPDATE sessions SET revoked_at=$now WHERE license_id=$license AND instance_id=$instance AND revoked_at IS NULL;",
            ("$license", licenseId), ("$instance", instance), ("$now", Now));
        var token = NewSecret("bot_");
        var sessionExpiry = Math.Min(expires, Now + _options.BotSessionHours * 3600L);
        InsertSession(db, tx, token, "bot", user.Id, licenseId, instance, sessionExpiry);
        Audit(db, tx, "bot.login", licenseId + "/" + instance, user.Username);
        tx.Commit();
        return new(token, Utc(sessionExpiry));
    }

    public BotStateDto CheckBot(string token, BotLicenseRequest request, string action)
    {
        var instance = ValidateInstanceId(request.InstanceId);
        var suppliedHash = HashLicense(request.LicenseKey);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var sessionHash = HashToken(token);
        BotSessionRow row;
        using (var command = Command(db, tx, """
            SELECT s.user_id,s.license_id,s.instance_id,s.expires_at,s.revoked_at,
                   u.username,u.enabled,u.is_admin,l.user_id,l.key_hash,l.enabled,l.expires_at,
                   d.instance_id,d.online,d.last_seen,l.max_devices,
                   (SELECT COUNT(*) FROM devices x WHERE x.license_id=l.id)
            FROM sessions s JOIN users u ON u.id=s.user_id JOIN licenses l ON l.id=s.license_id
            LEFT JOIN devices d ON d.license_id=s.license_id AND d.instance_id=s.instance_id
            WHERE s.token_hash=$hash AND s.kind='bot';
            """, ("$hash", sessionHash)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw Unauthorized();
            row = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                !reader.IsDBNull(4), reader.GetString(5), reader.GetInt64(6) == 1, reader.GetInt64(7) == 1,
                reader.GetString(8), reader.GetString(9), reader.GetInt64(10) == 1, reader.GetInt64(11),
                !reader.IsDBNull(12), !reader.IsDBNull(13) && reader.GetInt64(13) == 1,
                reader.IsDBNull(14) ? 0 : reader.GetInt64(14), reader.GetInt32(15), reader.GetInt32(16));
        }
        if (row.Revoked || row.SessionExpires <= Now) throw Unauthorized();
        if (!string.Equals(instance, row.InstanceId, StringComparison.Ordinal) ||
            !FixedEquals(suppliedHash, row.KeyHash))
            throw new ApiException(403, "session_mismatch", "This session does not match the supplied license and device.");
        string? reason = !row.UserEnabled || row.IsAdmin ? "user_disabled"
            : row.UserId != row.LicenseUserId ? "license_assignment_changed"
            : !row.LicenseEnabled ? "license_disabled"
            : row.LicenseExpires <= Now ? "license_expired"
            : !row.DeviceExists ? "device_reset"
            : row.DeviceCount > row.MaxDevices ? "device_limit" : null;
        if (reason is not null)
        {
            Execute(db, tx, "UPDATE sessions SET revoked_at=$now WHERE token_hash=$hash;", ("$now", Now), ("$hash", sessionHash));
            Execute(db, tx, "UPDATE devices SET online=0 WHERE license_id=$license AND instance_id=$instance;",
                ("$license", row.LicenseId), ("$instance", instance));
            Audit(db, tx, "bot.session_revoked", row.LicenseId + "/" + instance, row.Username);
            tx.Commit();
            throw new ApiException(403, reason, "The account, license, or device is no longer authorized. Sign in again after the owner resolves it.");
        }
        var online = row.Online && row.LastSeen > Now - _options.OnlineTimeoutSeconds;
        if (action is "heartbeat" or "verify")
        {
            Execute(db, tx, "UPDATE devices SET last_seen=$now,online=1 WHERE license_id=$license AND instance_id=$instance;",
                ("$license", row.LicenseId), ("$instance", instance), ("$now", Now));
            online = true;
        }
        else if (action == "offline")
        {
            Execute(db, tx, "UPDATE devices SET online=0,last_seen=$now WHERE license_id=$license AND instance_id=$instance;",
                ("$license", row.LicenseId), ("$instance", instance), ("$now", Now));
            Audit(db, tx, "bot.offline", row.LicenseId + "/" + instance, row.Username);
            online = false;
        }
        tx.Commit();
        return new(true, true, online, online ? "active" : "offline", null, _options.OnlineTimeoutSeconds);
    }

    private CredentialUser CheckCredentials(SqliteConnection db, SqliteTransaction tx, string? username, string? password, bool requireAdmin)
    {
        var normalized = NormalizeUsername((username ?? "").Trim());
        CredentialUser? user = null;
        using (var command = Command(db, tx, "SELECT id,username,password_hash,enabled,is_admin,failed_attempts,locked_until FROM users WHERE normalized_username=$name;",
                   ("$name", normalized.Length <= 64 ? normalized : "")))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) user = new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3) == 1, reader.GetInt64(4) == 1, reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6));
        var supplied = password is { Length: <= 256 } ? password : "";
        var verification = _passwordHasher.VerifyHashedPassword(user?.Username ?? "dummy", user?.PasswordHash ?? _dummyPasswordHash, supplied);
        var accepted = user is not null && user.Enabled && user.IsAdmin == requireAdmin &&
                       (user.LockedUntil is null || user.LockedUntil <= Now) && verification != PasswordVerificationResult.Failed;
        if (!accepted)
        {
            if (user is not null && verification == PasswordVerificationResult.Failed)
            {
                var previousLockoutExpired = user.LockedUntil is not null && user.LockedUntil <= Now;
                var failed = previousLockoutExpired ? 1 : user.FailedAttempts + 1;
                long? locked = failed >= _options.FailedPasswordAttemptsBeforeLockout
                    ? Now + _options.PasswordLockoutMinutes * 60L
                    : previousLockoutExpired ? null : user.LockedUntil;
                Execute(db, tx, "UPDATE users SET failed_attempts=$failed,locked_until=$until WHERE id=$id;",
                    ("$failed", failed), ("$until", locked), ("$id", user.Id));
            }
            Audit(db, tx, requireAdmin ? "admin.login_denied" : "bot.login_denied", SafeSubject(username), "anonymous");
            tx.Commit();
            throw new ApiException(401, "invalid_credentials", "The account credentials were not accepted, or the account is temporarily locked.");
        }
        Execute(db, tx, "UPDATE users SET failed_attempts=0,locked_until=NULL WHERE id=$id;", ("$id", user!.Id));
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            Execute(db, tx, "UPDATE users SET password_hash=$hash WHERE id=$id;",
                ("$hash", _passwordHasher.HashPassword(user.Username, supplied)), ("$id", user.Id));
        return user;
    }

    private IDisposable EnterLogin()
    {
        // Password verification is deliberately expensive. Bound concurrent
        // work without queuing callers behind SQLite's single writer lock.
        if (!_loginGate.Wait(0))
            throw new ApiException(429, "rate_limited", "Too many sign-in attempts are in progress. Try again shortly.");
        return new LoginLease(_loginGate);
    }

    private static void ValidateCredentialShape(string? username, string? password)
    {
        // Public creation rules make these impossible credentials. Reject them
        // before acquiring a write lock or computing an expensive dummy hash.
        // Plausible unknown usernames still use the normal dummy-hash path.
        if (username is null || !UsernamePattern().IsMatch(username.Trim()) ||
            password is null || password.Length is < 12 or > 256 || string.IsNullOrWhiteSpace(password))
            throw new ApiException(401, "invalid_credentials", "The account credentials were not accepted.");
    }

    private LicenseDto GetLicense(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        LicenseDto license;
        using (var command = Command(db, tx, """
            SELECT l.id,u.username,l.key_prefix,l.enabled,l.expires_at,l.max_devices,l.created_at
            FROM licenses l JOIN users u ON u.id=l.user_id WHERE l.id=$id;
            """, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new ApiException(404, "license_not_found", "The license was not found.");
            license = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1,
                Utc(reader.GetInt64(4)), reader.GetInt32(5), Utc(reader.GetInt64(6)), []);
        }
        var devices = new List<DeviceDto>();
        using (var command = Command(db, tx, """
            SELECT d.instance_id,d.last_seen,
                CASE WHEN d.online=1 AND d.last_seen>$cutoff AND l.enabled=1 AND l.expires_at>$now AND u.enabled=1
                AND EXISTS(SELECT 1 FROM sessions s WHERE s.license_id=d.license_id AND s.instance_id=d.instance_id
                    AND s.kind='bot' AND s.revoked_at IS NULL AND s.expires_at>$now) THEN 1 ELSE 0 END
            FROM devices d JOIN licenses l ON l.id=d.license_id JOIN users u ON u.id=l.user_id
            WHERE d.license_id=$id ORDER BY d.bound_at,d.instance_id;
            """, ("$id", id), ("$cutoff", Now - _options.OnlineTimeoutSeconds), ("$now", Now)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) devices.Add(new(reader.GetString(0), Utc(reader.GetInt64(1)), reader.GetInt64(2) == 1));
        return license with { Devices = devices };
    }

    private static UserDto GetUser(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var command = Command(db, tx, "SELECT id,username,enabled,created_at FROM users WHERE id=$id AND is_admin=0;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new ApiException(404, "user_not_found", "The application user was not found.");
        return ReadUser(reader);
    }

    private static UserDto ReadUser(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) == 1, Utc(reader.GetInt64(3)));

    private static void InsertSession(SqliteConnection db, SqliteTransaction tx, string token, string kind,
        string userId, string? licenseId, string? instanceId, long expires)
    {
        PruneOldSessions(db, tx);
        Execute(db, tx, """
            INSERT INTO sessions(token_hash,kind,user_id,license_id,instance_id,created_at,expires_at)
            VALUES($hash,$kind,$user,$license,$instance,$now,$expires);
            """, ("$hash", HashToken(token)), ("$kind", kind), ("$user", userId),
            ("$license", licenseId), ("$instance", instanceId), ("$now", Now), ("$expires", expires));
    }

    private static void PruneOldSessions(SqliteConnection db, SqliteTransaction? tx) =>
        Execute(db, tx, "DELETE FROM sessions WHERE expires_at<$cutoff OR (revoked_at IS NOT NULL AND revoked_at<$cutoff);",
            ("$cutoff", Now - 7 * 86400L));

    private static void Audit(SqliteConnection db, SqliteTransaction? tx, string action, string subject, string actor) =>
        Execute(db, tx, "INSERT INTO audit(occurred_at,action,subject,actor) VALUES($now,$action,$subject,$actor);",
            ("$now", Now), ("$action", action), ("$subject", subject), ("$actor", actor));

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var command = Command(db, tx, sql, args);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var command = Command(db, tx, sql, args);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public static string ValidateUsername(string? value)
    {
        var username = value?.Trim() ?? "";
        if (!UsernamePattern().IsMatch(username))
            throw Invalid("Username must be 3–64 ASCII letters/digits or . _ @ -, and start with a letter or digit.");
        return username;
    }

    public static void ValidatePassword(string? password)
    {
        if (password is null || password.Length is < 12 or > 256 || string.IsNullOrWhiteSpace(password))
            throw Invalid("Password must contain 12–256 characters.");
    }

    private static string ValidateInstanceId(string? value)
    {
        if (value is null || !InstancePattern().IsMatch(value))
            throw Invalid("instance_id must contain 1–128 letters/digits or . _ : @ -.");
        return value;
    }

    private static void ValidateDeviceLimit(int count)
    {
        if (count is < 1 or > 100) throw Invalid("maxDevices must be between 1 and 100.");
    }

    private static string HashLicense(string? key)
    {
        var normalized = key?.Trim().ToUpperInvariant() ?? "";
        // Hash even an incorrectly formatted value; never make a random value valid.
        if (normalized.Length > 256) normalized = "";
        return Hash("license:" + normalized);
    }
    private static string HashToken(string token) => Hash("session:" + token);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static string NormalizeUsername(string username) => username.ToUpperInvariant();
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string NewSecret(string prefix) => prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static DateTimeOffset Utc(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);
    private static ApiException Invalid(string message) => new(400, "invalid_request", message);
    private static ApiException Unauthorized() => new(401, "invalid_session", "The session is invalid, expired, or revoked. Sign in again.");
    private static string SafeSubject(string? username) => new((username ?? "").Take(64).Where(c => !char.IsControl(c)).ToArray());

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]{2,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
    [GeneratedRegex("^[\\p{L}\\p{Nd}._:@-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();

    private sealed record CredentialUser(string Id, string Username, string PasswordHash, bool Enabled,
        bool IsAdmin, int FailedAttempts, long? LockedUntil);
    private sealed record BotSessionRow(string UserId, string LicenseId, string InstanceId, long SessionExpires,
        bool Revoked, string Username, bool UserEnabled, bool IsAdmin, string LicenseUserId,
        string KeyHash, bool LicenseEnabled, long LicenseExpires, bool DeviceExists, bool Online,
        long LastSeen, int MaxDevices, int DeviceCount);
    private sealed class LoginLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
