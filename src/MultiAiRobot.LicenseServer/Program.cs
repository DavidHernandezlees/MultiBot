using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using MultiAiRobot.LicenseServer;

try
{
    var cli = CommandLine.Parse(args);
    if (cli.Help)
    {
        CommandLine.PrintHelp();
        return 0;
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = cli.HostArguments,
        ContentRootPath = AppContext.BaseDirectory
    });
    var options = builder.Configuration.GetSection("LicenseServer").Get<ServerOptions>() ?? new();
    options.Validate();
    var dataDirectory = cli.DataDirectory ?? Environment.GetEnvironmentVariable("MULTIAIROBOT_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiAiRobot", "LicenseServer");
    var database = new LicenseDatabase(Path.GetFullPath(dataDirectory), options);
    database.Initialize();

    if (cli.InitializeAdministrator)
    {
        if (!database.RequiresSetup())
        {
            Console.Error.WriteLine("An administrator already exists. No account was changed.");
            return 2;
        }
        var username = cli.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Write("Administrator username: ");
            username = Console.ReadLine();
        }
        var password = Environment.GetEnvironmentVariable("MULTIAIROBOT_ADMIN_PASSWORD");
        if (password is null)
        {
            if (Console.IsInputRedirected)
                throw new InvalidOperationException("Use an interactive terminal or supply MULTIAIROBOT_ADMIN_PASSWORD for unattended initialization.");
            password = CommandLine.ReadPassword("Administrator password (12–256 characters): ");
            var confirmation = CommandLine.ReadPassword("Confirm password: ");
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                throw new InvalidOperationException("Passwords did not match. No account was created.");
        }
        Environment.SetEnvironmentVariable("MULTIAIROBOT_ADMIN_PASSWORD", null);
        database.CreateInitialAdministrator(username ?? "", password);
        Console.WriteLine("Administrator created. Start the server and sign in through the dashboard.");
        return 0;
    }

    // No insecure public binding is accepted. Deploy public HTTPS through
    // Kestrel with a valid certificate; forwarded headers are not implicitly trusted.
    var urls = builder.Configuration["urls"];
    if (string.IsNullOrWhiteSpace(urls))
    {
        urls = "http://127.0.0.1:5173";
        builder.WebHost.UseUrls(urls);
    }
    TransportPolicy.ValidateConfiguredUrls(urls);
    foreach (var endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
        if (endpoint["Url"] is { Length: > 0 } endpointUrl) TransportPolicy.ValidateConfiguredUrls(endpointUrl);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 16 * 1024;
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
    });
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(database);
    builder.Services.ConfigureHttpJsonOptions(json =>
    {
        json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
        json.SerializerOptions.MaxDepth = 16;
    });
    builder.Services.Configure<RouteHandlerOptions>(route => route.ThrowOnBadRequest = true);
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (rejected, _) =>
        {
            rejected.HttpContext.Response.Headers.RetryAfter = "60";
            await ApiErrors.WriteAsync(rejected.HttpContext, 429, "rate_limited", "Too many sign-in attempts. Wait a minute and try again.", options.OnlineTimeoutSeconds);
        };
        limiter.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.LoginRequestsPerMinute, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
    });

    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!TransportPolicy.IsSecureRequest(context))
            {
                await ApiErrors.WriteAsync(context, 426, "https_required", "HTTPS is required for non-loopback connections.", options.OnlineTimeoutSeconds);
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = 16 * 1024;
            if (context.Request.ContentLength > 16 * 1024)
                throw new ApiException(413, "request_too_large", "The request body is too large.");
            await next(context);
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        {
            await ApiErrors.WriteAsync(context, exception.StatusCode, exception.Code, exception.Message, options.OnlineTimeoutSeconds);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            var tooLarge = exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
            await ApiErrors.WriteAsync(context, tooLarge ? 413 : 400, tooLarge ? "request_too_large" : "invalid_request",
                tooLarge ? "The request body is too large." : "The request JSON is missing or invalid.", options.OnlineTimeoutSeconds);
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            await ApiErrors.WriteAsync(context, 400, "invalid_request", "The request JSON is invalid.", options.OnlineTimeoutSeconds);
        }
        catch (SqliteException) when (!context.Response.HasStarted)
        {
            app.Logger.LogWarning("A database operation could not be completed. No request credentials were logged.");
            await ApiErrors.WriteAsync(context, 503, "temporarily_unavailable", "The service is temporarily unavailable. Try again shortly.", options.OnlineTimeoutSeconds);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A disconnected caller does not receive a response; do not log request data.
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            app.Logger.LogError("An unexpected request failure occurred ({ExceptionType}).", exception.GetType().Name);
            await ApiErrors.WriteAsync(context, 500, "server_error", "The service could not complete the request.", options.OnlineTimeoutSeconds);
        }
    });
    app.UseStatusCodePages(async status =>
    {
        var code = status.HttpContext.Response.StatusCode;
        var error = code switch
        {
            404 => ("not_found", "The requested route was not found."),
            405 => ("method_not_allowed", "This route does not accept that HTTP method."),
            413 => ("request_too_large", "The request body is too large."),
            400 => ("invalid_request", "The request JSON is missing or invalid."),
            _ => ("request_failed", "The request could not be completed.")
        };
        await ApiErrors.WriteAsync(status.HttpContext, code, error.Item1, error.Item2, options.OnlineTimeoutSeconds);
    });
    app.UseRouting();
    app.UseRateLimiter();
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/admin"))
        {
            var path = context.Request.Path.Value?.TrimEnd('/');
            var anonymous = string.Equals(path, "/api/admin/login", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(path, "/api/admin/setup-status", StringComparison.OrdinalIgnoreCase);
            if (!anonymous) context.Items["admin"] = database.AuthenticateAdmin(BearerToken.Read(context));
        }
        await next(context);
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();

    static IResult Success(object data) => Results.Json(new { success = true, data });
    static AdminPrincipal Admin(HttpContext context) => context.Items["admin"] as AdminPrincipal
        ?? throw new ApiException(401, "invalid_session", "Sign in as an administrator.");

    app.MapGet("/health", () => Results.Json(new { status = "ok" }));
    app.MapGet("/api/admin/setup-status", () => Success(new { requiresSetup = database.RequiresSetup() }));
    app.MapPost("/api/admin/login", (AdminLoginRequest request) => Success(database.LoginAdmin(request))).RequireRateLimiting("login");
    app.MapPost("/api/admin/logout", (HttpContext context) =>
    {
        database.LogoutAdmin(Admin(context));
        return Success(new { signedOut = true });
    });
    app.MapGet("/api/admin/overview", () => Success(database.GetOverview()));
    app.MapGet("/api/admin/users", () => Success(database.GetUsers()));
    app.MapPost("/api/admin/users", (HttpContext context, CreateUserRequest request) => Success(database.CreateUser(Admin(context), request)));
    app.MapPatch("/api/admin/users/{id}", (HttpContext context, string id, UpdateUserRequest request) => Success(database.UpdateUser(Admin(context), id, request)));
    app.MapGet("/api/admin/licenses", () => Success(database.GetLicenses()));
    app.MapPost("/api/admin/licenses", (HttpContext context, CreateLicenseRequest request) => Success(database.IssueLicense(Admin(context), request)));
    app.MapPatch("/api/admin/licenses/{id}", (HttpContext context, string id, UpdateLicenseRequest request) => Success(database.UpdateLicense(Admin(context), id, request)));
    app.MapDelete("/api/admin/licenses/{id}/devices/{instanceId}", (HttpContext context, string id, string instanceId) =>
    {
        database.ResetDevice(Admin(context), id, instanceId);
        return Success(new { reset = true });
    });
    app.MapGet("/api/admin/audit", () => Success(database.GetAudit()));

    app.MapPost("/api/bot/login", (BotLoginRequest request) => Success(database.LoginBot(request))).RequireRateLimiting("login");
    foreach (var action in new[] { "verify", "status", "heartbeat", "offline" })
    {
        var currentAction = action;
        app.MapPost("/api/bot/license/" + currentAction, (HttpContext context, BotLicenseRequest request) =>
            Success(database.CheckBot(BearerToken.Read(context), request, currentAction)));
    }

    await app.RunAsync();
    return 0;
}
catch (ApiException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or SqliteException)
{
    Console.Error.WriteLine(exception is SqliteException
        ? "The license database could not be initialized. Check the data directory and permissions."
        : exception.Message);
    return 2;
}

