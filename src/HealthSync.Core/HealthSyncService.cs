using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace HealthSync.Core;

public sealed class HealthSyncService(GoogleApiClient api, SyncOptions options, long maximumBytes = 64 * 1024 * 1024)
{
    private static readonly string[] DailyTypes = ["daily-resting-heart-rate", "daily-heart-rate-variability",
        "daily-oxygen-saturation", "daily-respiratory-rate"];
    private static readonly string[] ActivityTypes = ["steps", "total-calories", "active-energy-burned"];
    private string SourceFamily => options.AllAvailableData
        ? "users/me/dataSourceFamilies/all-sources" : "users/me/dataSourceFamilies/google-wearables";

    public async Task<IReadOnlyDictionary<string, int>> RunAsync(
        DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var queryTime = now ?? DateTimeOffset.UtcNow;
        var window = QueryWindow.Create(queryTime, options);
        var records = new JsonObject();
        long remainingBytes = maximumBytes - 2;
        void Store(string key, JsonArray rows)
        {
            remainingBytes -= Encoding.UTF8.GetByteCount(rows.ToJsonString()) + Encoding.UTF8.GetByteCount(key) + 4;
            if (remainingBytes < 0) throw new SyncException("Health export size limit exceeded; Drive was not updated");
            records[key] = rows;
        }
        var hasInterval = window.StartUtc != window.EndUtc;
        var sleepFilter = $"sleep.interval.end_time >= \"{window.StartUtc}\" AND sleep.interval.end_time < \"{window.EndUtc}\"";
        Store("sleep", hasInterval ? await ReconcileAsync("sleep", sleepFilter, 25, remainingBytes, cancellationToken) : new JsonArray());
        foreach (var kind in DailyTypes)
        {
            var field = kind.Replace('-', '_');
            var filter = $"{field}.date >= \"{window.FirstDate}\" AND {field}.date < \"{window.EndDateExclusive}\"";
            Store(kind, await ReconcileAsync(kind, filter, 1000, remainingBytes, cancellationToken));
        }
        if (options.HourlyHeartRate)
            Store("heart-rate-hourly", hasInterval ? await api.PagedAsync(HttpMethod.Post,
                $"{GoogleApiClient.HealthBase}/dataTypes/heart-rate/dataPoints:rollUp", new JsonObject
                {
                    ["range"] = new JsonObject { ["startTime"] = window.StartUtc, ["endTime"] = window.EndUtc },
                    ["windowSize"] = "3600s", ["pageSize"] = options.Days * 24, ["dataSourceFamily"] = SourceFamily
                }, "rollupDataPoints", "heart-rate-hourly", cancellationToken, maximumBytes: remainingBytes) : new JsonArray());
        var civilStart = DateOnly.ParseExact(window.FirstDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToDateTime(TimeOnly.MinValue);
        var civilEnd = TimeZoneInfo.ConvertTime(queryTime, SyncOptions.FindZone(options.TimeZone)).DateTime;
        foreach (var kind in ActivityTypes)
        {
            var key = $"{kind}-daily";
            Store(key, hasInterval ? await api.PagedAsync(HttpMethod.Post,
                $"{GoogleApiClient.HealthBase}/dataTypes/{kind}/dataPoints:dailyRollUp", new JsonObject
                {
                    ["range"] = new JsonObject { ["start"] = CivilTime(civilStart), ["end"] = CivilTime(civilEnd) },
                    ["windowSizeDays"] = 1, ["pageSize"] = options.Days, ["dataSourceFamily"] = SourceFamily
                }, "rollupDataPoints", key, cancellationToken, maximumBytes: remainingBytes) : new JsonArray());
        }
        var statuses = options.AllAvailableData
            ? await new HealthDataCollector(api, maximumBytes).CollectAsync(records, window, options, cancellationToken)
            : new JsonObject();
        var counts = records.ToDictionary(p => p.Key, p => ((JsonArray)p.Value!).Count);
        var countJson = new JsonObject();
        var empty = new JsonArray();
        foreach (var (kind, count) in counts)
        {
            countJson[kind] = count;
            if (count == 0) empty.Add(kind);
        }
        var snapshot = new JsonObject
        {
            ["schema_version"] = options.AllAvailableData ? 2 : 1,
            ["collection_mode"] = options.AllAvailableData ? "all_documented_readable_v4" : "summary",
            ["collection_statuses"] = statuses,
            ["archive_policy"] = options.ArchiveSnapshots ? "daily snapshots; no automatic deletion" : "disabled",
            ["query_completed_at_utc"] = QueryWindow.Utc(DateTimeOffset.UtcNow),
            ["query_end_utc"] = window.EndUtc,
            ["timezone"] = options.TimeZone,
            ["range"] = new JsonObject { ["start_utc"] = window.StartUtc, ["end_utc"] = window.EndUtc,
                ["first_date"] = window.FirstDate, ["end_date_exclusive"] = window.EndDateExclusive },
            ["source_family"] = SourceFamily, ["sleep_day_basis"] = "wake/end date",
            ["activity_day_basis"] = "recorded civil date; query endpoints use the configured timezone",
            ["record_counts"] = countJson, ["empty_data_types"] = empty,
            ["notes"] = new JsonArray("Query time is not the device's last synchronization time.",
                "Empty arrays mean no returned data; they are not zero measurements.",
                "The final hourly bucket may cover less than one hour.",
                "The final activity day is partial up to the query time.",
                "An activity bucket with an unset value is missing data, not zero.",
                "Total calories include active energy; do not add them together.",
                "Raw data from multiple sources can overlap; do not sum raw records and rollups together.",
                "High-frequency types use hourly rollups; seconds-level raw samples are not included for those types.",
                "Food details include only foods referenced by the selected nutrition logs, not the shared food database.",
                "Unavailable types are described in collection_statuses and are not zero measurements.",
                "Menstrual periods, ovulation tests, symptoms and moods are documented as write-only in v4 and are not exported.",
                "Wearable sleep stages and other health values are estimates."),
            ["records"] = records
        };
        if (Encoding.UTF8.GetByteCount(snapshot.ToJsonString()) > maximumBytes)
            throw new SyncException("Health snapshot including metadata exceeds the size limit; Drive was not updated");
        await new DriveSnapshotWriter(api, options).WriteAsync(snapshot, queryTime, cancellationToken);
        return counts;
    }

    private static JsonObject CivilTime(DateTime value) => new()
    {
        ["date"] = new JsonObject { ["year"] = value.Year, ["month"] = value.Month, ["day"] = value.Day },
        ["time"] = new JsonObject { ["hours"] = value.Hour, ["minutes"] = value.Minute,
            ["seconds"] = value.Second, ["nanos"] = 0 }
    };

    private Task<JsonArray> ReconcileAsync(string type, string filter, int pageSize, long maximumBytes, CancellationToken ct) =>
        api.PagedAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/dataTypes/{type}/dataPoints:reconcile",
            new JsonObject { ["filter"] = filter, ["pageSize"] = pageSize, ["dataSourceFamily"] = SourceFamily },
            "dataPoints", type, ct, maximumBytes: maximumBytes);
}
