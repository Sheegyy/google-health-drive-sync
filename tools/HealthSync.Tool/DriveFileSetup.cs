using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HealthSync.Core;

namespace HealthSync.Tool;

public sealed class DriveFileSetup(GoogleApiClient api)
{
    public static void ValidateFolderId(string? folderId)
    {
        if (folderId is not null) ValidateId(folderId, "Drive folder ID");
    }

    public async Task<JsonObject> ResolveAsync(string? fileId = null, string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFolderId(folderId);
        if (fileId is not null)
        {
            ValidateId(fileId, "Stored Drive file ID");
            var existing = await api.SendAsync(HttpMethod.Get, MetadataUrl(fileId,
                "id,name,mimeType,webViewLink,parents,trashed,capabilities(canEdit)"),
                "Existing Drive file", cancellationToken: cancellationToken);
            ValidateJsonFile(existing);
            if (existing["id"]!.ToString() != fileId)
                throw new SyncException("Existing Drive file returned an unexpected ID; no content was changed");
            if (existing["capabilities"]?["canEdit"]?.GetValue<bool>() == false)
                throw new SyncException("Existing Drive JSON is not writable; no content was changed");
            if (folderId is not null && (existing["parents"] is not JsonArray parents ||
                !parents.Any(parent => parent?.ToString() == folderId)))
                throw new SyncException("Existing Drive JSON is outside the requested folder; move it in Drive first. No file was moved or changed");
            return existing;
        }

        if (folderId is not null)
        {
            var folder = await api.SendAsync(HttpMethod.Get, MetadataUrl(folderId,
                "id,mimeType,trashed,capabilities(canAddChildren)"),
                "Target Drive folder (drive.file may require selecting it with Google Picker)",
                cancellationToken: cancellationToken);
            if (folder["mimeType"]?.ToString() != "application/vnd.google-apps.folder" ||
                folder["trashed"]?.GetValue<bool>() == true)
                throw new SyncException("Requested Drive target is not an active folder; no file was created");
            if (folder["capabilities"]?["canAddChildren"]?.GetValue<bool>() != true)
                throw new SyncException("Requested Drive folder does not allow creating files; no file was created");
        }
        else
        {
            var folder = await api.SendAsync(HttpMethod.Post,
                "https://www.googleapis.com/drive/v3/files?fields=id&supportsAllDrives=true", "Create private folder",
                new JsonObject { ["name"] = "Health Sync", ["mimeType"] = "application/vnd.google-apps.folder" }, cancellationToken);
            folderId = folder["id"]?.ToString() ?? throw new SyncException("Created Drive folder has no ID");
            ValidateId(folderId, "Created Drive folder ID");
        }

        var metadata = await api.SendAsync(HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/files?fields=id,name,mimeType,webViewLink,parents&supportsAllDrives=true", "Create JSON file",
            new JsonObject { ["name"] = "health_sync_latest.json", ["mimeType"] = "application/json",
                ["parents"] = new JsonArray(folderId) }, cancellationToken);
        ValidateJsonFile(metadata);
        if (metadata["id"]!.ToString() == folderId)
            throw new SyncException("Drive returned the folder ID instead of a JSON file ID; no content was written");
        if (metadata["parents"] is not JsonArray createdParents ||
            !createdParents.Any(parent => parent?.ToString() == folderId))
            throw new SyncException("Created Drive JSON is not in the requested folder; no content was written");
        await api.SendAsync(HttpMethod.Patch,
            $"https://www.googleapis.com/upload/drive/v3/files/{metadata["id"]}?uploadType=media&fields=id&supportsAllDrives=true", "Initialize JSON",
            new JsonObject { ["status"] = "not_synced_yet" }, cancellationToken);
        return metadata;
    }

    private static string MetadataUrl(string id, string fields) => GoogleApiClient.WithQuery(
        $"https://www.googleapis.com/drive/v3/files/{id}",
        new Dictionary<string, string> { ["fields"] = fields, ["supportsAllDrives"] = "true" });

    private static void ValidateId(string id, string label)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$"))
            throw new SyncException($"{label} must be an ID, not a URL");
    }

    private static void ValidateJsonFile(JsonObject metadata)
    {
        if (metadata["mimeType"]?.ToString() != "application/json" || metadata["trashed"]?.GetValue<bool>() == true)
            throw new SyncException("Drive target is not an active JSON file; no content was changed");
        ValidateId(metadata["id"]?.ToString() ?? "", "Drive JSON file ID");
    }
}