internal static class BearerToken
{
    public static string Read(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(401, "invalid_session", "A bearer session is required.");
        var token = value[7..];
        if (token.Length is < 20 or > 200 || token.Any(char.IsWhiteSpace))
            throw new ApiException(401, "invalid_session", "The bearer session is invalid.");
        return token;
    }
}

internal static class TransportPolicy
{
    public static void ValidateConfiguredUrls(string urls)
    {
        foreach (var value in urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Configure an explicit HTTP loopback URL or an HTTPS URL.");
            if (uri.Scheme == "http" && !LoopbackHost(uri.Host))
                throw new InvalidOperationException("Plain HTTP may bind only to loopback. Configure HTTPS and a certificate for a public listener.");
        }
    }

    public static bool IsSecureRequest(HttpContext context)
    {
        if (context.Request.IsHttps) return true;
        return IsLoopback(context.Connection.RemoteIpAddress) && IsLoopback(context.Connection.LocalIpAddress) &&
               LoopbackHost(context.Request.Host.Host);
    }

    private static bool LoopbackHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopback(address));
    private static bool IsLoopback(IPAddress? address) => address is not null &&
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}

internal sealed record CommandLine(bool Help, bool InitializeAdministrator, string? DataDirectory, string? Username, string[] HostArguments)
{
    public static CommandLine Parse(string[] args)
    {
        bool help = false, initialize = false;
        string? directory = null, username = null;
        var host = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "help": help = true; break;
                case "init-admin": initialize = true; break;
                case "--data-dir": directory = NextValue(args, ref i); break;
                case "--username": username = NextValue(args, ref i); break;
                case var item when item.StartsWith("--data-dir=", StringComparison.Ordinal): directory = item[11..]; break;
                case var item when item.StartsWith("--username=", StringComparison.Ordinal): username = item[11..]; break;
                case var item when item.StartsWith("--password", StringComparison.OrdinalIgnoreCase):
                    throw new ArgumentException("Password command-line arguments are not supported. Use the hidden prompt or MULTIAIROBOT_ADMIN_PASSWORD.");
                default: host.Add(args[i]); break;
            }
        }
        if (username is not null && !initialize) throw new ArgumentException("--username is valid only with init-admin.");
        if (directory is not null && string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("--data-dir requires a directory path.");
        if (initialize && host.Count > 0) throw new ArgumentException("Unknown init-admin argument. Run --help for usage.");
        return new(help, initialize, directory, username, host.ToArray());
    }

    private static string NextValue(string[] args, ref int position)
    {
        if (++position >= args.Length || args[position].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("A command-line option is missing its value.");
        return args[position];
    }

    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar) && characters.Count < 257) characters.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(characters.ToArray());
    }

    public static void PrintHelp() => Console.WriteLine("""
        MultiAiRobot LicenseServer

        Start server:
          MultiAiRobot.LicenseServer [--data-dir PATH] [--urls http://127.0.0.1:5173]

        Create the first administrator (refuses to overwrite an existing admin):
          MultiAiRobot.LicenseServer init-admin [--username NAME] [--data-dir PATH]

        Username and password are prompted interactively when omitted.
        For unattended initialization only, set MULTIAIROBOT_ADMIN_PASSWORD in
        the process environment. Passwords are never accepted as command-line arguments.
        MULTIAIROBOT_DATA_DIR is the alternative to --data-dir.

        Defaults: loopback HTTP, user-local data directory, no built-in credentials.
        Public listeners require HTTPS with a configured certificate and AllowedHosts.
        Dashboard: /    Health: /health
        """);
}
