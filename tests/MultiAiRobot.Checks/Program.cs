using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiAiRobot.Services;

// Exercises the real persisted service and the same LicenseClient.cs compiled into WPF.
// The service runs only on an ephemeral loopback port with a disposable database.
var checks = new List<CheckResult>();
string serverDll = GetArgument("--server") ?? Path.GetFullPath(
    "src/MultiAiRobot.LicenseServer/bin/Release/net10.0/MultiAiRobot.LicenseServer.dll");
string? reportFile = GetArgument("--report");
string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    ?? Environment.ProcessPath ?? "dotnet";
string dataDirectory = Path.Combine(Path.GetTempPath(), "MultiAiRobotChecks-" + Guid.NewGuid().ToString("N"));
string adminPassword = "Check!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
string userPassword = "User!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
string changedPassword = "Reset!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
var serverOutput = new List<string>();
Process? server = null;

try
{
    if (!File.Exists(serverDll))
        throw new FileNotFoundException("Build the license server before running these checks.", serverDll);
    Directory.CreateDirectory(dataDirectory);
    using (Process init = StartServerProcess(["init-admin", "--username", "check-admin", "--data-dir", dataDirectory], true))
    {
        await init.WaitForExitAsync();
        Require(init.ExitCode == 0, "First administrator can be initialized without default credentials");
    }
    int port;
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    string origin = $"http://127.0.0.1:{port}";
    using var http = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(12) };
    await StartServiceAsync();

    var setup = await Send(HttpMethod.Get, "/api/admin/setup-status");
    Require(setup.IsSuccess && !setup.Data.GetProperty("requiresSetup").GetBoolean(), "Setup state reflects the persisted administrator");
    Require((await Send(HttpMethod.Get, "/api/admin/users")).Status == 401, "Anonymous callers cannot read accounts");
    Require(!(await Send(HttpMethod.Post, "/api/admin/login", new { username = "check-admin", password = "wrong-password" })).IsSuccess,
        "Wrong administrator password is rejected");
    string adminToken = await AdminLogin();
    Require(adminToken.Length >= 32, "Administrator login issues an opaque session token");

    var createdUser = await Send(HttpMethod.Post, "/api/admin/users", new { username = "check-user", password = userPassword }, adminToken);
    Require(createdUser.IsSuccess, "Administrator can create a license account");
    string userId = Id(createdUser.Data);
    Require(!(await Send(HttpMethod.Post, "/api/admin/users", new { username = "check-user", password = userPassword }, adminToken)).IsSuccess,
        "Duplicate usernames are rejected");
    var secondUser = await Send(HttpMethod.Post, "/api/admin/users", new { username = "other-user", password = userPassword }, adminToken);
    Require(secondUser.IsSuccess, "A second independent account can be created");

    var issued = await Issue("check-user", 30, 1);
    string key = issued.GetProperty("key").GetString()!;
    string licenseId = Id(issued.GetProperty("license"));
    Require(!string.IsNullOrWhiteSpace(key) && key.Length >= 32, "License issuance returns a new key once");
    var listing = await Send(HttpMethod.Get, "/api/admin/licenses", token: adminToken);
    Require(listing.IsSuccess && !listing.Body.GetRawText().Contains(key, StringComparison.Ordinal),
        "License listing exposes a prefix rather than the full key");
    Require(!(await BotLogin("check-user", userPassword, "not-an-issued-key", "device-a")).IsSuccess,
        "An unissued key is rejected");
    Require(!(await BotLogin("check-user", "wrong-password", key, "device-a")).IsSuccess,
        "An issued key does not bypass account authentication");
    Require(!(await BotLogin("other-user", userPassword, key, "device-a")).IsSuccess,
        "A key cannot be used by a different account");

    var login = await BotLogin("check-user", userPassword, key, "device-a");
    Require(login.IsSuccess && HasToken(login), "The first licensed device can log in");
    string botToken = Token(login);
    var request = new { license_key = key, instance_id = "device-a" };
    Require(IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", request, botToken), "valid"),
        "The recovered verification contract accepts an issued active license");
    Require(!(await Send(HttpMethod.Post, "/api/bot/license/verify", request)).IsSuccess,
        "License verification requires a session token");
    Require(!(await Send(HttpMethod.Get, "/api/admin/licenses", token: botToken)).IsSuccess,
        "A bot token cannot access administration APIs");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", request, adminToken), "valid"),
        "An administrator token cannot impersonate a licensed bot session");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify",
        new { license_key = key, instance_id = "device-forged" }, botToken), "valid"),
        "Session validation checks the bound device identity");
    Require(!(await BotLogin("check-user", userPassword, key, "device-b")).IsSuccess,
        "The configured device limit is enforced");

    Require((await Send(HttpMethod.Patch, $"/api/admin/licenses/{licenseId}", new { enabled = false }, adminToken)).IsSuccess,
        "An administrator can disable a license");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", request, botToken), "valid"),
        "Disabling a license invalidates an existing bot session");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/status", request, botToken), "valid"),
        "The status endpoint does not authorize a disabled license");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/heartbeat", request, botToken), "online"),
        "Heartbeat does not authorize a disabled license");
    Require((await Send(HttpMethod.Patch, $"/api/admin/licenses/{licenseId}", new { enabled = true }, adminToken)).IsSuccess,
        "A license can be re-enabled");
    login = await BotLogin("check-user", userPassword, key, "device-a");
    Require(login.IsSuccess, "An enabled license can authenticate again");
    botToken = Token(login);

    Require((await Send(HttpMethod.Patch, $"/api/admin/licenses/{licenseId}",
        new { expiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1) }, adminToken)).IsSuccess,
        "An administrator can set a license expiry");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", request, botToken), "valid"),
        "An expired license cannot verify");
    Require(!(await BotLogin("check-user", userPassword, key, "device-a")).IsSuccess,
        "An expired license cannot open a new session");
    Require((await Send(HttpMethod.Patch, $"/api/admin/licenses/{licenseId}",
        new { expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) }, adminToken)).IsSuccess,
        "A license can be renewed");
    botToken = Token(await BotLogin("check-user", userPassword, key, "device-a"));

    Require((await Send(HttpMethod.Delete, $"/api/admin/licenses/{licenseId}/devices/device-a", token: adminToken)).IsSuccess,
        "An administrator can release a device binding");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", request, botToken), "valid"),
        "Releasing a device invalidates its existing session");
    botToken = Token(await BotLogin("check-user", userPassword, key, "device-b"));
    Require(botToken.Length > 0, "A replacement device can use a released seat");
    var requestB = new { license_key = key, instance_id = "device-b" };
    Require((await Send(HttpMethod.Post, "/api/bot/license/offline", requestB, botToken)).IsSuccess,
        "A client can report an orderly disconnect");
    Require(!(await BotLogin("check-user", userPassword, key, "device-c")).IsSuccess,
        "Going offline does not silently remove a device binding");
    botToken = Token(await BotLogin("check-user", userPassword, key, "device-b"));

    Require((await Send(HttpMethod.Patch, $"/api/admin/users/{userId}", new { enabled = false }, adminToken)).IsSuccess,
        "An administrator can disable an account");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", requestB, botToken), "valid"),
        "Disabling an account invalidates its license sessions");
    await Send(HttpMethod.Patch, $"/api/admin/users/{userId}", new { enabled = true }, adminToken);
    botToken = Token(await BotLogin("check-user", userPassword, key, "device-b"));
    Require((await Send(HttpMethod.Patch, $"/api/admin/users/{userId}", new { password = changedPassword }, adminToken)).IsSuccess,
        "An administrator can reset an account password");
    Require(!IsTrue(await Send(HttpMethod.Post, "/api/bot/license/verify", requestB, botToken), "valid"),
        "A password reset revokes existing sessions");
    Require(!(await BotLogin("check-user", userPassword, key, "device-b")).IsSuccess,
        "The old account password stops working after reset");
    userPassword = changedPassword;
    Require((await BotLogin("check-user", userPassword, key, "device-b")).IsSuccess,
        "The new account password works");

    var concurrentIssued = await Issue("check-user", 30, 1);
    string concurrentKey = concurrentIssued.GetProperty("key").GetString()!;
    var concurrent = await Task.WhenAll(
        BotLogin("check-user", userPassword, concurrentKey, "parallel-a"),
        BotLogin("check-user", userPassword, concurrentKey, "parallel-b"));
    Require(concurrent.Count(x => x.IsSuccess && HasToken(x)) == 1,
        "Concurrent login requests cannot over-allocate a single device seat");

    var clientIssued = await Issue("check-user", 30, 1);
    string clientKey = clientIssued.GetProperty("key").GetString()!;
    using (var clientHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
        var client = new LicenseClient(clientHttp, "desktop-contract-check");
        client.Method_0600043B("127.0.0.1", port);
        var auth = await client.Method_06000444("check-user", userPassword, clientKey, 0m, 0m,
            "BOOKMAKER-USERNAME-MUST-NOT-BE-SENT", "BOOKMAKER-PASSWORD-MUST-NOT-BE-SENT");
        Require(auth.Ok, "The actual WPF license client authenticates against the new server");
        Require((await client.Method_0600044A(clientKey)).Runnable, "The actual client reads active status");
        Require((await client.Method_06000449(clientKey)).Online, "The actual client sends a valid heartbeat");
        await client.Method_0600044C(clientKey);
    }

    var updater = new UpdateClient();
    Require(await updater.Method_06000620("127.0.0.1", port, null, new Version(1, 2, 0)) == null
        && updater.LastCheckMessage?.Contains("does not provide", StringComparison.Ordinal) == true,
        "A missing update feed is reported accurately");
    bool unsignedRejected = false;
    try { await updater.Method_06000621(new UpdateInfo(new Version(9, 0), "/missing.zip", "", false, ""), "127.0.0.1", port, null); }
    catch (InvalidOperationException) { unsignedRejected = true; }
    Require(unsignedRejected, "An update without a SHA-256 checksum is rejected before download");

    await CheckClientContractAsync();
    var audit = await Send(HttpMethod.Get, "/api/admin/audit", token: adminToken);
    Require(audit.IsSuccess && audit.Data.ValueKind == JsonValueKind.Array && audit.Data.GetArrayLength() > 0,
        "Administrative changes create an audit trail");
    Require(!audit.Body.GetRawText().Contains(key, StringComparison.Ordinal)
        && !audit.Body.GetRawText().Contains(userPassword, StringComparison.Ordinal),
        "The audit feed does not expose full license keys or passwords");

    await StopServiceAsync();
    string[] storedFiles = Directory.GetFiles(dataDirectory, "*", SearchOption.AllDirectories);
    bool secretFound = storedFiles.Any(path =>
    {
        string content = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        return content.Contains(adminPassword, StringComparison.Ordinal) || content.Contains(userPassword, StringComparison.Ordinal)
            || content.Contains(key, StringComparison.Ordinal) || content.Contains(botToken, StringComparison.Ordinal);
    });
    Require(!secretFound, "Database storage does not contain raw passwords, license keys, or bearer tokens");
    await StartServiceAsync();
    adminToken = await AdminLogin();
    Require((await BotLogin("check-user", userPassword, key, "device-b")).IsSuccess,
        "Issued licenses, accounts, and device bindings survive a server restart");
    Require((await Send(HttpMethod.Post, "/api/admin/logout", token: adminToken)).IsSuccess,
        "The administrator can sign out");
    Require(!(await Send(HttpMethod.Get, "/api/admin/licenses", token: adminToken)).IsSuccess,
        "Signing out revokes the administrator token");

    // Check throttling last so it cannot interfere with earlier acceptance checks.
    var burst = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
        Send(HttpMethod.Post, "/api/admin/login", new { username = "", password = "" })));
    Require(burst.Any(response => response.Status == 429), "Authentication requests are rate limited");

    async Task<JsonElement> Issue(string username, int days, int maxDevices)
    {
        var result = await Send(HttpMethod.Post, "/api/admin/licenses", new { username, days, maxDevices }, adminToken);
        if (!result.IsSuccess) throw new InvalidOperationException($"License issuance failed: HTTP {result.Status}.");
        return result.Data;
    }
    async Task<string> AdminLogin()
    {
        var result = await Send(HttpMethod.Post, "/api/admin/login", new { username = "check-admin", password = adminPassword });
        if (!result.IsSuccess || !HasToken(result)) throw new InvalidOperationException($"Administrator login failed: HTTP {result.Status}.");
        return Token(result);
    }
    Task<ApiResult> BotLogin(string username, string password, string licenseKey, string instanceId) =>
        Send(HttpMethod.Post, "/api/bot/login", new { username, password, license_key = licenseKey, instance_id = instanceId, balance = 0m, profit = 0m });

    async Task<ApiResult> Send(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body != null) message.Content = JsonContent.Create(body);
        if (token != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(message);
        string text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return new ApiResult((int)response.StatusCode, document.RootElement.Clone());
    }
    async Task StartServiceAsync()
    {
        server = StartServerProcess(["--urls", origin, "--data-dir", dataDirectory], false);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException("The license service exited during startup.");
            try
            {
                using var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The license service did not become healthy.");
    }
}
catch (Exception ex)
{
    checks.Add(new CheckResult("Acceptance checks completed", false, ex.Message));
    Console.Error.WriteLine("FAIL: " + ex.Message);
    lock (serverOutput)
        foreach (string line in serverOutput.TakeLast(25)) Console.Error.WriteLine(line);
}
finally
{
    await StopServiceAsync();
    try { Directory.Delete(dataDirectory, true); } catch (IOException) { }
    var report = new { checkedAtUtc = DateTimeOffset.UtcNow, passed = checks.Count(x => x.Passed),
        failed = checks.Count(x => !x.Passed), checks };
    if (reportFile != null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"RESULT: {report.passed} passed; {report.failed} failed.");
}
return checks.Any(x => !x.Passed) ? 1 : 0;

