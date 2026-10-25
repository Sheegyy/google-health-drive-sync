using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HealthSync.Core;

public interface ITokenProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
    Task<string> GetAsync(string requestUrl, CancellationToken cancellationToken) => GetAsync(cancellationToken);
}

public sealed class GoogleApiClient(HttpClient http, ITokenProvider tokens,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string HealthBase = "https://health.googleapis.com/v4/users/me";
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    public const int MaximumPagedRecords = 250_000;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static string WithQuery(string url, IReadOnlyDictionary<string, string> query) => url + "?" +
        string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public async Task<JsonObject> SendAsync(HttpMethod method, string url, string label,
        JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        var payload = await SendTextAsync(method, url, label, body, cancellationToken);
        try
        {
            return JsonNode.Parse(payload) as JsonObject ?? throw new SyncException($"{label}: expected a JSON object");
        }
        catch (JsonException) { throw new SyncException($"{label}: invalid JSON response"); }
    }

    public async Task<string> SendTextAsync(HttpMethod method, string url, string label,
        JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        // Creating a Drive file is never automatically retried. rollUp is read-only.
        var safeToRetry = method == HttpMethod.Get || method == HttpMethod.Patch ||
            url.EndsWith(":rollUp", StringComparison.Ordinal) || url.EndsWith(":dailyRollUp", StringComparison.Ordinal);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(url, cancellationToken));
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new SyncException($"{label}: transport failure ({ex.GetType().Name})"); }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (safeToRetry && attempt < 3 && (status == 429 || status is 500 or 502 or 503 or 504))
                {
                    var pause = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
                    await _delay(TimeSpan.FromSeconds(Math.Clamp(pause.TotalSeconds, 0, 60)), cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var reason = KnownErrorReason(await ReadBoundedAsync(response.Content, label, cancellationToken));
                    var diagnostic = reason is null ? "" : $" ({reason})";
                    throw new SyncException($"{label}: HTTP {status}{diagnostic}; check API enablement, scopes and account linkage", status, reason);
                }
                return await ReadBoundedAsync(response.Content, label, cancellationToken);
            }
        }
        throw new SyncException($"{label}: retries exhausted");
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, string label, CancellationToken cancellationToken)
    {
        try
        {
            await content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken);
            return await content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new SyncException($"{label}: response could not be read within the safety limit ({ex.GetType().Name}); Drive was not updated"); }
    }

    private static string? KnownErrorReason(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root || root["error"] is not JsonObject error) return null;
            if (error["details"] is JsonArray details)
            {
                var reasons = details.OfType<JsonObject>().SelectMany(detail => new[] {
                    detail["reason"]?.ToString() ?? "",
                    detail["metadata"] is JsonObject metadata ? metadata["detailedReasons"]?.ToString() ?? "" : ""
                }).ToArray();
                foreach (var reason in new[] { "MISSING_OAUTH_SCOPE", "ACCESS_TOKEN_SCOPE_INSUFFICIENT", "DISALLOWED_OAUTH_SCOPES",
                    "SERVICE_DISABLED", "ACCOUNT_NOT_LINKED", "BILLING_DISABLED", "API_PRIVATE_PREVIEW_ACCESS_DENIED",
                    "DATA_ACCESS_DENIED", "RESOURCE_PERMISSION_DENIED", "RESOURCE_NOT_FOUND", "UNSUPPORTED_DATA_TYPE_ACTION",
                    "INVALID_DATA_TYPE", "INVALID_PARENT_DATA_TYPE_COLLECTION", "INVALID_DATA_POINT_FILTER_DATA_TYPE_MEMBER",
                    "INVALID_DATA_POINT_FILTER", "INVALID_FILTER_FORMAT" })
                {
                    if (reasons.Any(value => Regex.IsMatch(value, $"(?<![A-Z0-9_]){reason}(?![A-Z0-9_])")))
                        return reason;
                }
            }
            var message = error["message"]?.ToString() ?? "";
            if (message.Contains("GaiaMint", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("UberMint", StringComparison.OrdinalIgnoreCase))
                return "GOOGLE_HEALTH_LINK_REQUIRED";
        }
        catch (JsonException) { }
        return null;
    }

    public async Task<JsonArray> PagedAsync(HttpMethod method, string url, JsonObject fields,
        string responseKey, string label, CancellationToken cancellationToken = default, long maximumBytes = 64 * 1024 * 1024)
    {
        var result = new JsonArray();
        long collectedBytes = 2;
        string? token = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < 100; page++)
        {
            var current = (JsonObject)fields.DeepClone();
            if (token is not null) current["pageToken"] = token;
            var requestUrl = method == HttpMethod.Get
                ? WithQuery(url, current.ToDictionary(p => p.Key, p => p.Value!.ToString())) : url;
            var response = await SendAsync(method, requestUrl, label,
                method == HttpMethod.Get ? null : current, cancellationToken);
            if (response[responseKey] is { } raw)
            {
                if (raw is not JsonArray batch || batch.Any(row => row is not JsonObject))
                    throw new SyncException($"{label}: malformed {responseKey}");
                foreach (var row in batch)
                {
                    collectedBytes += Encoding.UTF8.GetByteCount(row!.ToJsonString()) + 1;
                    if (collectedBytes > maximumBytes)
                        throw new SyncException($"{label}: response size limit exceeded; Drive was not updated");
                    if (result.Count >= MaximumPagedRecords)
                        throw new SyncException($"{label}: record safety limit exceeded; Drive was not updated");
                    result.Add(row!.DeepClone());
                }
            }
            token = response["nextPageToken"]?.ToString();
            if (string.IsNullOrEmpty(token)) return result;
            if (!seen.Add(token)) throw new SyncException($"{label}: repeated page token");
        }
        throw new SyncException($"{label}: pagination limit exceeded; Drive was not updated");
    }
}
