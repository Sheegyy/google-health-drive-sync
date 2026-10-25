using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HealthSync.Core;

public sealed class HealthDataCollector(GoogleApiClient api, long maximumBytes = 64 * 1024 * 1024)
{
    public async Task<JsonObject> CollectAsync(JsonObject records, QueryWindow window, SyncOptions options,
        CancellationToken cancellationToken = default)
    {
        var statuses = new JsonObject();
        long bytes = Encoding.UTF8.GetByteCount(records.ToJsonString());
        if (bytes > maximumBytes) throw new SyncException("Health export size limit exceeded; Drive was not updated");
        void Store(string key, JsonArray rows, string operation)
        {
            bytes += Encoding.UTF8.GetByteCount(rows.ToJsonString());
            if (bytes > maximumBytes)
                throw new SyncException("Health export size limit exceeded; no records were truncated and Drive was not updated");
            records[key] = rows;
            statuses[key] = new JsonObject { ["status"] = rows.Count == 0 ? "empty" : "ok", ["operation"] = operation };
        }

        foreach (var type in HealthDataCatalog.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records[type.Id] is JsonArray existing)
            {
                statuses[type.Id] = new JsonObject { ["status"] = existing.Count == 0 ? "empty" : "ok", ["operation"] = "reconcile" };
                continue;
            }
            if (type.Id == "food" && records["nutrition-log"] is not JsonArray)
            {
                statuses[type.Id] = new JsonObject { ["status"] = "not_collected", ["operation"] = "get",
                    ["reason"] = "nutrition-log-unavailable", ["selection"] = "referenced-by-nutrition-log" };
                continue;
            }
            try
            {
                var foodFailures = new JsonArray();
                var rows = type.Id == "food"
                    ? await ReadReferencedFoodsAsync(records["nutrition-log"]!.AsArray(), foodFailures, maximumBytes - bytes, cancellationToken)
                    : await ReadTypeAsync(type, window, options, maximumBytes - bytes, cancellationToken);
                if (type.Kind == HealthRecordKind.Ecg)
                {
                    var end = DateTimeOffset.Parse(window.EndUtc, CultureInfo.InvariantCulture);
                    rows = new JsonArray(rows.Where(row => EcgStart(row!) < end).Select(row => row!.DeepClone()).ToArray());
                }
                if (type.Kind == HealthRecordKind.Session && type.Id != "sleep")
                {
                    var start = DateTimeOffset.Parse(window.StartUtc, CultureInfo.InvariantCulture);
                    var end = DateTimeOffset.Parse(window.EndUtc, CultureInfo.InvariantCulture);
                    rows = new JsonArray(rows.Where(row => SessionStart(row!, type.Id) is var timestamp && timestamp >= start && timestamp < end)
                        .Select(row => row!.DeepClone()).ToArray());
                }
                Store(type.Id, rows, type.Operation);
                if (type.Id == "food")
                {
                    statuses[type.Id]!["selection"] = "referenced-by-nutrition-log";
                    if (foodFailures.Count > 0)
                    {
                        bytes += Encoding.UTF8.GetByteCount(foodFailures.ToJsonString());
                        if (bytes > maximumBytes) throw new SyncException("Food status size limit exceeded; Drive was not updated");
                        statuses[type.Id]!["status"] = "partial";
                        statuses[type.Id]!["unavailable_references"] = foodFailures;
                    }
                }
            }
            catch (SyncException exception) when (Unavailable(exception) is not null)
            {
                statuses[type.Id] = Unavailable(exception);
            }
        }

