using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiAiRobot.Services;

public enum LicenseFailureKind
{
    None,
    Validation,
    Unauthorized,
    Denied,
    Unavailable,
    InvalidResponse
}

public sealed record LicenseResult(bool Ok, string Message, LicenseFailureKind Failure = LicenseFailureKind.None);
public sealed record LicenseStatusResult(bool Ok, bool Runnable, string Status, string Reason,
    LicenseFailureKind Failure = LicenseFailureKind.None, int TimeoutSeconds = 0);
public sealed record LicenseHeartbeatResult(bool Ok, bool Online, string Reason, int TimeoutSeconds,
    LicenseFailureKind Failure = LicenseFailureKind.None);

/// <summary>
/// Client for the included licensing service. Bearer credentials are attached only to individual
/// requests to the configured origin. Bookmaker credentials are never part of this protocol.
/// </summary>
public class LicenseClient : IDisposable
{
    public const int DefaultPort = 5173;
    public const string DefaultHost = "127.0.0.1";
    private const int MaximumResponseBytes = 65536;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly Lazy<string> DefaultInstanceId = new(CreateInstanceId);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _sensitiveLock = new();
    private readonly LinkedList<string> _sensitiveValues = new();
    private string _token = "";
    private Uri? _tokenOrigin;

    // Source-compatible names used by the recovered application.
    public const int field_04000630 = DefaultPort;
    public static readonly string field_04000632 = "/api/bot/login";
    public static readonly string field_04000633 = "/api/bot/license/verify";
    public static readonly string field_04000634 = "/api/bot/license/heartbeat";
    public static readonly string field_04000635 = "/api/bot/license/offline";
    public static readonly string field_04000636 = "/api/bot/license/status";
    public static readonly string field_04000639 = "[license service]";
    public static string field_0400063C => DefaultInstanceId.Value;

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:5173/");
    public string Token => _token;
    public string InstanceId { get; }

