using System.Globalization;
using System.Text.RegularExpressions;

namespace HealthSync.Core;

public sealed class SyncException(string message, int? statusCode = null, string? reason = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? Reason { get; } = reason;
}

public sealed record SyncOptions(string ClientId, string ClientSecret, string RefreshToken,
    string DriveFileId, int Days = 14, string TimeZone = "Asia/Tokyo", bool HourlyHeartRate = true,
    bool AllAvailableData = false, bool ArchiveSnapshots = false, string? ArchiveFolderId = null)
{
    public const string DailyScheduleUtc = "0 0 22 * * *";

    public static readonly string[] HealthScopes = HealthDataCatalog.ReadScopes;
    public static readonly string[] DriveScopes = ["https://www.googleapis.com/auth/drive.file"];
    public static readonly string[] Scopes = [.. HealthScopes, .. DriveScopes];

    public static SyncOptions FromEnvironment()
    {
        string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name)?.Trim();
            if (string.IsNullOrEmpty(value) || value.StartsWith("@Microsoft.KeyVault(", StringComparison.Ordinal))
                throw new SyncException($"{name} is absent or its Key Vault reference is unresolved");
            return value;
        }
        var fileId = Required("DRIVE_FILE_ID");
        if (!Regex.IsMatch(fileId, "^[A-Za-z0-9_-]+$"))
            throw new SyncException("DRIVE_FILE_ID must be a file ID, not a URL");
        if (!int.TryParse(Environment.GetEnvironmentVariable("HEALTH_LOOKBACK_DAYS") ?? "14", out var days)
            || days is < 1 or > 14)
            throw new SyncException("HEALTH_LOOKBACK_DAYS must be 1..14");
        var zone = Environment.GetEnvironmentVariable("HEALTH_TIMEZONE") ?? "Asia/Tokyo";
        _ = FindZone(zone);
        if (!bool.TryParse(Environment.GetEnvironmentVariable("INCLUDE_HOURLY_HEART_RATE") ?? "true", out var hourly))
            throw new SyncException("INCLUDE_HOURLY_HEART_RATE must be true or false");
        if (!bool.TryParse(Environment.GetEnvironmentVariable("HEALTH_INCLUDE_ALL_DATA") ?? "true", out var allData))
            throw new SyncException("HEALTH_INCLUDE_ALL_DATA must be true or false");
        if (!bool.TryParse(Environment.GetEnvironmentVariable("HEALTH_ARCHIVE_ENABLED") ?? "true", out var archive))
            throw new SyncException("HEALTH_ARCHIVE_ENABLED must be true or false");
        var archiveFolder = Environment.GetEnvironmentVariable("DRIVE_ARCHIVE_FOLDER_ID")?.Trim();
        if (string.IsNullOrEmpty(archiveFolder)) archiveFolder = null;
        if (archiveFolder is not null && !Regex.IsMatch(archiveFolder, "^[A-Za-z0-9_-]+$"))
            throw new SyncException("DRIVE_ARCHIVE_FOLDER_ID must be a folder ID, not a URL");
        return new(Required("GOOGLE_CLIENT_ID"), Required("GOOGLE_CLIENT_SECRET"),
            Required("GOOGLE_REFRESH_TOKEN"), fileId, days, zone, hourly, allData, archive, archiveFolder);
    }

    public static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            throw new SyncException("HEALTH_TIMEZONE is invalid");
        }
    }
}

public sealed record QueryWindow(string StartUtc, string EndUtc, string FirstDate, string EndDateExclusive)
{
    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString(
        "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static QueryWindow Create(DateTimeOffset now, SyncOptions options)
    {
        var zone = SyncOptions.FindZone(options.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(1 - options.Days);
        var midnight = DateTime.SpecifyKind(first.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var start = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
        return new(Utc(start), Utc(now), first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
