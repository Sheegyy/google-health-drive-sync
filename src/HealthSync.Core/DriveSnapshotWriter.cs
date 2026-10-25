using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HealthSync.Core;

public sealed class DriveSnapshotWriter(GoogleApiClient api, SyncOptions options)
{
    private const string FilesUrl = "https://www.googleapis.com/drive/v3/files";
    private const string MetadataFields = "id,mimeType,parents,trashed,shared,driveId,capabilities(canEdit,canAddChildren),appProperties";

    public async Task WriteAsync(JsonObject snapshot, DateTimeOffset queryTime, CancellationToken cancellationToken = default)
    {
        ValidateId(options.DriveFileId);
        if (options.ArchiveSnapshots)
        {
            var latest = await api.SendAsync(HttpMethod.Get, GoogleApiClient.WithQuery($"{FilesUrl}/{options.DriveFileId}",
                new Dictionary<string, string> { ["fields"] = MetadataFields, ["supportsAllDrives"] = "true" }),
                "Latest file metadata", cancellationToken: cancellationToken);
            ValidateFile(latest);
            if (latest["id"]!.ToString() != options.DriveFileId || latest["parents"] is not JsonArray { Count: 1 } parents)
                throw new SyncException("Latest JSON must have one valid parent folder; no snapshot was written");
            ValidateId(parents[0]?.ToString() ?? "");
            var folderId = await ResolveArchiveFolderAsync(cancellationToken);
            var date = TimeZoneInfo.ConvertTime(queryTime, SyncOptions.FindZone(options.TimeZone))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var properties = new JsonObject { ["healthsyncSource"] = options.DriveFileId, ["healthsyncDate"] = date };
            var existing = await FindArchiveAsync(folderId, date, cancellationToken);
            var archive = existing ?? await api.SendAsync(HttpMethod.Post, GoogleApiClient.WithQuery(FilesUrl,
                new Dictionary<string, string> { ["fields"] = MetadataFields, ["supportsAllDrives"] = "true", ["ignoreDefaultVisibility"] = "true" }),
                "Create daily archive", new JsonObject { ["name"] = $"health_sync_{date}.json", ["mimeType"] = "application/json",
                    ["parents"] = new JsonArray(folderId), ["appProperties"] = properties }, cancellationToken);
            ValidateFile(archive);
            if (archive["id"]!.ToString() == options.DriveFileId ||
                archive["parents"] is not JsonArray { Count: 1 } archiveParents || archiveParents[0]?.ToString() != folderId ||
                archive["appProperties"] is not JsonObject markers || markers["healthsyncSource"]?.ToString() != options.DriveFileId ||
                markers["healthsyncDate"]?.ToString() != date)
                throw new SyncException("Daily archive metadata does not match the destination; no snapshot was written");
            await UploadAsync(archive["id"]!.ToString(), snapshot, "Daily archive update", cancellationToken);
        }
        await UploadAsync(options.DriveFileId, snapshot, "Drive update", cancellationToken);
    }

    private async Task<string> ResolveArchiveFolderAsync(CancellationToken cancellationToken)
    {
        JsonObject folder;
        if (options.ArchiveFolderId is { } configuredId)
        {
            ValidateId(configuredId);
            folder = await api.SendAsync(HttpMethod.Get, GoogleApiClient.WithQuery($"{FilesUrl}/{configuredId}",
                new Dictionary<string, string> { ["fields"] = MetadataFields, ["supportsAllDrives"] = "true" }),
                "Archive folder metadata", cancellationToken: cancellationToken);
            if (folder["id"]?.ToString() != configuredId)
                throw new SyncException("Archive folder returned an unexpected ID; no snapshot was written");
        }
        else
        {
            var query = $"trashed = false and appProperties has {{ key='healthsyncSource' and value='{options.DriveFileId}' }} and " +
                "appProperties has { key='healthsyncKind' and value='archive-folder' }";
            folder = await FindUniqueAsync(query, cancellationToken) ?? await api.SendAsync(HttpMethod.Post,
                GoogleApiClient.WithQuery(FilesUrl, new Dictionary<string, string> { ["fields"] = MetadataFields,
                    ["supportsAllDrives"] = "true", ["ignoreDefaultVisibility"] = "true" }),
                "Create archive folder", new JsonObject { ["name"] = "Health Sync History", ["mimeType"] = "application/vnd.google-apps.folder",
                    ["appProperties"] = new JsonObject { ["healthsyncSource"] = options.DriveFileId, ["healthsyncKind"] = "archive-folder" } },
                cancellationToken);
            if (folder["appProperties"] is not JsonObject markers || markers["healthsyncSource"]?.ToString() != options.DriveFileId ||
                markers["healthsyncKind"]?.ToString() != "archive-folder")
                throw new SyncException("Archive folder markers do not match this sync; no snapshot was written");
        }
        ValidateId(folder["id"]?.ToString() ?? "");
        ValidatePrivate(folder);
        if (folder["mimeType"]?.ToString() != "application/vnd.google-apps.folder" ||
            folder["capabilities"] is not JsonObject capabilities || capabilities["canAddChildren"]?.GetValue<bool>() != true)
            throw new SyncException("Archive folder must allow creating files; no snapshot was written");
        return folder["id"]!.ToString();
    }