string? GetArgument(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
void Require(bool condition, string name)
{
    checks.Add(new CheckResult(name, condition, condition ? null : "Assertion failed"));
    Console.WriteLine((condition ? "PASS: " : "FAIL: ") + name);
    if (!condition) throw new InvalidOperationException(name);
}
Process StartServerProcess(string[] arguments, bool initialize)
{
    var info = new ProcessStartInfo(dotnet)
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(serverDll))!
    };
    info.ArgumentList.Add(Path.GetFullPath(serverDll));
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    if (initialize) info.Environment["MULTIAIROBOT_ADMIN_PASSWORD"] = adminPassword;
    info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    // Keep the production limit unchanged; the acceptance run makes many valid logins in one minute.
    info.Environment["LicenseServer__LoginRequestsPerMinute"] = "60";
    var process = new Process { StartInfo = info };
    process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (serverOutput) serverOutput.Add(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (serverOutput) serverOutput.Add(e.Data); };
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}
async Task StopServiceAsync()
{
    if (server == null) return;
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync();
    server.Dispose();
    server = null;
}
static string Id(JsonElement value) => value.GetProperty("id").ToString();
static bool HasToken(ApiResult result) => result.Data.ValueKind == JsonValueKind.Object
    && result.Data.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(token.GetString());
