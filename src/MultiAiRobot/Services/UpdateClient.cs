using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiAiRobot.Models;

namespace MultiAiRobot.Services;


public sealed record UpdateInfo
{
    public Version Version { get; set; }
    public string Url { get; set; }
    public string Sha256 { get; set; }
    public bool Mandatory { get; set; }
    public string Notes { get; set; }

    public UpdateInfo(Version version, string url, string sha256, bool mandatory, string notes)
    {
        Version = version;
        Url = url;
        Sha256 = sha256;
        Mandatory = mandatory;
        Notes = notes;
    }

    // Compatibility methods used by the recovered UI.
    public Version Method_06000608() => Version;
    public void Method_06000609(Version value) => Version = value;
    public string Method_0600060A() => Url;
    public void Method_0600060B(string value) => Url = value;
    public string Method_0600060C() => Sha256;
    public void Method_0600060D(string value) => Sha256 = value;
    public bool Method_0600060E() => Mandatory;
    public void Method_0600060F(bool value) => Mandatory = value;
    public string Method_06000610() => Notes;
    public void Method_06000611(string value) => Notes = value;
    public UpdateInfo Method_06000619() => this with { };
    public void Method_0600061B(out Version version, out string url, out string sha256, out bool mandatory, out string notes)
    {
        version = Version; url = Url; sha256 = Sha256; mandatory = Mandatory; notes = Notes;
    }
}

public sealed class UpdateClient
{
	public string? LastCheckMessage { get; private set; }

    private static readonly HttpClient DefaultHttpClient = Method_0600061C();
    private readonly HttpClient field_04000892;

    public UpdateClient(HttpClient? httpClient = null)
    {
        field_04000892 = httpClient ?? DefaultHttpClient;
    }

	[CompilerGenerated]
	private event Action<string, LogLevel>? Log;

	private static readonly string field_04000894 = """
param([int]$BotPid, [string]$Stage, [string]$AppDir, [string]$Exe)
$ErrorActionPreference = 'Stop'
$work = Split-Path -Parent $Stage
$backup = Join-Path $work 'backup'
$failureLog = Join-Path $work 'install-error.txt'
$added = New-Object 'System.Collections.Generic.List[string]'
$copyStarted = $false
try {
    try { Wait-Process -Id $BotPid -Timeout 90 -ErrorAction Stop }
    catch { if (Get-Process -Id $BotPid -ErrorAction SilentlyContinue) { throw 'The app is still running. No update was installed.' } }
    $prefix = $Stage.TrimEnd([char]92) + [char]92
    $files = @(Get-ChildItem -LiteralPath $Stage -File -Recurse)
    if ($files.Count -eq 0) { throw 'The update folder is empty.' }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($prefix.Length)
        $destination = Join-Path $AppDir $relative
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $saved = Join-Path $backup $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $saved -Force
        } else { $added.Add($destination) }
    }
    $copyStarted = $true
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($prefix.Length)
        $destination = Join-Path $AppDir $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        # Identical runtime DLLs can remain loaded by the local license server.
        if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
            ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq
             (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)) { continue }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    Start-Process -FilePath $Exe -WorkingDirectory $AppDir
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
} catch {
    $problem = $_.Exception.Message
    if ($copyStarted) {
        try {
            foreach ($path in $added) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
            $backupPrefix = $backup.TrimEnd([char]92) + [char]92
            foreach ($saved in Get-ChildItem -LiteralPath $backup -File -Recurse) {
                $destination = Join-Path $AppDir $saved.FullName.Substring($backupPrefix.Length)
                if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
                    ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq
                     (Get-FileHash -LiteralPath $saved.FullName -Algorithm SHA256).Hash)) { continue }
                Copy-Item -LiteralPath $saved.FullName -Destination $destination -Force
            }
            $problem += "`r`nPrevious files were restored."
        } catch { $problem += "`r`nSome files could not be restored: " + $_.Exception.Message }
    }
    $problem += "`r`nUpdate staging and backup were preserved at: " + $work
    $problem | Set-Content -LiteralPath $failureLog -Encoding UTF8
    # Do not restart a possibly incomplete installation or delete recovery files.
    exit 1
}
""";

