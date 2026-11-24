using System.Text.Json.Serialization;

namespace MultiAiRobot.LicenseServer;

public sealed class ServerOptions
{
    public int AdminSessionHours { get; set; } = 8;
    public int BotSessionHours { get; set; } = 24;
    public int OnlineTimeoutSeconds { get; set; } = 180;
    public int LoginRequestsPerMinute { get; set; } = 10;
    public int FailedPasswordAttemptsBeforeLockout { get; set; } = 5;
    public int PasswordLockoutMinutes { get; set; } = 5;

    public void Validate()
    {
        if (AdminSessionHours is < 1 or > 24 || BotSessionHours is < 1 or > 168 ||
            OnlineTimeoutSeconds is < 30 or > 3600 || LoginRequestsPerMinute is < 1 or > 120 ||
            FailedPasswordAttemptsBeforeLockout is < 3 or > 20 || PasswordLockoutMinutes is < 1 or > 60)
            throw new InvalidOperationException("LicenseServer configuration contains an out-of-range value.");
    }
}

public sealed record AdminLoginRequest(string? Username, string? Password);
public sealed record CreateUserRequest(string? Username, string? Password);
public sealed record UpdateUserRequest(bool? Enabled, string? Password);
public sealed record CreateLicenseRequest(string? Username, int Days, int MaxDevices);
public sealed record UpdateLicenseRequest(bool? Enabled, DateTimeOffset? ExpiresAtUtc, int? MaxDevices);

public sealed record BotLoginRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("license_key")] string? LicenseKey,
    [property: JsonPropertyName("instance_id")] string? InstanceId,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("profit")] decimal? Profit);

public sealed record BotLicenseRequest(
    [property: JsonPropertyName("license_key")] string? LicenseKey,
    [property: JsonPropertyName("instance_id")] string? InstanceId);

public sealed record UserDto(string Id, string Username, bool Enabled, DateTimeOffset CreatedAtUtc);
public sealed record DeviceDto(string InstanceId, DateTimeOffset LastSeenAtUtc, bool Online);
public sealed record LicenseDto(string Id, string Username, string KeyPrefix, bool Enabled,
    DateTimeOffset ExpiresAtUtc, int MaxDevices, DateTimeOffset CreatedAtUtc, IReadOnlyList<DeviceDto> Devices);
public sealed record AuditDto(DateTimeOffset OccurredAtUtc, string Action, string Subject, string Actor);
public sealed record OverviewDto(int Users, int Licenses, int ActiveLicenses, int OnlineDevices);
public sealed record AdminLoginDto(string Token, string Username, DateTimeOffset ExpiresAtUtc);
public sealed record LicenseIssueDto(LicenseDto License, string Key);
public sealed record BotLoginDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at_utc")] DateTimeOffset ExpiresAtUtc);
public sealed record BotStateDto(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("online_timeout_seconds")] int OnlineTimeoutSeconds);

public sealed record AdminPrincipal(string SessionHash, string UserId, string Username);

public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public static class ApiErrors
{
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string message,
        int onlineTimeout = 180)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        object? data = context.Request.Path.StartsWithSegments("/api/bot")
            ? new BotStateDto(false, false, false, code, message, onlineTimeout)
            : null;
        await context.Response.WriteAsJsonAsync(new { success = false, message, error = new { code }, data });
    }
}
