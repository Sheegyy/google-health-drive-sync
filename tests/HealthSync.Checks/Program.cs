using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HealthSync.Core;
using HealthSync.Functions;
using HealthSync.Tool;
using Microsoft.Azure.Functions.Worker.Core.FunctionMetadata;
using Microsoft.AspNetCore.WebUtilities;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Daily archives save in private history before the latest update", ArchiveChecks.CreateAsync),
    ("Same-day retries reuse archives while later days retain separate files", ArchiveChecks.ReuseAsync),
    ("Archive dates follow the configured timezone across year boundaries", ArchiveChecks.DateBoundaryAsync),
    ("Archiving can be disabled without changing the latest-only write", ArchiveChecks.DisabledAsync),
    ("Archive upload failure preserves the previous latest file", ArchiveChecks.ArchiveFailureAsync),
    ("Archive file creation is not automatically retried", ArchiveChecks.CreateFailureAsync),
    ("Archive destinations reject shared files and invalid identifiers", ArchiveChecks.DestinationSafetyAsync),
    ("Archive search rejects ambiguous, incomplete and mismatched results", ArchiveChecks.SearchSafetyAsync),
    ("Archive search follows pagination before deciding to create a file", ArchiveChecks.SearchPaginationAsync),
    ("Archive folders reject sharing, shared drives and missing privacy metadata", ArchiveChecks.FolderSafetyAsync),
    ("Authorized archive folders can be configured without creating another folder", ArchiveChecks.ConfiguredFolderAsync),
    ("Credential files remove inherited and other-user access before writing", PrivateFileChecks.RunAsync),
    ("All readable v4 types use documented filters and read-only scopes", CatalogChecks.RunAsync),
    ("All-data mode visits every documented type and does not invent empty values", AllDataChecks.AllTypesAsync),
    ("Unavailable types are distinguished from successful empty records", AllDataChecks.StatusesAsync),
    ("Nutrition retains all meals and only their referenced food details", AllDataChecks.FoodReferencesAsync),
    ("Food references cannot redirect authenticated requests or overwrite Drive", AllDataChecks.FoodReferenceSafetyAsync),
    ("Unavailable food references retain all other readable foods with partial status", AllDataChecks.FoodPartialAsync),
    ("Food authentication and server failures preserve the previous snapshot", AllDataChecks.FoodFailureAsync),
    ("Missing full-data consent preserves the previous Drive snapshot", AllDataChecks.AuthorizationFailureAsync),
    ("Full-data server failures preserve the previous Drive snapshot", AllDataChecks.ServerFailureAsync),
    ("ECG records respect the query end even when the API cannot filter it", AllDataChecks.EcgBoundaryAsync),
    ("Full-data connected-device collection includes every page", AllDataChecks.DevicesPaginationAsync),
    ("Travel sessions use padded civil queries and exact UTC result filtering", AllDataChecks.TravelSessionAsync),
    ("Detailed error metadata prioritizes missing consent without leaking values", AllDataChecks.DetailedReasonsAsync),
    ("Page budgets stop before collecting additional oversized results", AllDataChecks.PageBudgetAsync),
    ("Exercise GPS exports preserve validated TCX without extra Drive permissions", AllDataChecks.TcxAsync),
    ("Invalid or external-entity TCX never overwrites Drive", AllDataChecks.TcxValidationAsync),
    ("Unavailable exercise marks dependent TCX as not collected", AllDataChecks.UnavailableExerciseAsync),
    ("Full-data size limits fail rather than silently truncating records", AllDataChecks.BudgetAsync),
    ("Summary collections share one remaining export budget", AllDataChecks.SummaryBudgetAsync),
    ("Final snapshot size includes collection metadata before Drive upload", AllDataChecks.FinalMetadataBudgetAsync),
    ("Full-data snapshots retain all records, statuses and existing daily summaries", AllDataChecks.SnapshotAsync),
    ("Optional API errors expose only structured allowlisted metadata", async () =>
    {
        using var h = new Harness(Error(403, "{\"error\":{\"details\":[{\"reason\":\"API_PRIVATE_PREVIEW_ACCESS_DENIED\",\"metadata\":\"PRIVATE\"}]}}"));
        var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/data", "read"));
        Equal(403, error.StatusCode);
        Equal("API_PRIVATE_PREVIEW_ACCESS_DENIED", error.Reason);
        True(!error.Message.Contains("metadata"));
    }),
    ("TCX downloads share authenticated retry and response size controls", async () =>
    {
        using var h = new Harness(Error(503), Ok("<TrainingCenterDatabase />"));
        var text = await h.Api.SendTextAsync(HttpMethod.Get, "https://example.test/data:exportExerciseTcx?alt=media", "exercise export");
        Equal("<TrainingCenterDatabase />", text);
        Equal(2, h.Requests.Count);
        True(h.Requests.All(request => request.Bearer == "fake-token"));
    }),
    ("All-data collection requests only documented Health read scopes", async () =>
    {
        Equal(9, SyncOptions.HealthScopes.Length);
        True(SyncOptions.HealthScopes.Contains("https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly"));
        True(SyncOptions.HealthScopes.All(scope => scope.EndsWith(".readonly", StringComparison.Ordinal)));
        Equal(1, SyncOptions.DriveScopes.Length);
        Equal("https://www.googleapis.com/auth/drive.file", SyncOptions.DriveScopes[0]);
        await Task.CompletedTask;
    }),
    ("Daily activity uses civil boundaries, one-day windows and distinct calorie types", async () =>
    {
        using var h = new Harness(HealthResponses().Append(Ok("{} ")).ToArray());
        var counts = await h.Service().RunAsync(Now());
        var kinds = new[] { "steps", "total-calories", "active-energy-burned" };
        for (var index = 0; index < kinds.Length; index++)
        {
            var request = h.Requests[6 + index];
            Equal(HttpMethod.Post, request.Method);
            Contains($"/dataTypes/{kinds[index]}/dataPoints:dailyRollUp", request.Url);
            var body = JsonNode.Parse(request.Body!)!;
            Equal(1, body["windowSizeDays"]!.GetValue<int>());
            Equal(14, body["pageSize"]!.GetValue<int>());
            Equal("users/me/dataSourceFamilies/google-wearables", body["dataSourceFamily"]!.ToString());
            Equal(2026, body["range"]!["start"]!["date"]!["year"]!.GetValue<int>());
            Equal(10, body["range"]!["start"]!["date"]!["month"]!.GetValue<int>());
            Equal(12, body["range"]!["start"]!["date"]!["day"]!.GetValue<int>());
            Equal(0, body["range"]!["start"]!["time"]!["hours"]!.GetValue<int>());
            Equal(25, body["range"]!["end"]!["date"]!["day"]!.GetValue<int>());
            Equal(12, body["range"]!["end"]!["time"]!["hours"]!.GetValue<int>());
            Equal(1, counts[$"{kinds[index]}-daily"]);
        }
        var saved = JsonNode.Parse(h.Requests.Last().Body!)!;
        Contains("Total calories include active energy", saved["notes"]!.ToJsonString());
    }),
    ("Daily civil ranges cross years and stop at the actual query time", async () =>
    {
        using var h = new Harness(HealthResponses().Append(Ok("{}")).ToArray());
        await h.Service().RunAsync(DateTimeOffset.Parse("2026-12-31T22:00:07Z"));
        var body = JsonNode.Parse(h.Requests[6].Body!)!;
        Equal(2026, body["range"]!["start"]!["date"]!["year"]!.GetValue<int>());
        Equal(12, body["range"]!["start"]!["date"]!["month"]!.GetValue<int>());
        Equal(19, body["range"]!["start"]!["date"]!["day"]!.GetValue<int>());
        Equal(2027, body["range"]!["end"]!["date"]!["year"]!.GetValue<int>());
        Equal(1, body["range"]!["end"]!["date"]!["month"]!.GetValue<int>());
        Equal(1, body["range"]!["end"]!["date"]!["day"]!.GetValue<int>());
        Equal(7, body["range"]!["end"]!["time"]!["hours"]!.GetValue<int>());
        Equal(7, body["range"]!["end"]!["time"]!["seconds"]!.GetValue<int>());
    }),
    ("Failure in any activity type preserves the previous Drive file", async () =>
    {
        for (var failedType = 0; failedType < 3; failedType++)
        {
            using var h = new Harness(HealthResponses().Take(6)
                .Concat(Enumerable.Range(0, failedType).Select(_ => Ok("{}"))).Append(Error(403, "PRIVATE_ACTIVITY")).ToArray());
            var error = await Fails(() => h.Service().RunAsync(Now()));
            Equal(7 + failedType, h.Requests.Count);
            True(h.Requests.All(request => request.Method != HttpMethod.Patch));
            True(!error.Message.Contains("PRIVATE_ACTIVITY"));
        }
    }),
    ("Daily activity pagination retains the same civil range", async () =>
    {
        using var h = new Harness(HealthResponses().Take(6).Concat(new[] {
            Ok("{\"rollupDataPoints\":[{\"steps\":{\"countSum\":\"10\"}}],\"nextPageToken\":\"next-day\"}"),
            Ok("{\"rollupDataPoints\":[{\"steps\":{\"countSum\":\"20\"}}]}"), Ok("{}"), Ok("{}"), Ok("{}")
        }).ToArray());
        var counts = await h.Service().RunAsync(Now());
        Equal(2, counts["steps-daily"]);
        var first = JsonNode.Parse(h.Requests[6].Body!)!;
        var next = JsonNode.Parse(h.Requests[7].Body!)!;
        Equal("next-day", next["pageToken"]!.ToString());
        True(JsonNode.DeepEquals(first["range"], next["range"]));
        Equal(1, next["windowSizeDays"]!.GetValue<int>());
    }),
    ("Daily buckets preserve measured zero separately from an unset value", async () =>
    {
        using var h = new Harness(HealthResponses().Take(6).Concat(new[] {
            Ok("{\"rollupDataPoints\":[{\"steps\":{\"countSum\":\"0\"}},{}]}"), Ok("{}"), Ok("{}"), Ok("{}")
        }).ToArray());
        await h.Service().RunAsync(Now());
        var saved = JsonNode.Parse(h.Requests.Last().Body!)!;
        Equal("0", saved["records"]!["steps-daily"]![0]!["steps"]!["countSum"]!.ToString());
        True(saved["records"]!["steps-daily"]![1]!["steps"] is null);
    }),
    ("Malformed activity arrays prevent Drive update", async () =>
    {
        using var h = new Harness(HealthResponses().Take(6).Append(Ok("{\"rollupDataPoints\":[null]}")).ToArray());
        Contains("malformed rollupDataPoints", (await Fails(() => h.Service().RunAsync(Now()))).Message);
        True(h.Requests.All(request => request.Method != HttpMethod.Patch));
    }),
    ("Read-only daily rollup POST can retry", async () =>
    {
        using var h = new Harness(Error(503), Ok("{}"));
        await h.Api.SendAsync(HttpMethod.Post, "https://example.test/data:dailyRollUp", "daily rollup", new JsonObject());
        Equal(2, h.Requests.Count);
    }),
    ("Health and Drive request separate access-token scopes and retain separate caches", async () =>
    {
        using var h = new Harness(
            Ok(new JsonObject { ["access_token"] = "health-only", ["token_type"] = "Bearer", ["expires_in"] = 3600,
                ["scope"] = string.Join(" ", SyncOptions.HealthScopes) }.ToJsonString()), Ok("{}"),
            Ok(new JsonObject { ["access_token"] = "drive-only", ["token_type"] = "Bearer", ["expires_in"] = 3600,
                ["scope"] = string.Join(" ", SyncOptions.DriveScopes) }.ToJsonString()), Ok("{}"), Ok("{}"), Ok("{}"));
        using var tokens = new GoogleTokenProvider(Options(), h.Http);
        var api = new GoogleApiClient(h.Http, tokens);
        await api.SendAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/identity", "profile");
        await api.SendAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/target_file", "metadata");
        await api.SendAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/identity", "profile again");
        await api.SendAsync(HttpMethod.Patch, "https://www.googleapis.com/upload/drive/v3/files/target_file", "upload", new JsonObject());
        Equal(6, h.Requests.Count);
        Equal("https://oauth2.googleapis.com/token", h.Requests[0].Url);
        Equal(string.Join(" ", SyncOptions.HealthScopes), QueryHelpers.ParseQuery(h.Requests[0].Body!)["scope"].ToString());
        Equal(string.Join(" ", SyncOptions.DriveScopes), QueryHelpers.ParseQuery(h.Requests[2].Body!)["scope"].ToString());
        Equal("refresh_token", QueryHelpers.ParseQuery(h.Requests[0].Body!)["grant_type"].ToString());
        Equal("health-only", h.Requests[1].Bearer);
        Equal("drive-only", h.Requests[3].Bearer);
        Equal("health-only", h.Requests[4].Bearer);
        Equal("drive-only", h.Requests[5].Bearer);
    }),
    ("Real token provider refuses unapproved endpoints before refreshing credentials", async () =>
    {
        using var h = new Harness();
        using var tokens = new GoogleTokenProvider(Options(), h.Http);
        foreach (var url in new[] { "http://health.googleapis.com/v4/users/me/identity", "https://example.test/data",
            "https://health.googleapis.com.example.test/data", "https://www.googleapis.com/gmail/v1/users/me" })
            await Fails(() => tokens.GetAsync(url, CancellationToken.None));
        Equal(0, h.Requests.Count);
    }),
    ("Token refresh rejects a broader returned scope set without exposing its token", async () =>
    {
        using var h = new Harness(Ok(new JsonObject { ["access_token"] = "PRIVATE_TOKEN", ["token_type"] = "Bearer",
            ["expires_in"] = 3600, ["scope"] = string.Join(" ", SyncOptions.Scopes) }.ToJsonString()));
        using var tokens = new GoogleTokenProvider(Options(), h.Http);
        var error = await Fails(() => tokens.GetAsync(CancellationToken.None));
        Contains("Google token refresh failed", error.Message);
        True(!error.Message.Contains("PRIVATE_TOKEN"));
        Equal(1, h.Requests.Count);
    }),
    ("Known Google error reasons are classified without exposing response details", async () =>
    {
        foreach (var reason in new[] { "ACCESS_TOKEN_SCOPE_INSUFFICIENT", "SERVICE_DISABLED", "ACCOUNT_NOT_LINKED", "BILLING_DISABLED", "DISALLOWED_OAUTH_SCOPES" })
        {
            using var h = new Harness(Error(403, new JsonObject { ["error"] = new JsonObject {
                ["message"] = "PRIVATE_ACCOUNT_DETAILS", ["details"] = new JsonArray(new JsonObject { ["reason"] = reason, ["metadata"] = "PRIVATE_METADATA" })
            } }.ToJsonString()));
            var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/identity", "profile"));
            Contains($"({reason})", error.Message);
            True(!error.Message.Contains("PRIVATE_"));
        }
    }),
    ("Google Health linkage error is classified without its raw message", async () =>
    {
        using var h = new Harness(Error(403, "{\"error\":{\"message\":\"Unable to create UberMint from GaiaMint PRIVATE_ACCOUNT\"}}"));
        var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/identity", "profile"));
        Contains("GOOGLE_HEALTH_LINK_REQUIRED", error.Message);
        True(!error.Message.Contains("PRIVATE_ACCOUNT"));
        True(!error.Message.Contains("GaiaMint"));
    }),
    ("Unknown or malformed error diagnostics remain sanitized", async () =>
    {
        foreach (var body in new[] { "PRIVATE_BODY", "[]", "{\"error\":\"PRIVATE_BODY\"}",
            "{\"error\":{\"details\":[null,{\"reason\":\"PRIVATE_REASON\"}],\"message\":\"PRIVATE_BODY\"}}" })
        {
            using var h = new Harness(Error(403, body));
            var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/identity", "profile"));
            Contains("HTTP 403", error.Message);
            True(!error.Message.Contains("PRIVATE_"));
        }
    }),
    ("Selected folder is validated and JSON creation uses that parent", async () =>
    {
        using var h = new Harness(
            Ok("{\"id\":\"chosen_folder\",\"mimeType\":\"application/vnd.google-apps.folder\",\"capabilities\":{\"canAddChildren\":true}}"),
            Ok("{\"id\":\"created_json\",\"mimeType\":\"application/json\",\"parents\":[\"chosen_folder\"]}"),
            Ok("{\"id\":\"created_json\"}"));
        var metadata = await new DriveFileSetup(h.Api).ResolveAsync(folderId: "chosen_folder");
        Equal("created_json", metadata["id"]!.ToString());
        Equal(3, h.Requests.Count);
        Equal(HttpMethod.Get, h.Requests[0].Method);
        Contains("/chosen_folder?", h.Requests[0].Url);
        var created = JsonNode.Parse(h.Requests[1].Body!)!;
        Equal("application/json", created["mimeType"]!.ToString());
        Equal("chosen_folder", created["parents"]![0]!.ToString());
        Contains("/created_json?uploadType=media", h.Requests[2].Url);
        True(h.Requests.All(request => request.Url.Contains("supportsAllDrives=true")));
    }),
    ("Default bootstrap still creates a new Health Sync folder", async () =>
    {
        using var h = new Harness(Ok("{\"id\":\"new_folder\"}"),
            Ok("{\"id\":\"created_json\",\"mimeType\":\"application/json\",\"parents\":[\"new_folder\"]}"), Ok("{}"));
        await new DriveFileSetup(h.Api).ResolveAsync();
        Equal("Health Sync", JsonNode.Parse(h.Requests[0].Body!)!["name"]!.ToString());
        Equal("new_folder", JsonNode.Parse(h.Requests[1].Body!)!["parents"]![0]!.ToString());
    }),
    ("Bootstrap reuses the JSON in the requested folder without resetting content", async () =>
    {
        using var h = new Harness(Ok("{\"id\":\"existing_json\",\"mimeType\":\"application/json\",\"parents\":[\"chosen_folder\"],\"capabilities\":{\"canEdit\":true}}"));
        var metadata = await new DriveFileSetup(h.Api).ResolveAsync("existing_json", "chosen_folder");
        Equal("existing_json", metadata["id"]!.ToString());
        Equal(1, h.Requests.Count);
        Equal(HttpMethod.Get, h.Requests[0].Method);
    }),
    ("Bootstrap will not move an existing JSON from another folder", async () =>
    {
        using var h = new Harness(Ok("{\"id\":\"existing_json\",\"mimeType\":\"application/json\",\"parents\":[\"other_folder\"]}"));
        Contains("outside the requested folder", (await Fails(() => new DriveFileSetup(h.Api).ResolveAsync("existing_json", "chosen_folder"))).Message);
        Equal(1, h.Requests.Count);
    }),
    ("Folder access failure does not fall back to a different upload location", async () =>
    {
        using var h = new Harness(Error(404, "PRIVATE_FOLDER_DETAILS"));
        var error = await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "chosen_folder"));
        Contains("Google Picker", error.Message);
        True(!error.Message.Contains("PRIVATE_FOLDER_DETAILS"));
        Equal(1, h.Requests.Count);
    }),
    ("Read-only or trashed target folders stop before JSON creation", async () =>
    {
        foreach (var folder in new[] {
            "{\"id\":\"chosen_folder\",\"mimeType\":\"application/vnd.google-apps.folder\",\"capabilities\":{\"canAddChildren\":false}}",
            "{\"id\":\"chosen_folder\",\"mimeType\":\"application/vnd.google-apps.folder\",\"trashed\":true,\"capabilities\":{\"canAddChildren\":true}}" })
        {
            using var h = new Harness(Ok(folder));
            await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "chosen_folder"));
            Equal(1, h.Requests.Count);
        }
    }),
    ("A file ID is not accepted as the upload folder", async () =>
    {
        using var h = new Harness(Ok("{\"id\":\"not_a_folder\",\"mimeType\":\"application/json\"}"));
        Contains("not an active folder", (await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "not_a_folder"))).Message);
        Equal(1, h.Requests.Count);
    }),
    ("Folder IDs and file IDs reject URLs before any API calls", async () =>
    {
        using var h = new Harness();
        await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "https://drive.google.com/drive/folders/example"));
        await Fails(() => new DriveFileSetup(h.Api).ResolveAsync("https://drive.google.com/file/d/example/view"));
        Equal(0, h.Requests.Count);
    }),
    ("Existing folders or read-only JSON targets are never overwritten", async () =>
    {
        foreach (var target in new[] {
            "{\"id\":\"existing_target\",\"mimeType\":\"application/vnd.google-apps.folder\"}",
            "{\"id\":\"existing_target\",\"mimeType\":\"application/json\",\"capabilities\":{\"canEdit\":false}}" })
        {
            using var h = new Harness(Ok(target));
            await Fails(() => new DriveFileSetup(h.Api).ResolveAsync("existing_target"));
            Equal(1, h.Requests.Count);
        }
    }),
    ("Malformed create responses never initialize the folder as a JSON file", async () =>
    {
        using var h = new Harness(
            Ok("{\"id\":\"chosen_folder\",\"mimeType\":\"application/vnd.google-apps.folder\",\"capabilities\":{\"canAddChildren\":true}}"),
            Ok("{\"id\":\"chosen_folder\",\"mimeType\":\"application/json\"}"));
        await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "chosen_folder"));
        Equal(2, h.Requests.Count);
        True(h.Requests.All(request => request.Method != HttpMethod.Patch));
    }),
    ("Created JSON must have the requested parent before content is written", async () =>
    {
        using var h = new Harness(
            Ok("{\"id\":\"chosen_folder\",\"mimeType\":\"application/vnd.google-apps.folder\",\"capabilities\":{\"canAddChildren\":true}}"),
            Ok("{\"id\":\"created_json\",\"mimeType\":\"application/json\",\"parents\":[\"other_folder\"]}"));
        Contains("not in the requested folder", (await Fails(() => new DriveFileSetup(h.Api).ResolveAsync(folderId: "chosen_folder"))).Message);
        Equal(2, h.Requests.Count);
        True(h.Requests.All(request => request.Method != HttpMethod.Patch));
    }),
    ("Generated worker metadata registers only the monitored timer", async () =>
    {
        var providerType = typeof(HealthSyncFunction).Assembly.GetTypes().Single(type =>
            typeof(IFunctionMetadataProvider).IsAssignableFrom(type) && !type.IsAbstract);
        var provider = (IFunctionMetadataProvider)Activator.CreateInstance(providerType, nonPublic: true)!;
        var metadata = (await provider.GetFunctionMetadataAsync(string.Empty)).Single();
        Equal("HealthSyncToDrive", metadata.Name);
        var bindings = metadata.RawBindings ?? throw new Exception("Generated bindings are missing");
        var binding = bindings.Select(raw => JsonNode.Parse(raw)!).Single();
        Equal("timerTrigger", binding["type"]!.ToString());
        Equal("%HEALTH_SYNC_SCHEDULE%", binding["schedule"]!.ToString());
        Equal(false, binding["runOnStartup"]!.GetValue<bool>());
        Equal(true, binding["useMonitor"]!.GetValue<bool>());
    }),
    ("Daily UTC schedule is 07:00 JST across date boundaries", async () =>
    {
        Equal("0 0 22 * * *", SyncOptions.DailyScheduleUtc);
        var zone = SyncOptions.FindZone("Asia/Tokyo");
        foreach (var date in new[] { new DateOnly(2026, 10, 25), new DateOnly(2026, 12, 31), new DateOnly(2028, 2, 29) })
        {
            var scheduledUtc = new DateTimeOffset(date.ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero);
            var local = TimeZoneInfo.ConvertTime(scheduledUtc, zone);
            Equal(new TimeOnly(7, 0), TimeOnly.FromDateTime(local.DateTime));
            Equal(date.AddDays(1), DateOnly.FromDateTime(local.DateTime));
        }
        await Task.CompletedTask;
    }),
    ("JST date boundary and 14-day window", async () =>
    {
        var range = QueryWindow.Create(DateTimeOffset.Parse("2026-10-25T16:00:00Z"), Options());
        Equal("2026-10-12T15:00:00Z", range.StartUtc);
        Equal("2026-10-13", range.FirstDate);
        Equal("2026-10-27", range.EndDateExclusive);
        await Task.CompletedTask;
    }),
    ("Successful sync: wake-date filter, daily fields, source family and hourly rollup", async () =>
    {
        using var h = new Harness(HealthResponses().Append(Ok("{\"id\":\"target_file\"}")).ToArray());
        var counts = await h.Service().RunAsync(Now());
        Equal(10, h.Requests.Count);
        Equal(1, counts["sleep"]);
        Equal(1, counts["heart-rate-hourly"]);
        Contains("sleep.interval.end_time", Uri.UnescapeDataString(h.Requests[0].Url));
        Contains("daily_resting_heart_rate.date", Uri.UnescapeDataString(h.Requests[1].Url));
        True(h.Requests.Take(5).All(r => Uri.UnescapeDataString(r.Url).Contains("users/me/dataSourceFamilies/google-wearables")));
        var rollup = JsonNode.Parse(h.Requests[5].Body!)!;
        Equal("3600s", rollup["windowSize"]!.ToString());
        Equal("users/me/dataSourceFamilies/google-wearables", rollup["dataSourceFamily"]!.ToString());
        Equal(HttpMethod.Patch, h.Requests[9].Method);
        Contains("/target_file?uploadType=media&fields=id", h.Requests[9].Url);
        Contains("supportsAllDrives=true", h.Requests[9].Url);
        var saved = JsonNode.Parse(h.Requests[9].Body!)!;
        Equal("wake/end date", saved["sleep_day_basis"]!.ToString());
        Equal(1, saved["record_counts"]!["sleep"]!.GetValue<int>());
    }),
    ("Early Health failure preserves previous Drive file", async () =>
    {
        using var h = new Harness(Error(403, "HEALTH_PRIVATE_VALUE"));
        var error = await Fails(() => h.Service().RunAsync(Now()));
        Equal(1, h.Requests.Count);
        True(!error.Message.Contains("HEALTH_PRIVATE_VALUE"));
        True(h.Requests.All(r => r.Method != HttpMethod.Patch));
    }),
    ("Later daily failure prevents Drive update", async () =>
    {
        using var h = new Harness(Ok("{}"), Ok("{}"), Error(403));
        await Fails(() => h.Service().RunAsync(Now()));
        Equal(3, h.Requests.Count);
        True(h.Requests.All(r => r.Method != HttpMethod.Patch));
    }),
    ("Hourly failure prevents Drive update", async () =>
    {
        using var h = new Harness(Enumerable.Range(0, 5).Select(_ => Ok("{}")).Append(Error(403)).ToArray());
        await Fails(() => h.Service().RunAsync(Now()));
        True(h.Requests.All(r => r.Method != HttpMethod.Patch));
    }),
    ("Drive failure is surfaced", async () =>
    {
        using var h = new Harness(HealthResponses().Append(Error(403)).ToArray());
        Contains("Drive update: HTTP 403", (await Fails(() => h.Service().RunAsync(Now()))).Message);
        Equal(10, h.Requests.Count);
    }),
    ("GET pagination collects both pages and encodes token", async () =>
    {
        using var h = new Harness(Ok("{\"dataPoints\":[{\"n\":1}],\"nextPageToken\":\"a+b/c\"}"),
            Ok("{\"dataPoints\":[{\"n\":2}]}"));
        var rows = await h.Api.PagedAsync(HttpMethod.Get, "https://example.test/data", new(), "dataPoints", "paged");
        Equal(2, rows.Count);
        Contains("pageToken=a%2Bb%2Fc", h.Requests[1].Url);
        Equal(2, rows[1]!["n"]!.GetValue<int>());
    }),
    ("Repeated page token fails instead of looping", async () =>
    {
        using var h = new Harness(Ok("{\"nextPageToken\":\"same\"}"), Ok("{\"nextPageToken\":\"same\"}"));
        Contains("repeated page token", (await Fails(() => h.Api.PagedAsync(HttpMethod.Get,
            "https://example.test/data", new(), "dataPoints", "paged"))).Message);
        Equal(2, h.Requests.Count);
    }),
    ("POST rollup pagination passes page token in body", async () =>
    {
        using var h = new Harness(Ok("{\"rollupDataPoints\":[{}],\"nextPageToken\":\"next\"}"),
            Ok("{\"rollupDataPoints\":[{}]}"));
        var rows = await h.Api.PagedAsync(HttpMethod.Post, "https://example.test/data:rollUp",
            new JsonObject { ["windowSize"] = "3600s" }, "rollupDataPoints", "rollup");
        Equal(2, rows.Count);
        Equal("next", JsonNode.Parse(h.Requests[1].Body!)!["pageToken"]!.ToString());
        Equal("3600s", JsonNode.Parse(h.Requests[1].Body!)!["windowSize"]!.ToString());
    }),
    ("429 retries with Retry-After and fresh request", async () =>
    {
        var retry = Error(429);
        retry.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
        using var h = new Harness(retry, Ok("{}"));
        await h.Api.SendAsync(HttpMethod.Get, "https://example.test/data", "read");
        Equal(2, h.Requests.Count);
        Equal(TimeSpan.FromSeconds(7), h.Delays.Single());
        True(h.Requests.All(r => r.Bearer == "fake-token"));
    }),
    ("403 does not retry or expose response body", async () =>
    {
        using var h = new Harness(Error(403, "SECRET_TOKEN_AND_HEALTH"));
        var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/data", "read"));
        Equal(1, h.Requests.Count);
        True(!error.Message.Contains("SECRET_TOKEN_AND_HEALTH"));
    }),
    ("Drive creation POST is not automatically retried", async () =>
    {
        using var h = new Harness(Error(503));
        await Fails(() => h.Api.SendAsync(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files", "create", new JsonObject()));
        Equal(1, h.Requests.Count);
    }),
    ("Read-only rollup POST can retry", async () =>
    {
        using var h = new Harness(Error(503), Ok("{}"));
        await h.Api.SendAsync(HttpMethod.Post, "https://example.test/data:rollUp", "rollup", new JsonObject());
        Equal(2, h.Requests.Count);
    }),
    ("Invalid JSON is sanitized", async () =>
    {
        using var h = new Harness(Ok("PRIVATE_INVALID_JSON"));
        var error = await Fails(() => h.Api.SendAsync(HttpMethod.Get, "https://example.test/data", "read"));
        Contains("invalid JSON", error.Message);
        True(!error.Message.Contains("PRIVATE_INVALID_JSON"));
    }),
    ("Empty midnight interval skips sleep and hourly requests", async () =>
    {
        using var h = new Harness(Enumerable.Range(0, 5).Select(_ => Ok("{}")).ToArray());
        var counts = await h.Service(Options() with { Days = 1 }).RunAsync(DateTimeOffset.Parse("2026-10-24T15:00:00Z"));
        Equal(5, h.Requests.Count);
        Equal(0, counts["sleep"]);
        Equal(0, counts["heart-rate-hourly"]);
        Equal(0, counts["steps-daily"]);
        Equal(0, counts["total-calories-daily"]);
        Equal(0, counts["active-energy-burned-daily"]);
        True(h.Requests.Take(4).All(r => r.Url.Contains("/dataTypes/daily-")));
    }),
    ("Hourly can be disabled and empty arrays remain empty", async () =>
    {
        using var h = new Harness(Enumerable.Range(0, 9).Select(_ => Ok("{}")).ToArray());
        var counts = await h.Service(Options() with { HourlyHeartRate = false }).RunAsync(Now());
        True(!counts.ContainsKey("heart-rate-hourly"));
        Equal(0, counts["sleep"]);
        var saved = JsonNode.Parse(h.Requests.Last().Body!)!;
        Equal(8, saved["empty_data_types"]!.AsArray().Count);
        Equal(0, saved["records"]!["sleep"]!.AsArray().Count);
    }),
    ("Malformed record arrays prevent Drive update", async () =>
    {
        using var h = new Harness(Ok("{\"dataPoints\":[null]}"));
        Contains("malformed dataPoints", (await Fails(() => h.Service().RunAsync(Now()))).Message);
        Equal(1, h.Requests.Count);
    }),
    ("Unresolved Key Vault reference fails configuration", async () =>
    {
        var names = new[] { "DRIVE_FILE_ID", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET", "GOOGLE_REFRESH_TOKEN" };
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "test");
            Environment.SetEnvironmentVariable("GOOGLE_REFRESH_TOKEN", "@Microsoft.KeyVault(SecretUri=unresolved)");
            var error = await Fails(() => Task.FromResult(SyncOptions.FromEnvironment()));
            Contains("GOOGLE_REFRESH_TOKEN", error.Message);
        }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed");
Environment.ExitCode = failures == 0 ? 0 : 1;

static SyncOptions Options() => new("client", "secret", "refresh", "target_file");
static DateTimeOffset Now() => DateTimeOffset.Parse("2026-10-25T03:00:00Z");
static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static HttpResponseMessage Error(int status, string text = "api error") => new((HttpStatusCode)status) { Content = new StringContent(text) };
static IEnumerable<HttpResponseMessage> HealthResponses()
{
    for (var i = 0; i < 5; i++) yield return Ok("{\"dataPoints\":[{\"data\":\"sample\"}]}");
    yield return Ok("{\"rollupDataPoints\":[{\"data\":\"hour\"}]}");
    for (var index = 0; index < 3; index++) yield return Ok("{\"rollupDataPoints\":[{\"data\":\"activity\"}]}");
}
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new Exception($"Missing: {expected}"); }
static async Task<SyncException> Fails(Func<Task> action)
{
    try { await action(); }
    catch (SyncException ex) { return ex; }
    throw new Exception("Expected SyncException");
}

sealed record CapturedRequest(HttpMethod Method, string Url, string? Body, string? Bearer);
sealed class FakeToken : ITokenProvider
{
    public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("fake-token");
}
sealed class Harness : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    private readonly HttpClient _http;
    public List<CapturedRequest> Requests { get; } = [];
    public List<TimeSpan> Delays { get; } = [];
    public GoogleApiClient Api { get; }
    public HttpClient Http => _http;
    public Harness(params HttpResponseMessage[] responses)
    {
        _responses = new(responses);
        _http = new HttpClient(this, disposeHandler: false);
        Api = new(_http, new FakeToken(), (delay, _) => { Delays.Add(delay); return Task.CompletedTask; });
    }
    public HealthSyncService Service(SyncOptions? options = null) => new(Api, options ?? new("client", "secret", "refresh", "target_file"));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new(request.Method, request.RequestUri!.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.Parameter));
        if (_responses.Count == 0) throw new Exception("Unexpected HTTP request");
        return _responses.Dequeue();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _http.Dispose(); foreach (var response in _responses) response.Dispose(); }
        base.Dispose(disposing);
    }
}