    private Task<JsonObject?> FindArchiveAsync(string folderId, string date, CancellationToken cancellationToken) =>
        FindUniqueAsync($"trashed = false and '{folderId}' in parents and " +
            $"appProperties has {{ key='healthsyncSource' and value='{options.DriveFileId}' }} and " +
            $"appProperties has {{ key='healthsyncDate' and value='{date}' }}", cancellationToken);

    private async Task<JsonObject?> FindUniqueAsync(string query, CancellationToken cancellationToken)
    {
        JsonObject? found = null;
        string? token = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 100; page++)
        {
            var fields = new Dictionary<string, string> { ["q"] = query, ["spaces"] = "drive", ["pageSize"] = "100",
                ["fields"] = $"files({MetadataFields}),nextPageToken,incompleteSearch", ["supportsAllDrives"] = "true",
                ["includeItemsFromAllDrives"] = "true" };
            if (token is not null) fields["pageToken"] = token;
            var response = await api.SendAsync(HttpMethod.Get, GoogleApiClient.WithQuery(FilesUrl, fields),
                "Find daily archive", cancellationToken: cancellationToken);
            if (response["incompleteSearch"]?.GetValue<bool>() == true)
                throw new SyncException("Daily archive search was incomplete; no snapshot was written");
            if (response["files"] is { } raw)
            {
                if (raw is not JsonArray files || files.Any(file => file is not JsonObject))
                    throw new SyncException("Daily archive search returned malformed files; no snapshot was written");
                foreach (var file in files.OfType<JsonObject>())
                {
                    if (found is not null) throw new SyncException("Multiple daily archives match; resolve duplicates before syncing");
                    found = file;
                }
            }
            token = response["nextPageToken"]?.ToString();
            if (string.IsNullOrEmpty(token)) return found;
            if (!seenTokens.Add(token)) throw new SyncException("Daily archive search repeated a page token; no snapshot was written");
        }
        throw new SyncException("Daily archive search exceeded the page limit; no snapshot was written");
    }

    private Task<JsonObject> UploadAsync(string fileId, JsonObject snapshot, string label, CancellationToken cancellationToken) =>
        api.SendAsync(HttpMethod.Patch, GoogleApiClient.WithQuery($"https://www.googleapis.com/upload/drive/v3/files/{fileId}",
            new Dictionary<string, string> { ["uploadType"] = "media", ["fields"] = "id", ["supportsAllDrives"] = "true" }),
            label, snapshot, cancellationToken);

    private static void ValidateFile(JsonObject file)
    {
        ValidateId(file["id"]?.ToString() ?? "");
        ValidatePrivate(file);
        if (file["mimeType"]?.ToString() != "application/json" || file["capabilities"] is not JsonObject capabilities ||
            capabilities["canEdit"]?.GetValue<bool>() != true)
            throw new SyncException("Snapshot destination must be an active, unshared, writable JSON file; no snapshot was written");
    }

    private static void ValidatePrivate(JsonObject value)
    {
        if (value["trashed"]?.GetValue<bool>() == true || value["driveId"] is not null ||
            value["shared"] is not JsonValue shared || !shared.TryGetValue<bool>(out var isShared) || isShared)
            throw new SyncException("History requires an unshared My Drive destination; no snapshot was written");
    }

    private static void ValidateId(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]+$"))
            throw new SyncException("Snapshot destination must use a valid Drive ID; no snapshot was written");
    }
}