        foreach (var resource in new[] { "profile", "settings", "irnProfile", "identity" })
        {
            try
            {
                var value = await api.SendAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/{resource}", resource,
                    cancellationToken: cancellationToken);
                Store(resource, value.Count == 0 ? new JsonArray() : new JsonArray(value), "get");
            }
            catch (SyncException exception) when (Unavailable(exception, allowNotFound: true) is not null)
            {
                statuses[resource] = Unavailable(exception, allowNotFound: true);
            }
        }
        try
        {
            Store("paired-devices", await api.PagedAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/pairedDevices",
                new JsonObject { ["pageSize"] = 100 }, "pairedDevices", "paired devices", cancellationToken,
                maximumBytes: maximumBytes - bytes), "list");
        }
        catch (SyncException exception) when (Unavailable(exception) is not null)
        {
            statuses["paired-devices"] = Unavailable(exception);
        }

        var exports = new JsonArray();
        var partial = false;
        long exportBytes = 0;
        if (records["exercise"] is JsonArray exercises)
        {
            foreach (var exercise in exercises.OfType<JsonObject>())
            {
                var name = exercise["name"]?.ToString() ?? exercise["dataPointName"]?.ToString() ?? "";
                var match = Regex.Match(name, "^users/[A-Za-z0-9_-]+/dataTypes/exercise/dataPoints/([A-Za-z0-9_-]+)$");
                if (!match.Success) throw new SyncException("Exercise export has no valid data point ID; Drive was not updated");
                try
                {
                    var text = await api.SendTextAsync(HttpMethod.Get,
                        $"{GoogleApiClient.HealthBase}/dataTypes/exercise/dataPoints/{match.Groups[1].Value}:exportExerciseTcx?alt=media&partialData=true",
                        "exercise TCX", cancellationToken: cancellationToken);
                    var document = ParseTcx(text);
                    var row = new JsonObject { ["exercise_name"] = name, ["status"] = "ok", ["tcx_xml"] = text,
                        ["contains_gps_positions"] = document.Descendants().Any(element => element.Name.LocalName == "Position") };
                    exportBytes += Encoding.UTF8.GetByteCount(row.ToJsonString());
                    if (bytes + exportBytes > maximumBytes) throw new SyncException("Exercise export size limit exceeded; Drive was not updated");
                    exports.Add(row);
                }
                catch (SyncException exception) when (Unavailable(exception, allowNotFound: true) is JsonObject)
                {
                    var unavailable = Unavailable(exception, allowNotFound: true)!;
                    unavailable["exercise_name"] = name;
                    exportBytes += Encoding.UTF8.GetByteCount(unavailable.ToJsonString());
                    if (bytes + exportBytes > maximumBytes) throw new SyncException("Exercise export size limit exceeded; Drive was not updated");
                    exports.Add(unavailable);
                    partial = true;
                }
            }
        }
        if (records["exercise"] is JsonArray)
        {
            Store("exercise-tcx", exports, "exportExerciseTcx");
            if (partial) statuses["exercise-tcx"]!["status"] = "partial";
        }
        else
        {
            statuses["exercise-tcx"] = new JsonObject { ["status"] = "not_collected",
                ["operation"] = "exportExerciseTcx", ["reason"] = "exercise-unavailable" };
        }
        return statuses;
    }

    private async Task<JsonArray> ReadReferencedFoodsAsync(JsonArray logs, JsonArray failures, long remainingBytes,
        CancellationToken cancellationToken)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in logs.OfType<JsonObject>())
        {
            if (row["nutritionLog"] is not JsonObject log || log["food"] is null) continue;
            var match = Regex.Match(log["food"]!.ToString(), "^users/[A-Za-z0-9_-]+/dataTypes/food/dataPoints/([A-Za-z0-9_-]+)$");
            if (!match.Success) throw new SyncException("Food reference has no valid resource ID; Drive was not updated");
            identifiers.Add(match.Groups[1].Value);
        }
        var rows = new JsonArray();
        long bytes = 2;
        foreach (var identifier in identifiers)
        {
            try
            {
                var row = await api.SendAsync(HttpMethod.Get,
                    $"{GoogleApiClient.HealthBase}/dataTypes/food/dataPoints/{identifier}", "referenced food",
                    cancellationToken: cancellationToken);
                bytes += Encoding.UTF8.GetByteCount(row.ToJsonString()) + 1;
                if (bytes > remainingBytes) throw new SyncException("Food export size limit exceeded; Drive was not updated");
                rows.Add(row);
            }
            catch (SyncException exception) when (Unavailable(exception, allowNotFound: true) is not null)
            {
                var failure = Unavailable(exception, allowNotFound: true)!;
                failure["food_name"] = $"users/me/dataTypes/food/dataPoints/{identifier}";
                bytes += Encoding.UTF8.GetByteCount(failure.ToJsonString()) + 1;
                if (bytes > remainingBytes) throw new SyncException("Food status size limit exceeded; Drive was not updated");
                failures.Add(failure);
            }
        }
        return rows;
    }

    private async Task<JsonArray> ReadTypeAsync(HealthDataType type, QueryWindow window, SyncOptions options,
        long remainingBytes, CancellationToken cancellationToken)
    {
        if (type.Operation == "rollUp")
        {
            if (window.StartUtc == window.EndUtc) return new JsonArray();
            return await api.PagedAsync(HttpMethod.Post, $"{GoogleApiClient.HealthBase}/dataTypes/{type.Id}/dataPoints:rollUp",
                new JsonObject { ["range"] = new JsonObject { ["startTime"] = window.StartUtc, ["endTime"] = window.EndUtc },
                    ["windowSize"] = "3600s", ["pageSize"] = options.Days * 24 },
                "rollupDataPoints", type.Id, cancellationToken, maximumBytes: remainingBytes);
        }
        if (type.Kind == HealthRecordKind.Aggregate)
        {
            if (window.StartUtc == window.EndUtc) return new JsonArray();
            var start = DateOnly.ParseExact(window.FirstDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
            var end = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(window.EndUtc, CultureInfo.InvariantCulture),
                SyncOptions.FindZone(options.TimeZone)).DateTime;
            return await api.PagedAsync(HttpMethod.Post,
                $"{GoogleApiClient.HealthBase}/dataTypes/{type.Id}/dataPoints:dailyRollUp",
                new JsonObject { ["range"] = new JsonObject { ["start"] = CivilTime(start), ["end"] = CivilTime(end) },
                    ["windowSizeDays"] = 1, ["pageSize"] = options.Days },
                "rollupDataPoints", type.Id, cancellationToken, maximumBytes: remainingBytes);
        }
        if (type.Kind != HealthRecordKind.Food && window.StartUtc == window.EndUtc && type.Kind != HealthRecordKind.Daily)
            return new JsonArray();
        var fields = new JsonObject { ["pageSize"] = type.Id is "sleep" or "exercise" ? 25 : 10000 };
        if (type.Filter(window, options.TimeZone) is { } filter) fields["filter"] = filter;
        var suffix = type.Operation == "reconcile" ? ":reconcile" : "";
        return await api.PagedAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/dataTypes/{type.Id}/dataPoints{suffix}",
            fields, "dataPoints", type.Id, cancellationToken, maximumBytes: remainingBytes);
    }

    private static DateTimeOffset SessionStart(JsonNode row, string id)
    {
        var parts = id.Split('-');
        var field = parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        var value = row[field]?["interval"]?["startTime"]?.ToString();
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new SyncException($"{id}: session has no physical start time; Drive was not updated");
        return timestamp;
    }

    private static DateTimeOffset EcgStart(JsonNode row)
    {
        var value = row["electrocardiogram"]?["interval"]?["startTime"]?.ToString();
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new SyncException("ECG record has no valid start time; Drive was not updated");
        return timestamp;
    }

    private static XDocument ParseTcx(string text)
    {
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = GoogleApiClient.MaximumResponseBytes });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "TrainingCenterDatabase") throw new SyncException("Exercise export is not valid TCX; Drive was not updated");
            return document;
        }
        catch (XmlException) { throw new SyncException("Exercise export is not valid TCX; Drive was not updated"); }
    }

    private static JsonObject? Unavailable(SyncException exception, bool allowNotFound = false)
    {
        var status = exception.Reason switch
        {
            "UNSUPPORTED_DATA_TYPE_ACTION" or "INVALID_DATA_TYPE" or "INVALID_PARENT_DATA_TYPE_COLLECTION" when exception.StatusCode == 400 => "not_supported",
            "API_PRIVATE_PREVIEW_ACCESS_DENIED" when exception.StatusCode == 403 => "not_eligible",
            "DATA_ACCESS_DENIED" or "RESOURCE_PERMISSION_DENIED" when exception.StatusCode == 403 => "access_denied",
            "RESOURCE_NOT_FOUND" when allowNotFound && exception.StatusCode == 404 => "not_recorded",
            _ => null
        };
        return status is null ? null : new JsonObject { ["status"] = status, ["http_status"] = exception.StatusCode, ["reason"] = exception.Reason };
    }

    private static JsonObject CivilTime(DateTime value) => new()
    {
        ["date"] = new JsonObject { ["year"] = value.Year, ["month"] = value.Month, ["day"] = value.Day },
        ["time"] = new JsonObject { ["hours"] = value.Hour, ["minutes"] = value.Minute, ["seconds"] = value.Second, ["nanos"] = 0 }
    };
}