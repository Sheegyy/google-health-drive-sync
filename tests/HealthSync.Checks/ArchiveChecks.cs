using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HealthSync.Core;

internal static class ArchiveChecks
{
    private static readonly SyncOptions Options = new("client", "secret", "refresh", "latest_file", ArchiveSnapshots: true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T03:00:00Z");

    public static async Task CreateAsync()
    {
        using var harness = new ArchiveHarness();
        await harness.Writer().WriteAsync(new JsonObject { ["test"] = "snapshot" }, Now);
        Check(harness.Created.Count == 1 && harness.Created[0]["name"]!.ToString() == "health_sync_2026-10-25.json", "Create one dated snapshot");
        Check(harness.Created[0]["parents"]![0]!.ToString() == "folder_1" && harness.CreatedFolders.Count == 1,
            "Create daily snapshots in the private history folder");
        Check(harness.Uploads.SequenceEqual(new[] { "archive_1", "latest_file" }), "Archive must be saved before latest");
        Check(harness.Payloads.All(payload => payload["test"]!.ToString() == "snapshot"), "Keep identical history and latest data");
        Check(harness.Urls.All(url => !url.Contains("permissions", StringComparison.Ordinal)), "Never add sharing permissions");
    }

    public static async Task ReuseAsync()
    {
        using var harness = new ArchiveHarness();
        await harness.Writer().WriteAsync(new JsonObject(), Now);
        await harness.Writer().WriteAsync(new JsonObject { ["retry"] = true }, Now.AddMinutes(1));
        await harness.Writer().WriteAsync(new JsonObject(), Now.AddDays(1));
        Check(harness.Created.Count == 2, "A same-day rerun must reuse its archive while the next day creates a new one");
        Check(harness.CreatedFolders.Count == 1, "Reuse the history folder on later runs");
        Check(harness.Uploads.SequenceEqual(new[] { "archive_1", "latest_file", "archive_1", "latest_file", "archive_2", "latest_file" }), "Do not overwrite earlier daily archives");
    }

    public static async Task DateBoundaryAsync()
    {
        using var harness = new ArchiveHarness();
        await harness.Writer().WriteAsync(new JsonObject(), DateTimeOffset.Parse("2026-12-31T14:59:00Z"));
        await harness.Writer().WriteAsync(new JsonObject(), DateTimeOffset.Parse("2026-12-31T15:01:00Z"));
        Check(harness.Created[0]["name"]!.ToString() == "health_sync_2026-12-31.json" &&
            harness.Created[1]["name"]!.ToString() == "health_sync_2027-01-01.json", "Use the configured timezone across year boundaries");
    }

    public static async Task DisabledAsync()
    {
        using var harness = new ArchiveHarness();
        await harness.Writer(Options with { ArchiveSnapshots = false }).WriteAsync(new JsonObject(), Now);
        Check(harness.Urls.Count == 1 && harness.Uploads.SequenceEqual(new[] { "latest_file" }), "Disabled archiving keeps the original one-file update");
    }

    public static async Task ArchiveFailureAsync()
    {
        using var harness = new ArchiveHarness { FailArchive = true };
        await ExpectFailureAsync(() => new HealthSyncService(harness.Api, Options).RunAsync(Now));
        Check(!harness.Uploads.Contains("latest_file"), "Archive failure must preserve the previous latest snapshot");
    }

    public static async Task CreateFailureAsync()
    {
        using var harness = new ArchiveHarness { FailCreate = true };
        await ExpectFailureAsync(() => harness.Writer().WriteAsync(new JsonObject(), Now));
        Check(harness.CreateAttempts == 1 && harness.Uploads.Count == 0, "Do not retry a non-idempotent file create or update latest after failure");
    }

    public static async Task DestinationSafetyAsync()
    {
        foreach (var invalid in new[] { "shared", "folder", "parent", "id", "read-only", "shared-drive", "missing-shared" })
        {
            using var harness = new ArchiveHarness { InvalidLatest = invalid };
            await ExpectFailureAsync(() => harness.Writer().WriteAsync(new JsonObject(), Now));
            Check(harness.CreateAttempts == 0 && harness.Uploads.Count == 0, "Invalid or shared destination must stop before creating archives");
        }
    }

    public static async Task SearchSafetyAsync()
    {
        foreach (var mode in new[] { "duplicate", "incomplete", "malformed", "wrong-parent", "wrong-source", "latest-alias", "shared-archive" })
        {
            using var harness = new ArchiveHarness { SearchMode = mode };
            await ExpectFailureAsync(() => harness.Writer().WriteAsync(new JsonObject(), Now));
            Check(harness.CreateAttempts == 0 && harness.Uploads.Count == 0, "Ambiguous or mismatched search results must never be overwritten");
        }
    }

    public static async Task SearchPaginationAsync()
    {
        using var harness = new ArchiveHarness { SearchMode = "second-page" };
        await harness.Writer().WriteAsync(new JsonObject(), Now);
        Check(harness.CreateAttempts == 0 && harness.Uploads[0] == "archive_existing", "An existing archive on a later page must be reused");
    }

    public static async Task FolderSafetyAsync()
    {
        foreach (var invalid in new[] { "shared", "shared-drive", "missing-shared", "read-only", "wrong-id", "not-folder" })
        {
            using var harness = new ArchiveHarness { InvalidFolder = invalid };
            await ExpectFailureAsync(() => harness.Writer(Options with { ArchiveFolderId = "folder_1" }).WriteAsync(new JsonObject(), Now));
            Check(harness.Created.Count == 0 && harness.CreatedFolders.Count == 0 && harness.Uploads.Count == 0,
                "Never upload to an inaccessible, shared or invalid history folder");
        }
    }

    public static async Task ConfiguredFolderAsync()
    {
        using var harness = new ArchiveHarness();
        await harness.Writer(Options with { ArchiveFolderId = "folder_1" }).WriteAsync(new JsonObject(), Now);
        Check(harness.CreatedFolders.Count == 0 && harness.Created.Count == 1, "Use an authorized configured folder without creating another one");
    }

    private static async Task ExpectFailureAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (SyncException exception)
        {
            Check(!exception.Message.Contains("PRIVATE", StringComparison.Ordinal), "Do not expose raw Drive errors");
            return;
        }
        throw new Exception("Expected a safe archive failure");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class ArchiveHarness : HttpMessageHandler
    {
        private readonly HttpClient _http;
        private readonly List<JsonObject> _archives = [];
        private readonly List<JsonObject> _folders = [];
        public GoogleApiClient Api { get; }
        public List<string> Urls { get; } = [];
        public List<JsonObject> Created { get; } = [];
        public List<JsonObject> CreatedFolders { get; } = [];
        public List<string> Uploads { get; } = [];
        public List<JsonObject> Payloads { get; } = [];
        public bool FailArchive { get; init; }
        public bool FailCreate { get; init; }
        public string? InvalidLatest { get; init; }
        public string? InvalidFolder { get; init; }
        public string? SearchMode { get; init; }
        public int CreateAttempts { get; private set; }

        public ArchiveHarness()
        {
            _http = new HttpClient(this, disposeHandler: false);
            Api = new(_http, new Token(), (_, _) => Task.CompletedTask);
        }

        public DriveSnapshotWriter Writer(SyncOptions? options = null) => new(Api, options ?? Options);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (request.Method == HttpMethod.Patch)
            {
                var identifier = request.RequestUri.AbsolutePath.Split('/')[^1];
                Uploads.Add(identifier);
                Payloads.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
                return FailArchive && identifier != "latest_file" ? Error(HttpStatusCode.Forbidden) : Ok(new JsonObject { ["id"] = identifier });
            }
            if (url.Contains("/drive/v3/files/latest_file?", StringComparison.Ordinal))
            {
                var file = Metadata("latest_file", "2026-10-25");
                if (InvalidLatest == "shared") file["shared"] = true;
                if (InvalidLatest == "folder") file["mimeType"] = "application/vnd.google-apps.folder";
                if (InvalidLatest == "parent") file["parents"] = new JsonArray("unsafe'parent");
                if (InvalidLatest == "id") file["id"] = "other_file";
                if (InvalidLatest == "read-only") file["capabilities"]!["canEdit"] = false;
                if (InvalidLatest == "shared-drive") { file["driveId"] = "shared_drive"; file.Remove("shared"); }
                if (InvalidLatest == "missing-shared") file.Remove("shared");
                return Ok(file);
            }
            if (url.Contains("/drive/v3/files/folder_1?", StringComparison.Ordinal))
            {
                var folder = FolderMetadata();
                if (InvalidFolder == "shared") folder["shared"] = true;
                if (InvalidFolder == "shared-drive") { folder["driveId"] = "shared_drive"; folder.Remove("shared"); }
                if (InvalidFolder == "missing-shared") folder.Remove("shared");
                if (InvalidFolder == "read-only") folder["capabilities"]!["canAddChildren"] = false;
                if (InvalidFolder == "wrong-id") folder["id"] = "other_folder";
                if (InvalidFolder == "not-folder") folder["mimeType"] = "application/json";
                return Ok(folder);
            }
            if (request.Method == HttpMethod.Post && url.Contains("/drive/v3/files?", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                if (body["mimeType"]?.ToString() == "application/vnd.google-apps.folder")
                {
                    CreatedFolders.Add((JsonObject)body.DeepClone());
                    var folder = FolderMetadata();
                    _folders.Add(folder);
                    return Ok(folder);
                }
                CreateAttempts++;
                if (FailCreate) return Error(HttpStatusCode.ServiceUnavailable);
                Created.Add((JsonObject)body.DeepClone());
                var metadata = Metadata($"archive_{Created.Count}", body["appProperties"]!["healthsyncDate"]!.ToString());
                _archives.Add(metadata);
                return Ok(metadata);
            }
            if (url.Contains("/drive/v3/files?", StringComparison.Ordinal))
            {
                var decoded = Uri.UnescapeDataString(url);
                if (decoded.Contains("healthsyncKind", StringComparison.Ordinal))
                    return Ok(new JsonObject { ["files"] = new JsonArray(_folders.Select(folder => folder.DeepClone()).ToArray()) });
                var date = System.Text.RegularExpressions.Regex.Match(decoded, "healthsyncDate' and value='([0-9-]+)'").Groups[1].Value;
                var files = new JsonArray(_archives.Where(file => file["appProperties"]!["healthsyncDate"]!.ToString() == date)
                    .Select(file => file.DeepClone()).ToArray());
                if (SearchMode == "incomplete") return Ok(new JsonObject { ["incompleteSearch"] = true });
                if (SearchMode == "malformed") return Ok(new JsonObject { ["files"] = "PRIVATE" });
                if (SearchMode == "second-page" && !url.Contains("pageToken=second", StringComparison.Ordinal))
                    return Ok(new JsonObject { ["nextPageToken"] = "second" });
                if (SearchMode is not null)
                {
                    var file = Metadata("archive_existing", date);
                    if (SearchMode == "wrong-parent") file["parents"] = new JsonArray("other_folder");
                    if (SearchMode == "wrong-source") file["appProperties"]!["healthsyncSource"] = "other_file";
                    if (SearchMode == "latest-alias") file["id"] = "latest_file";
                    if (SearchMode == "shared-archive") file["shared"] = true;
                    files.Add(file);
                    if (SearchMode == "duplicate") files.Add(file.DeepClone());
                }
                return Ok(new JsonObject { ["files"] = files });
            }
            return Ok(new JsonObject());
        }

        private static JsonObject Metadata(string identifier, string date) => new()
        {
            ["id"] = identifier, ["mimeType"] = "application/json", ["parents"] = new JsonArray("folder_1"),
            ["shared"] = false, ["capabilities"] = new JsonObject { ["canEdit"] = true },
            ["appProperties"] = new JsonObject { ["healthsyncSource"] = "latest_file", ["healthsyncDate"] = date }
        };

        private static JsonObject FolderMetadata() => new()
        {
            ["id"] = "folder_1", ["mimeType"] = "application/vnd.google-apps.folder", ["shared"] = false,
            ["capabilities"] = new JsonObject { ["canAddChildren"] = true },
            ["appProperties"] = new JsonObject { ["healthsyncSource"] = "latest_file", ["healthsyncKind"] = "archive-folder" }
        };

        private static HttpResponseMessage Ok(JsonObject value) => new(HttpStatusCode.OK)
            { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Error(HttpStatusCode code) => new(code)
            { Content = new StringContent("{\"error\":{\"message\":\"PRIVATE\"}}", Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
        private sealed class Token : ITokenProvider
        {
            public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("dummy");
        }
    }
}