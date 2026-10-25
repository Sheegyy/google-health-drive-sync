using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HealthSync.Core;

internal static class AllDataChecks
{
    private static readonly SyncOptions Options = new("client", "secret", "refresh", "target_file", AllAvailableData: true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T03:00:00Z");

    public static async Task AllTypesAsync()
    {
        using var harness = new ExportHarness();
        var records = new JsonObject();
        var statuses = await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(statuses.Count == 45 && records.Count == 45, "All 39 types, four resources, devices and TCX must be represented");
        foreach (var type in HealthDataCatalog.Types)
        {
            Check(records[type.Id] is JsonArray, $"Missing type: {type.Id}");
            Check(statuses[type.Id]!["status"]!.ToString() == "empty", "An empty response is not an unavailable response");
            Check(harness.Urls.Count(url => url.Contains($"/dataTypes/{type.Id}/dataPoints", StringComparison.Ordinal)) ==
                (type.Id == "food" ? 0 : 1), "Types are collected once; food details require a nutrition reference");
        }
        Check(harness.Urls.All(url => !url.Contains("google-wearables", StringComparison.Ordinal)), "All sources must be included");
        Check(harness.Urls.Where(url => url.Contains("/dataTypes/", StringComparison.Ordinal)).All(url =>
            !url.Contains("/food/dataPoints?", StringComparison.Ordinal) || !url.Contains("filter=", StringComparison.Ordinal)), "Food must not use a time filter");
    }

    public static async Task StatusesAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/", StringComparison.Ordinal)
            ? Error(403, "API_PRIVATE_PREVIEW_ACCESS_DENIED") : null);
        var records = new JsonObject();
        var statuses = await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(statuses["nutrition-log"]!["status"]!.ToString() == "not_eligible", "Preview rejection must be explicit");
        Check(records["nutrition-log"] is null, "Unavailable data must not look like measured zero or an empty successful result");
        Check(statuses["food"]!["status"]!.ToString() == "not_collected" && records["food"] is null,
            "Unavailable nutrition must not be misrepresented as no referenced foods");
        Check(statuses["steps"]!["status"]!.ToString() == "empty", "Other types must still be collected");
    }

    public static async Task FoodReferencesAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/dataPoints?", StringComparison.Ordinal)
            ? Ok("{\"dataPoints\":[{\"nutritionLog\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"},\"food\":\"users/me/dataTypes/food/dataPoints/food-1234\"}},{\"nutritionLog\":{\"interval\":{\"startTime\":\"2026-10-25T02:01:00Z\"},\"food\":\"users/me/dataTypes/food/dataPoints/food-1234\"}}]}")
            : url.EndsWith("/food/dataPoints/food-1234", StringComparison.Ordinal)
                ? Ok("{\"name\":\"users/me/dataTypes/food/dataPoints/food-1234\",\"food\":{\"displayName\":\"test food\"}}") : null);
        var records = new JsonObject();
        var statuses = await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["nutrition-log"]!.AsArray().Count == 2 && records["food"]!.AsArray().Count == 1, "Retain every meal and deduplicate food references");
        Check(harness.Urls.Count(url => url.Contains("/food/dataPoints", StringComparison.Ordinal)) == 1, "Never list the shared food database");
        Check(statuses["food"]!["selection"]!.ToString() == "referenced-by-nutrition-log", "The food selection must be explicit");
    }

    public static async Task FoodReferenceSafetyAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/dataPoints?", StringComparison.Ordinal)
            ? Ok("{\"dataPoints\":[{\"nutritionLog\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"},\"food\":\"https://untrusted.test/food\"}}]}") : null);
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
        Check(harness.Uploads == 0 && harness.Urls.All(url => !url.Contains("untrusted.test", StringComparison.Ordinal)), "Do not follow arbitrary food URLs or overwrite Drive");
    }

    public static async Task FoodPartialAsync()
    {
        foreach (var reason in new[] { "RESOURCE_PERMISSION_DENIED", "RESOURCE_NOT_FOUND" })
        {
            using var harness = new ExportHarness(url =>
            {
                if (url.Contains("/nutrition-log/dataPoints?", StringComparison.Ordinal))
                    return Ok(new JsonObject { ["dataPoints"] = new JsonArray(new[] { "first", "denied", "last" }.Select(identifier => (JsonNode)new JsonObject
                    {
                        ["nutritionLog"] = new JsonObject { ["interval"] = new JsonObject { ["startTime"] = "2026-10-25T02:00:00Z" },
                            ["food"] = $"users/me/dataTypes/food/dataPoints/{identifier}" }
                    }).ToArray()) }.ToJsonString());
                if (url.EndsWith("/food/dataPoints/denied", StringComparison.Ordinal))
                    return Error(reason == "RESOURCE_NOT_FOUND" ? 404 : 403, reason);
                if (url.Contains("/food/dataPoints/", StringComparison.Ordinal))
                    return Ok("{\"food\":{\"displayName\":\"test food\"}}");
                return null;
            });
            await new HealthSyncService(harness.Api, Options).RunAsync(Now);
            var snapshot = harness.Snapshot!;
            Check(snapshot["records"]!["food"]!.AsArray().Count == 2, "A rejected reference must not discard readable foods");
            Check(snapshot["collection_statuses"]!["food"]!["status"]!.ToString() == "partial", "Partial food collection must be explicit");
            Check(snapshot["collection_statuses"]!["food"]!["unavailable_references"]!.AsArray().Count == 1, "Record each unavailable food reference");
            Check(harness.Urls.Count(url => url.Contains("/food/dataPoints/", StringComparison.Ordinal)) == 3, "Continue to later readable foods");
        }
    }

    public static async Task FoodFailureAsync()
    {
        foreach (var status in new[] { 403, 503 })
        {
            using var harness = new ExportHarness(url => url.Contains("/nutrition-log/dataPoints?", StringComparison.Ordinal)
                ? Ok("{\"dataPoints\":[{\"nutritionLog\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"},\"food\":\"users/me/dataTypes/food/dataPoints/food-1234\"}}]}")
                : url.Contains("/food/dataPoints/", StringComparison.Ordinal)
                    ? Error(status, status == 403 ? "MISSING_OAUTH_SCOPE" : "INTERNAL_ERROR") : null);
            await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
            Check(harness.Uploads == 0, "Food authentication and server failures must preserve Drive");
        }
    }

    public static async Task AuthorizationFailureAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/", StringComparison.Ordinal)
            ? Error(403, "MISSING_OAUTH_SCOPE") : null);
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
        Check(harness.Uploads == 0, "Missing consent must preserve the previous Drive snapshot");
    }

    public static async Task ServerFailureAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/", StringComparison.Ordinal)
            ? Error(503, "INTERNAL_ERROR") : null);
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
        Check(harness.Uploads == 0, "Server failure must preserve the previous Drive snapshot");
    }

    public static async Task EcgBoundaryAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/electrocardiogram/", StringComparison.Ordinal)
            ? Ok("{\"dataPoints\":[{\"electrocardiogram\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"}}},{\"electrocardiogram\":{\"interval\":{\"startTime\":\"2026-10-25T03:00:00Z\"}}}]}") : null);
        var records = new JsonObject();
        await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["electrocardiogram"]!.AsArray().Count == 1, "ECG API's missing upper bound must be enforced locally");
        Check(!Uri.UnescapeDataString(harness.Urls.Single(url => url.Contains("/electrocardiogram/", StringComparison.Ordinal))).Contains(" AND ", StringComparison.Ordinal), "Unsupported ECG filter upper bound");
    }

    public static async Task DevicesPaginationAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/pairedDevices?", StringComparison.Ordinal)
            ? Ok(url.Contains("pageToken=second", StringComparison.Ordinal)
                ? "{\"pairedDevices\":[{\"name\":\"second\"}]}"
                : "{\"pairedDevices\":[{\"name\":\"first\"}],\"nextPageToken\":\"second\"}") : null);
        var records = new JsonObject();
        await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["paired-devices"]!.AsArray().Count == 2, "Connected-device pages must not be lost");
    }

    public static async Task TravelSessionAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/exercise/dataPoints?", StringComparison.Ordinal)
            ? Ok("{\"dataPoints\":[{\"name\":\"users/me/dataTypes/exercise/dataPoints/1\",\"exercise\":{\"interval\":{\"startTime\":\"2026-10-25T03:01:00Z\"}}}]}") : null);
        var records = new JsonObject();
        await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["exercise"]!.AsArray().Count == 0, "Padded civil query must exclude records after the UTC endpoint");
        var query = Uri.UnescapeDataString(harness.Urls.Single(url => url.Contains("/exercise/dataPoints?", StringComparison.Ordinal)));
        Check(query.Contains("2026-10-25T17:00:00", StringComparison.Ordinal), "Civil range must cover UTC+14 travel");
        Check(!harness.Urls.Any(url => url.Contains("exportExerciseTcx", StringComparison.Ordinal)), "Do not export exercise outside the UTC range");
    }

    public static async Task DetailedReasonsAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/nutrition-log/", StringComparison.Ordinal)
            ? Ok("{}") : null);
        using var handler = new DetailedErrorHandler();
        using var http = new HttpClient(handler);
        var api = new GoogleApiClient(http, new DummyToken());
        try { await api.SendAsync(HttpMethod.Get, "https://health.googleapis.com/v4/users/me/profile", "profile"); }
        catch (SyncException exception)
        {
            Check(exception.Reason == "MISSING_OAUTH_SCOPE", "Missing scope must take precedence over optional unsupported errors");
            Check(!exception.Message.Contains("PRIVATE", StringComparison.Ordinal), "Detailed error text must remain private");
            return;
        }
        throw new Exception("Detailed reasons must fail safely");
    }

    public static async Task PageBudgetAsync()
    {
        using var harness = new ExportHarness(_ => Ok("{\"dataPoints\":[{\"record\":\"longer than the allowed budget\"}],\"nextPageToken\":\"must-not-fetch\"}"));
        await ExpectFailureAsync(() => harness.Api.PagedAsync(HttpMethod.Get, "https://health.googleapis.com/v4/users/me/dataTypes/food/dataPoints",
            new JsonObject(), "dataPoints", "food", maximumBytes: 8));
        Check(harness.Urls.Count == 1, "Stop before fetching another page after budget exhaustion");
    }

    public static async Task TcxAsync()
    {
        using var harness = new ExportHarness(url =>
        {
            if (url.Contains(":exportExerciseTcx", StringComparison.Ordinal))
                return Ok("<TrainingCenterDatabase><Activities><Position><LatitudeDegrees>0</LatitudeDegrees></Position></Activities></TrainingCenterDatabase>");
            if (url.Contains("/exercise/dataPoints?", StringComparison.Ordinal))
                return Ok("{\"dataPoints\":[{\"name\":\"users/me/dataTypes/exercise/dataPoints/1234\",\"exercise\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"}}}]}");
            return null;
        });
        var records = new JsonObject();
        await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["exercise-tcx"]!.AsArray().Count == 1, "Every recorded exercise must have a TCX export result");
        Check(records["exercise-tcx"]![0]!["contains_gps_positions"]!.GetValue<bool>(), "GPS data must be preserved");
        Check(harness.Urls.Any(url => url.EndsWith(":exportExerciseTcx?alt=media&partialData=true", StringComparison.Ordinal)), "Use documented media export");
    }

    public static async Task TcxValidationAsync()
    {
        foreach (var xml in new[] { "<html>PRIVATE_ERROR</html>", "<!DOCTYPE TrainingCenterDatabase [<!ENTITY external SYSTEM 'file:///private'>]><TrainingCenterDatabase>&external;</TrainingCenterDatabase>" })
        {
            using var harness = new ExportHarness(url => url.Contains(":exportExerciseTcx", StringComparison.Ordinal) ? Ok(xml)
                : url.Contains("/exercise/dataPoints?", StringComparison.Ordinal)
                    ? Ok("{\"dataPoints\":[{\"name\":\"users/me/dataTypes/exercise/dataPoints/1234\",\"exercise\":{\"interval\":{\"startTime\":\"2026-10-25T02:00:00Z\"}}}]}") : null);
            await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
            Check(harness.Uploads == 0, "Invalid TCX must not overwrite Drive");
        }
    }

    public static async Task UnavailableExerciseAsync()
    {
        using var harness = new ExportHarness(url => url.Contains("/exercise/dataPoints?", StringComparison.Ordinal)
            ? Error(403, "RESOURCE_PERMISSION_DENIED") : null);
        var records = new JsonObject();
        var statuses = await new HealthDataCollector(harness.Api).CollectAsync(records, QueryWindow.Create(Now, Options), Options);
        Check(records["exercise"] is null && records["exercise-tcx"] is null, "Unavailable exercise must not produce a fabricated empty TCX array");
        Check(statuses["exercise-tcx"]!["status"]!.ToString() == "not_collected", "Unavailable exercise must mark TCX as not collected");
        Check(!harness.Urls.Any(url => url.Contains("exportExerciseTcx", StringComparison.Ordinal)), "Do not export unavailable exercise sessions");
    }

    public static async Task BudgetAsync()
    {
        using var harness = new ExportHarness();
        await ExpectFailureAsync(() => new HealthDataCollector(harness.Api, maximumBytes: 1)
            .CollectAsync(new JsonObject(), QueryWindow.Create(Now, Options), Options));
    }

    public static async Task SummaryBudgetAsync()
    {
        using var harness = new ExportHarness(_ => Ok("{\"dataPoints\":[{\"value\":\"" + new string('x', 100) + "\"}]}"));
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options with { AllAvailableData = false }, maximumBytes: 210).RunAsync(Now));
        Check(harness.Uploads == 0 && harness.Urls.Count == 2, "Stop early when combined summaries exceed the remaining budget");
    }

    public static async Task FinalMetadataBudgetAsync()
    {
        using var harness = new ExportHarness();
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options, maximumBytes: 1800).RunAsync(Now));
        Check(harness.Uploads == 0, "Metadata must count toward the final snapshot limit");
    }

    public static async Task SnapshotAsync()
    {
        using var harness = new ExportHarness();
        var counts = await new HealthSyncService(harness.Api, Options).RunAsync(Now);
        Check(harness.Uploads == 1 && harness.Snapshot is not null, "One full snapshot must be uploaded only after all reads");
        Check(harness.Snapshot!["schema_version"]!.GetValue<int>() == 2, "All-data snapshot uses schema v2");
        Check(counts.Count == 49, "All-data export must retain the existing summaries and extra resources");
        Check(harness.Snapshot["collection_statuses"]!.AsObject().Count == 45, "Each data type must have an explicit result");
        Check(harness.Snapshot["source_family"]!.ToString().EndsWith("all-sources", StringComparison.Ordinal), "All-data summaries must include all sources");
        Check(counts.ContainsKey("steps-daily") && counts.ContainsKey("total-calories-daily") && counts.ContainsKey("active-energy-burned-daily"), "Keep distinct daily steps/calorie summaries");
    }

    private static async Task ExpectFailureAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (SyncException exception)
        {
            Check(!exception.Message.Contains("PRIVATE_ERROR", StringComparison.Ordinal), "Raw errors must not be logged");
            return;
        }
        throw new Exception("Expected a safe failure");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Error(int code, string reason) => new((HttpStatusCode)code)
    {
        Content = new StringContent(new JsonObject { ["error"] = new JsonObject {
            ["message"] = "PRIVATE_ERROR", ["details"] = new JsonArray(new JsonObject { ["reason"] = reason })
        } }.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed class DetailedErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(
                "{\"error\":{\"details\":[{\"reason\":\"UNSUPPORTED_DATA_TYPE_ACTION\"},{\"metadata\":{\"detailedReasons\":\"MISSING_OAUTH_SCOPE PRIVATE_ACCOUNT\"}}]}}",
                Encoding.UTF8, "application/json") });
    }

    private sealed class DummyToken : ITokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("dummy");
    }

    private sealed class ExportHarness : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage?> _response;
        private readonly HttpClient _http;
        public GoogleApiClient Api { get; }
        public List<string> Urls { get; } = [];
        public int Uploads { get; private set; }
        public JsonObject? Snapshot { get; private set; }

        public ExportHarness(Func<string, HttpResponseMessage?>? response = null)
        {
            _response = response ?? (_ => null);
            _http = new HttpClient(this, disposeHandler: false);
            Api = new(_http, new Token(), (_, _) => Task.CompletedTask);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (request.Method == HttpMethod.Patch)
            {
                Uploads++;
                Snapshot = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            }
            return _response(url) ?? Ok("{}");
        }

        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
        private sealed class Token : ITokenProvider
        {
            public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("dummy");
        }
    }
}