	private static HttpClient Method_0600061C()
	{
		return new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate),
			AllowAutoRedirect = false,
			Proxy = null,
			UseProxy = false
		})
		{
			Timeout = TimeSpan.FromMinutes(5L)
		};
	}

    public void Method_0600061D(Action<string, LogLevel>? value) => Log += value;
    public void Method_0600061E(Action<string, LogLevel>? value) => Log -= value;

	private static string Method_0600061F(string P_0, int P_1)
	{
		string host = (P_0 ?? "").Trim();
        Uri origin;
        if (host.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out origin!))
                throw new InvalidOperationException("Invalid update server address.");
        }
        else
        {
            bool local = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
            if (!Uri.TryCreate($"{(local ? "http" : "https")}://{host}:{P_1}", UriKind.Absolute, out origin!))
                throw new InvalidOperationException("Invalid update server address.");
        }
        if ((origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Use an HTTPS server origin, or HTTP on localhost.");
        return origin.GetLeftPart(UriPartial.Authority);
	}

	public async Task<UpdateInfo?> Method_06000620(string P_0, int P_1, string? bearer, Version P_3, CancellationToken P_4 = default(CancellationToken))
	{
        LastCheckMessage = null;
		if (string.IsNullOrWhiteSpace(P_0))
		{
            LastCheckMessage = "Configure your own update server first.";
			return null;
		}
		try
		{
			using HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, Method_0600061F(P_0, P_1) + "/api/bot/version");
			if (!string.IsNullOrWhiteSpace(bearer))
			{
				httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
			}
			using HttpResponseMessage httpResponseMessage = await field_04000892.SendAsync(httpRequestMessage, P_4);
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
                LastCheckMessage = httpResponseMessage.StatusCode == HttpStatusCode.NotFound
                    ? "This license server does not provide an update feed. Install new releases from your own trusted build package."
                    : $"The update check failed (HTTP {(int)httpResponseMessage.StatusCode}). Your version could not be compared.";
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await httpResponseMessage.Content.ReadAsStringAsync(P_4));
			JsonElement jsonElement = jsonDocument.RootElement;
            if (jsonElement.ValueKind != JsonValueKind.Object ||
                (jsonElement.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True))
            {
                LastCheckMessage = "The update server did not return a successful manifest.";
                return null;
            }
			if (jsonElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object)
			{
				jsonElement = value;
			}
			string? text = Method_06000623(jsonElement, "version");
			string text2 = Method_06000623(jsonElement, "url") ?? "";
			if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text, out Version? result))
			{
                LastCheckMessage = "The server returned an invalid update manifest.";
				return null;
			}
			if (result <= P_3)
			{
                LastCheckMessage = "Your installed version is up to date according to this server.";
				return null;
			}
			string text3 = (Method_06000623(jsonElement, "sha256") ?? Method_06000623(jsonElement, "sha") ?? "").Trim();
            string hash = text3.Replace("-", "");
            Uri origin = new Uri(Method_0600061F(P_0, P_1));
            if (string.IsNullOrWhiteSpace(text2) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) ||
                !Uri.TryCreate(origin, text2, out Uri? packageUri) ||
                (packageUri.Scheme != Uri.UriSchemeHttps && !(packageUri.Scheme == Uri.UriSchemeHttp && packageUri.IsLoopback)) ||
                !string.IsNullOrEmpty(packageUri.UserInfo))
            {
                LastCheckMessage = "The update manifest has no valid package URL and SHA-256 checksum. It was rejected.";
                return null;
            }
			bool flag = jsonElement.TryGetProperty("mandatory", out var value2) && value2.ValueKind == JsonValueKind.True;
			string text4 = Method_06000623(jsonElement, "notes") ?? Method_06000623(jsonElement, "message") ?? "";
			return new UpdateInfo(result, text2, text3, flag, text4);
		}
		catch (OperationCanceledException) when (P_4.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
            LastCheckMessage = "The update server could not be checked. " + ex2.Message;
			Log?.Invoke(LastCheckMessage, LogLevel.Warning);
			return null;
		}
	}

	public async Task<string> Method_06000621(UpdateInfo P_0, string P_1, int P_2, string? bearer, CancellationToken P_4 = default(CancellationToken))
	{
        string expectedHash = P_0.Method_0600060C().Replace("-", "").Trim();
        if (expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("A valid SHA-256 checksum is required before an update can be downloaded.");
        Uri origin = new Uri(Method_0600061F(P_1, P_2));
        Uri download = new Uri(origin, P_0.Method_0600060A());
        if (download.Scheme != Uri.UriSchemeHttps && !(download.Scheme == Uri.UriSchemeHttp && download.IsLoopback))
            throw new InvalidOperationException("Update packages must use HTTPS, or HTTP on localhost.");
        if (!string.IsNullOrEmpty(download.UserInfo))
            throw new InvalidOperationException("Update URLs must not contain embedded credentials.");
        bool sameOrigin = download.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority);
        string requestUri = download.AbsoluteUri;
        string text = Path.Combine(Path.GetTempPath(), "MultiAiRobotUpdate", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		string text2 = Path.Combine(text, $"update-{P_0.Method_06000608()}.zip");
		string text3 = Path.Combine(text, "stage");
		Directory.CreateDirectory(text3);
		Log?.Invoke($"Downloading update {P_0.Method_06000608()} …", LogLevel.Info);
		using (HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri))
		{
			if (sameOrigin && !string.IsNullOrWhiteSpace(bearer))
			{
				httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
			}
			using HttpResponseMessage httpResponseMessage = await field_04000892.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, P_4);
			httpResponseMessage.EnsureSuccessStatusCode();
			await using FileStream stream = File.Create(text2);
			await httpResponseMessage.Content.CopyToAsync(stream, P_4);
		}
		if (!string.IsNullOrWhiteSpace(P_0.Method_0600060C()))
		{
			string text4 = await Method_06000624(text2, P_4);
			if (!string.Equals(text4, P_0.Method_0600060C().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"SHA-256 mismatch — refusing to install (expected {P_0.Method_0600060C()}, got {text4}).");
			}
			Log?.Invoke("Update integrity verified (SHA-256).", LogLevel.Info);
		}
		else
		{
            throw new InvalidOperationException("The update has no SHA-256 checksum.");
		}
		ZipFile.ExtractToDirectory(text2, text3, overwriteFiles: true);
		return Path.GetDirectoryName(Directory.EnumerateFiles(text3, "MultiAiRobot.exe", SearchOption.AllDirectories).FirstOrDefault() ?? throw new InvalidOperationException("Downloaded update does not contain MultiAiRobot.exe."))
            ?? throw new InvalidOperationException("The update has no installation folder.");
	}

	public void Method_06000622(string P_0)
	{
		string text = AppContext.BaseDirectory.TrimEnd('\\');
		string item = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(text, "MultiAiRobot.exe");
		int processId = Environment.ProcessId;
        string text2 = Path.Combine(Path.GetDirectoryName(P_0)!, "updater.ps1");
		File.WriteAllText(text2, field_04000894);
		Process.Start(new ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetTempPath(),
			ArgumentList = 
			{
				"-NoProfile",
				"-ExecutionPolicy",
				"Bypass",
				"-WindowStyle",
				"Hidden",
				"-File",
				text2,
				"-BotPid",
				processId.ToString(),
				"-Stage",
				P_0,
				"-AppDir",
				text,
				"-Exe",
				item
			}
		});
		Log?.Invoke("Updater launched — the bot will restart on version " + P_0 + ".", LogLevel.Info);
	}

	private static string? Method_06000623(JsonElement P_0, string P_1)
	{
		if (!P_0.TryGetProperty(P_1, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static async Task<string> Method_06000624(string P_0, CancellationToken P_1)
	{
		string result;
		await using (FileStream inputStream = File.OpenRead(P_0))
		{
			using SHA256 sHA = SHA256.Create();
			result = Convert.ToHexString(await sHA.ComputeHashAsync(inputStream, P_1));
		}
		return result;
	}
}