    /// <param name="httpClient">Optional caller-owned client, useful for local protocol tests.
    /// A supplied client must disable automatic redirects when used with a real service.</param>
    /// <param name="instanceId">Optional stable device identifier. Not a license key.</param>
    public LicenseClient(HttpClient? httpClient = null, string? instanceId = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true
        }) { Timeout = RequestTimeout };
        InstanceId = string.IsNullOrWhiteSpace(instanceId) ? DefaultInstanceId.Value : instanceId.Trim();
    }

    /// <summary>
    /// An absolute URL supplies its own port; the separate port applies to a bare host.
    /// Bare remote hosts use HTTPS. Plain HTTP is allowed only for localhost or loopback IPs.
    /// Paths, credentials, queries, fragments and whitespace are rejected.
    /// </summary>
    public static bool TryCreateBaseUri(string host, int port, out Uri? baseUri, out string error)
    {
        baseUri = null;
        error = "";
        string value = (host ?? "").Trim();
        if (value.Length == 0)
        {
            error = "Enter the license server origin, for example http://127.0.0.1:5173 or https://licenses.example.com.";
            return false;
        }
        if (port < 1 || port > 65535)
        {
            error = "License server port must be between 1 and 65535.";
            return false;
        }
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.Contains('\\') ||
            value.Contains('?') || value.Contains('#'))
        {
            error = "Use a server origin without whitespace, a query or a fragment.";
            return false;
        }
        try
        {
            Uri? candidate;
            if (value.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out candidate) ||
                    (candidate.Scheme != Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttp))
                {
                    error = "The license server must use HTTPS, or HTTP on localhost for local setup.";
                    return false;
                }
            }
            else if (IPAddress.TryParse(value.Trim('[', ']'), out IPAddress? address))
            {
                candidate = new UriBuilder(IPAddress.IsLoopback(address) ? "http" : "https", address.ToString(), port).Uri;
            }
            else
            {
                if (!Uri.TryCreate("https://" + value, UriKind.Absolute, out candidate))
                {
                    error = "Enter a valid host name or IP address.";
                    return false;
                }
                if (candidate.UserInfo.Length != 0)
                {
                    error = "Do not include credentials in the server address.";
                    return false;
                }
                bool hasPort = value.StartsWith("[", StringComparison.Ordinal)
                    ? value.IndexOf("]:", StringComparison.Ordinal) >= 0
                    : value.Count(c => c == ':') == 1;
                candidate = new UriBuilder(IsLoopback(candidate.Host) ? "http" : "https",
                    candidate.Host, hasPort ? candidate.Port : port) { Path = candidate.AbsolutePath }.Uri;
            }
            if (candidate.Port < 1 || candidate.Port > 65535)
            {
                error = "License server port must be between 1 and 65535.";
                return false;
            }
            if (candidate.UserInfo.Length != 0 || candidate.Query.Length != 0 || candidate.Fragment.Length != 0 ||
                candidate.AbsolutePath != "/" || candidate.HostNameType == UriHostNameType.Unknown)
            {
                error = "Use only the server origin, without a path, username, password, query or fragment.";
                return false;
            }
            if (candidate.Scheme == Uri.UriSchemeHttp && !IsLoopback(candidate.Host))
            {
                error = "Remote license servers require HTTPS. HTTP is allowed only on localhost or a loopback IP.";
                return false;
            }
            baseUri = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
            return true;
        }
        catch (UriFormatException)
        {
            error = "Enter a valid server origin and port.";
            return false;
        }
        catch (ArgumentException)
        {
            error = "Enter a valid server origin and port.";
            return false;
        }
    }

    public bool TryConfigureEndpoint(string host, int port, out string error)
    {
        if (!TryCreateBaseUri(host, port, out Uri? uri, out error))
            return false;
        if (BaseUri != uri)
        {
            BaseUri = uri!;
            _token = "";
            _tokenOrigin = null;
        }
        return true;
    }

    public void ConfigureEndpoint(string host, int port = DefaultPort)
    {
        if (!TryConfigureEndpoint(host, port, out string error))
            throw new ArgumentException(error, nameof(host));
    }

    public async Task<LicenseResult> LoginAsync(string username, string password, string licenseKey,
        decimal balance = 0m, decimal profit = 0m, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _token = "";
        _tokenOrigin = null;
        Uri loginOrigin = BaseUri;
        RememberSensitive(password);
        RememberSensitive(licenseKey);
        if (string.IsNullOrWhiteSpace(username)) return Invalid("License username is empty.");
        if (string.IsNullOrWhiteSpace(password)) return Invalid("License password is empty.");
        if (string.IsNullOrWhiteSpace(licenseKey)) return Invalid("License key is empty.");
        WireResponse response = await PostAsync(field_04000632, new
        {
            username = username.Trim(),
            password,
            license_key = licenseKey.Trim(),
            instance_id = InstanceId,
            balance,
            profit
        }, authenticated: false, ct, loginOrigin).ConfigureAwait(false);
        if (!response.Ok) return new(false, response.Message, response.Failure);
        string? token = ReadString(response.Data, "token");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return InvalidResponse("The license server did not return a valid session token.");
        ct.ThrowIfCancellationRequested();
        if (BaseUri != loginOrigin)
            return Invalid("The license server changed during sign-in. Test the current settings again.");
        RememberSensitive(token);
        _tokenOrigin = loginOrigin;
        _token = token;
        return new(true, "Login successful.");
    }

    public async Task<LicenseResult> AuthenticateAndVerifyAsync(string username, string password, string licenseKey,
        decimal balance = 0m, decimal profit = 0m, CancellationToken ct = default)
    {
        LicenseResult login = await LoginAsync(username, password, licenseKey, balance, profit, ct).ConfigureAwait(false);
        if (!login.Ok) return login;
        LicenseResult verified = await VerifyAsync(licenseKey, ct).ConfigureAwait(false);
        if (!verified.Ok) _token = "";
        return verified;
    }

    public async Task<LicenseResult> VerifyAsync(string licenseKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LicenseResult? missing = ValidateSession(licenseKey);
        if (missing is not null) return missing;
        WireResponse response = await PostLicenseAsync(field_04000633, licenseKey, ct).ConfigureAwait(false);
        if (!response.Ok) return new(false, response.Message, response.Failure);
        if (!TryReadBoolean(response.Data, "valid", out bool valid))
            return InvalidResponse("The license server returned an invalid verification response.");
        return valid ? new(true, "License verified. This device is authorized.")
            : new(false, "This license is not active for this account and device.", LicenseFailureKind.Denied);
    }

    public async Task<LicenseStatusResult> GetStatusAsync(string licenseKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LicenseResult? missing = ValidateSession(licenseKey);
        if (missing is not null) return new(false, false, "", missing.Message, missing.Failure);
        WireResponse response = await PostLicenseAsync(field_04000636, licenseKey, ct).ConfigureAwait(false);
        if (!response.Ok) return new(false, false, "", response.Message, response.Failure);
        if (!TryReadBoolean(response.Data, "valid", out bool valid) ||
            !TryReadBoolean(response.Data, "enabled", out bool enabled))
            return new(false, false, "", "The license server returned an invalid status response.", LicenseFailureKind.InvalidResponse);
        bool runnable = valid && enabled;
        return new(true, runnable, runnable ? "active" : "disabled",
            runnable ? "" : "The license is disabled, expired or unavailable for this device.",
            runnable ? LicenseFailureKind.None : LicenseFailureKind.Denied, ReadTimeout(response.Data));
    }

    public async Task<LicenseHeartbeatResult> HeartbeatAsync(string licenseKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LicenseResult? missing = ValidateSession(licenseKey);
        if (missing is not null) return new(false, false, missing.Message, 0, missing.Failure);
        WireResponse response = await PostLicenseAsync(field_04000634, licenseKey, ct).ConfigureAwait(false);
        if (!response.Ok) return new(false, false, response.Message, 0, response.Failure);
        if (!TryReadBoolean(response.Data, "valid", out bool valid) ||
            !TryReadBoolean(response.Data, "online", out bool online))
            return new(false, false, "The license server returned an invalid heartbeat response.", 0, LicenseFailureKind.InvalidResponse);
        return valid && online ? new(true, true, "", ReadTimeout(response.Data))
            : new(true, false, "The license service no longer authorizes this device.", ReadTimeout(response.Data), LicenseFailureKind.Denied);
    }

    public async Task<LicenseResult> OfflineAsync(string licenseKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_token)) return new(true, "No active session.");
        LicenseResult? missing = ValidateSession(licenseKey);
        if (missing is not null) return missing;
        WireResponse response = await PostLicenseAsync(field_04000635, licenseKey, ct).ConfigureAwait(false);
        return response.Ok ? new(true, "Device marked offline.") : new(false, response.Message, response.Failure);
    }

    private Task<WireResponse> PostLicenseAsync(string path, string licenseKey, CancellationToken ct) =>
        PostAsync(path, new { license_key = licenseKey.Trim(), instance_id = InstanceId }, authenticated: true, ct);

    private async Task<WireResponse> PostAsync(string path, object payload, bool authenticated, CancellationToken ct, Uri? requestOrigin = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            Uri origin = requestOrigin ?? BaseUri;
            string token = _token;
            if (authenticated && (string.IsNullOrEmpty(token) || _tokenOrigin != origin))
                return WireResponse.Failed(LicenseFailureKind.Unauthorized, "Sign in to the configured license service first.");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path))
            {
                Content = JsonContent.Create(payload)
            };
            if (authenticated)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                return WireResponse.Failed(LicenseFailureKind.InvalidResponse,
                    "The license endpoint redirected the request. Configure the final service origin directly.");
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return WireResponse.Failed(LicenseFailureKind.InvalidResponse, "The license server returned an oversized response.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                    return WireResponse.Failed(LicenseFailureKind.InvalidResponse, "The license server returned an oversized response.");
                buffer.Write(chunk, 0, read);
            }
            ct.ThrowIfCancellationRequested();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            }
            catch (JsonException)
            {
                return WireResponse.Failed(response.IsSuccessStatusCode ? LicenseFailureKind.InvalidResponse : FailureForStatus(response.StatusCode),
                    response.IsSuccessStatusCode ? "The license server did not return a valid JSON response." : ErrorMessage(null, response.StatusCode));
            }
            using (document)
            {
                JsonElement root = document.RootElement;
                string? code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.Object ? ReadString(error, "code") : null;
                if (!response.IsSuccessStatusCode)
                    return WireResponse.Failed(FailureForStatus(response.StatusCode), ErrorMessage(code, response.StatusCode));
                if (!TryReadBoolean(root, "success", out bool success))
                    return WireResponse.Failed(LicenseFailureKind.InvalidResponse, "The license response is missing a boolean success value.");
                if (!success)
                    return WireResponse.Failed(LicenseFailureKind.Denied, ErrorMessage(code, HttpStatusCode.Forbidden));
                if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
                    return WireResponse.Failed(LicenseFailureKind.InvalidResponse, "The license response is missing its data object.");
                return new(true, data.Clone(), LicenseFailureKind.None, "");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return WireResponse.Failed(LicenseFailureKind.Unavailable, "The license server did not respond within 15 seconds.");
        }
        catch (HttpRequestException)
        {
            return WireResponse.Failed(LicenseFailureKind.Unavailable,
                "Cannot connect to the license server. Check its address, port, HTTPS certificate and whether the service is running.");
        }
        catch (IOException)
        {
            return WireResponse.Failed(LicenseFailureKind.Unavailable, "The license server connection was interrupted.");
        }
    }

    private LicenseResult? ValidateSession(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey)) return Invalid("License key is empty.");
        if (string.IsNullOrWhiteSpace(_token)) return new(false, "Sign in to the license service first.", LicenseFailureKind.Unauthorized);
        return null;
    }

    private static bool IsLoopback(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));
    private static LicenseResult Invalid(string message) => new(false, message, LicenseFailureKind.Validation);
    private static LicenseResult InvalidResponse(string message) => new(false, message, LicenseFailureKind.InvalidResponse);
    private static string? ReadString(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryReadBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement member) ||
            (member.ValueKind != JsonValueKind.True && member.ValueKind != JsonValueKind.False)) return false;
        value = member.GetBoolean();
        return true;
    }
    private static int ReadTimeout(JsonElement data) => data.TryGetProperty("online_timeout_seconds", out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int seconds) && seconds > 0 ? seconds : 0;
    private static LicenseFailureKind FailureForStatus(HttpStatusCode status) => (int)status switch
    {
        401 => LicenseFailureKind.Unauthorized,
        403 or 409 => LicenseFailureKind.Denied,
        408 or 429 => LicenseFailureKind.Unavailable,
        >= 500 => LicenseFailureKind.Unavailable,
        _ => LicenseFailureKind.InvalidResponse
    };
    private static string ErrorMessage(string? code, HttpStatusCode status) => code?.ToLowerInvariant() switch
    {
        "invalid_credentials" => "The license username or password is incorrect.",
        "license_not_found" or "not_found" => "The license key was not found. Check the key in the license manager.",
        "license_expired" or "expired" => "This license has expired. Renew it in the license manager.",
        "license_disabled" or "disabled" => "This license is disabled in the license manager.",
        "license_unassigned" or "unassigned" => "Assign this license to the correct user in the license manager.",
        "license_user_mismatch" or "license_bound" or "user_mismatch" => "This license belongs to a different user.",
        "device_limit" or "device_limit_reached" => "This license has reached its device limit. Review its devices in the license manager.",
        "device_reset" or "session_revoked" or "revoked" => "This device session was revoked. Sign in again after the administrator authorizes the device.",
        "token_expired" or "session_expired" or "invalid_session" => "The license session has expired or was revoked. Sign in again.",
        "user_disabled" => "This license account is disabled in the license manager.",
        "license_assignment_changed" => "This license was assigned to another account. Check the license manager.",
        "session_mismatch" => "The session does not match this license and device. Sign in again.",
        "invalid_request" => "The license request is incomplete or invalid. Check the account, key and service configuration.",
        "https_required" => "This license service requires HTTPS. Configure its HTTPS origin.",
        _ => (int)status switch
        {
            401 => "License sign-in was rejected or the session expired. Check the account credentials.",
            403 => "This account, license or device is not authorized. Check the license manager.",
            409 => "The license could not be assigned to this device. Check its device limit in the license manager.",
            404 or 405 => "The configured server does not provide the expected license API. Check the server origin and port.",
            408 or 429 => "The license server is temporarily unavailable or is limiting requests. Try again shortly.",
            >= 500 => "The license service is temporarily unavailable. Check the service and try again.",
            _ => "The license service rejected the request or returned an unsupported response."
        }
    };

    private void RememberSensitive(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_sensitiveLock)
        {
            // Keep current credentials and recent tokens for redaction without retaining an unbounded history.
            _sensitiveValues.Remove(value);
            _sensitiveValues.AddLast(value);
            while (_sensitiveValues.Count > 64) _sensitiveValues.RemoveFirst();
        }
    }

    public string SanitizeMessage(string message)
    {
        string safe = message ?? "";
        lock (_sensitiveLock)
            foreach (string value in _sensitiveValues.OrderByDescending(x => x.Length))
                safe = safe.Replace(value, "[redacted]", StringComparison.Ordinal);
        return safe.Replace(BaseUri.AbsoluteUri.TrimEnd('/'), field_04000639, StringComparison.OrdinalIgnoreCase)
            .Replace(BaseUri.Host, field_04000639, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateInstanceId()
    {
        string fallback = $"device-{Guid.NewGuid():N}";
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAiRobot");
            string path = Path.Combine(directory, "instance.id");
            Directory.CreateDirectory(directory);
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length is > 0 and <= 128 && !existing.Any(char.IsControl)) return existing;
            }
            File.WriteAllText(path, fallback);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback;
    }

    public void Dispose()
    {
        _token = "";
        lock (_sensitiveLock) _sensitiveValues.Clear();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    // Compatibility wrappers keep the recovered call sites and existing extension code working.
    public int Method_06000438() => BaseUri.Port;
    public string Method_0600043A() => BaseUri.AbsoluteUri.TrimEnd('/');
    public void Method_0600043B(string host, int port = DefaultPort) => ConfigureEndpoint(host, port);
    public bool Method_0600043C(string host, int port, out string error) => TryConfigureEndpoint(host, port, out error);
    public string Method_0600043E() => field_04000639;
    public string Method_0600043F(string message) => SanitizeMessage(message);
    public string Method_06000441() => Token;
    public async Task<(bool Ok, string Message)> Method_06000444(string username, string password, string key,
        decimal balance, decimal profit, string bookmakerUsername, string bookmakerPassword, CancellationToken ct = default)
    {
        LicenseResult result = await AuthenticateAndVerifyAsync(username, password, key, balance, profit, ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }
    public async Task<(bool Ok, string Message)> Method_06000445(string username, string password, string key,
        decimal balance, decimal profit, string bookmakerUsername, string bookmakerPassword, CancellationToken ct = default)
    {
        LicenseResult result = await LoginAsync(username, password, key, balance, profit, ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }
    public async Task<(bool Ok, string Message)> Method_06000448(string key, CancellationToken ct = default)
    {
        LicenseResult result = await VerifyAsync(key, ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }
    public async Task<(bool Online, string Reason, int TimeoutSeconds)> Method_06000449(string key, CancellationToken ct = default)
    {
        LicenseHeartbeatResult result = await HeartbeatAsync(key, ct).ConfigureAwait(false);
        return (result.Ok && result.Online, result.Reason, result.TimeoutSeconds);
    }
    public async Task<(bool Ok, bool Runnable, string Status, string Reason)> Method_0600044A(string key, CancellationToken ct = default)
    {
        LicenseStatusResult result = await GetStatusAsync(key, ct).ConfigureAwait(false);
        return (result.Ok || result.Failure == LicenseFailureKind.Denied, result.Runnable, result.Status, result.Reason);
    }
    public async Task Method_0600044C(string key, CancellationToken ct = default) => await OfflineAsync(key, ct).ConfigureAwait(false);

    private sealed record WireResponse(bool Ok, JsonElement Data, LicenseFailureKind Failure, string Message)
    {
        public static WireResponse Failed(LicenseFailureKind failure, string message) => new(false, default, failure, message);
    }
}