static string Token(ApiResult result) => HasToken(result) ? result.Data.GetProperty("token").GetString()!
    : throw new InvalidOperationException($"A login response has no token (HTTP {result.Status}).");
static bool IsTrue(ApiResult result, string property) => result.IsSuccess && result.Data.ValueKind == JsonValueKind.Object
    && result.Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

async Task CheckClientContractAsync()
{
    Require(!LicenseClient.TryCreateBaseUri("http://licenses.example.invalid", 5173, out _, out _),
        "The desktop client rejects unencrypted remote license endpoints");
    Require(LicenseClient.TryCreateBaseUri("https://licenses.example.invalid", 5173, out var secure, out _)
        && secure?.Scheme == "https", "The desktop client preserves HTTPS");
    Require(!LicenseClient.TryCreateBaseUri("https://user:password@licenses.example.invalid", 5173, out _, out _),
        "License endpoints cannot contain embedded credentials");
    Require(!LicenseClient.TryCreateBaseUri("https://licenses.example.invalid/admin?token=x", 5173, out _, out _),
        "License endpoints must be clean origins");
    Require(!LicenseClient.TryCreateBaseUri("https://licenses.example.invalid:0", 5173, out _, out _),
        "Explicit invalid endpoint ports are rejected");

    var handler = new RecordingHandler(
        "{\"success\":true,\"data\":{\"token\":\"test-session-token\"}}",
        "{\"success\":true,\"data\":{\"valid\":true}}");
    using var http = new HttpClient(handler);
    var client = new LicenseClient(http, "contract-device");
    client.Method_0600043B("https://licenses.example.invalid", 443);
    var result = await client.Method_06000444("account", "account-password", "issued-key", 12m, 1m,
        "PRIVATE-BOOKIE-USERNAME", "PRIVATE-BOOKIE-PASSWORD");
    Require(result.Ok, "The desktop client understands the successful login and verification JSON contract");
    Require(handler.Requests.Count == 2 && !handler.Requests[0].Body.Contains("bookmaker", StringComparison.OrdinalIgnoreCase)
        && !handler.Requests[0].Body.Contains("PRIVATE-BOOKIE", StringComparison.Ordinal),
        "License requests omit bookmaker usernames and passwords");
    Require(handler.Requests[0].Authorization == null && handler.Requests[1].Authorization == "Bearer test-session-token",
        "The bearer token is sent only after login");
    client.Method_0600043B("https://other.example.invalid", 443);
    Require(string.IsNullOrEmpty(client.Method_06000441()), "Changing the license server clears the previous session token");

    var malformedHandler = new RecordingHandler(
        "{\"success\":true,\"data\":{\"token\":\"test-token\"}}", "{\"success\":true,\"data\":{\"valid\":\"true\"}}");
    using var malformedHttp = new HttpClient(malformedHandler);
    var malformedClient = new LicenseClient(malformedHttp, "contract-device");
    Require(!(await malformedClient.Method_06000444("account", "password", "key", 0m, 0m, "", "")).Ok,
        "The client rejects string-valued validity instead of accepting it as a Boolean");

    var failureHandler = new RecordingHandler("{\"success\":true,\"data\":{\"token\":\"test-token\"}}", "{\"success\":true,\"data\":{\"valid\":true}}", "{\"success\":true,\"data\":{\"valid\":false,\"online\":false,\"reason\":\"disabled\"}}");
    using var failureHttp = new HttpClient(failureHandler);
    var failureClient = new LicenseClient(failureHttp, "contract-device");
    await failureClient.Method_06000444("account", "password", "key", 0m, 0m, "", "");
    Require(!(await failureClient.Method_06000449("key")).Online, "The client treats a negative heartbeat as a license failure");

    using var failedManifestHttp = new HttpClient(new RecordingHandler(
        "{\"success\":false,\"data\":{\"version\":\"9.0.0\",\"url\":\"/update.zip\",\"sha256\":\"" + new string('A', 64) + "\"}}"));
    var failedManifestUpdater = new UpdateClient(failedManifestHttp);
    Require(await failedManifestUpdater.Method_06000620("127.0.0.1", 5173, null, new Version(1, 2)) == null,
        "A failed update response cannot advertise an installable version");
    using var unsignedManifestHttp = new HttpClient(new RecordingHandler(
        "{\"success\":true,\"data\":{\"version\":\"9.0.0\",\"url\":\"/update.zip\",\"mandatory\":true}}"));
    var unsignedManifestUpdater = new UpdateClient(unsignedManifestHttp);
    Require(await unsignedManifestUpdater.Method_06000620("127.0.0.1", 5173, null, new Version(1, 2)) == null,
        "A mandatory update without a checksum is rejected before the UI can stop the bot");
}

record CheckResult(string Name, bool Passed, string? Detail);
record ApiResult(int Status, JsonElement Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
    public JsonElement Data => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("data", out var value) ? value : default;
}
record CapturedRequest(string Path, string Body, string? Authorization);
sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> queue = new(responses);
    public List<CapturedRequest> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath,
            request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString()));
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json") };
    }
}
