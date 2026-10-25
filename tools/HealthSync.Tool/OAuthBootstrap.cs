using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using HealthSync.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace HealthSync.Tool;

internal static class OAuthBootstrap
{
    private const string RedirectUri = "http://localhost:8765/";

    public static async Task RunAsync(string root, string clientSecretsPath, bool noBrowser, string? driveFolderId = null,
        bool resume = false)
    {
        DriveFileSetup.ValidateFolderId(driveFolderId);
        var web = JsonNode.Parse(await File.ReadAllTextAsync(clientSecretsPath))?["web"];
        if (web is null || web["redirect_uris"] is not JsonArray redirects || !redirects.Any(v => v?.ToString() == RedirectUri))
            throw new SyncException($"Use a Web application OAuth client with this exact redirect URI: {RedirectUri}");
        var secrets = new ClientSecrets { ClientId = web["client_id"]!.ToString(), ClientSecret = web["client_secret"]!.ToString() };
        using var flow = new PkceGoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets, Scopes = SyncOptions.Scopes
        });
        var privateDirectory = Path.Combine(root, "private");
        var refreshTokenPath = Path.Combine(privateDirectory, "google-refresh-token.txt");
        if (resume && !File.Exists(refreshTokenPath)) throw new SyncException("No stored Google consent; run bootstrap without --resume first");
        var token = resume
            ? new TokenResponse { RefreshToken = (await File.ReadAllTextAsync(refreshTokenPath)).Trim() }
            : await ConsentAsync(flow, noBrowser);
        if (string.IsNullOrEmpty(token.RefreshToken)) throw new SyncException("Google did not issue a refresh token; repeat consent");
        if (!string.IsNullOrEmpty(token.Scope) && !SyncOptions.Scopes.All(token.Scope.Split(' ').Contains))
            throw new SyncException("Not all required scopes were granted; repeat consent and grant each one");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    using var tokens = new GoogleTokenProvider(new SyncOptions(secrets.ClientId, secrets.ClientSecret, token.RefreshToken, "pending"), http);
    var api = new GoogleApiClient(http, tokens);
        await PrivateFiles.WriteAsync(Path.Combine(privateDirectory, "google-client-secret.txt"), secrets.ClientSecret);
    await PrivateFiles.WriteAsync(refreshTokenPath, token.RefreshToken);
    Console.WriteLine("Google consent is available; credentials saved privately. Checking Google Health linkage.");
        await api.SendAsync(HttpMethod.Get, $"{GoogleApiClient.HealthBase}/identity", "Google Health profile");
        var metadataPath = Path.Combine(privateDirectory, "bootstrap.json");
        var storedFileId = File.Exists(metadataPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))?["drive_file_id"]?.ToString() : null;
        var metadata = await new DriveFileSetup(api).ResolveAsync(storedFileId, driveFolderId);
        var fileId = metadata["id"]!.ToString();
        var settingsPath = Path.Combine(root, "src/HealthSync.Functions/local.settings.json");
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "src/HealthSync.Functions/local.settings.example.json")))!;
        var values = settings["Values"]!.AsObject();
        values["GOOGLE_CLIENT_ID"] = secrets.ClientId;
        values["GOOGLE_CLIENT_SECRET"] = secrets.ClientSecret;
        values["GOOGLE_REFRESH_TOKEN"] = token.RefreshToken;
        values["DRIVE_FILE_ID"] = fileId;
        await PrivateFiles.WriteAsync(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));
        await PrivateFiles.WriteAsync(metadataPath, new JsonObject
        {
            ["google_client_id"] = secrets.ClientId, ["drive_file_id"] = fileId,
            ["drive_folder_id"] = metadata["parents"]?.AsArray().FirstOrDefault()?.ToString(),
            ["drive_url"] = metadata["webViewLink"]?.ToString() ?? $"https://drive.google.com/file/d/{fileId}/view"
        }.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine("Authorization completed. Drive link and non-secret IDs: private/bootstrap.json");
        Console.WriteLine("Next: dotnet run --project tools/HealthSync.Tool -- run");
    }

    private static async Task<TokenResponse> ConsentAsync(PkceGoogleAuthorizationCodeFlow flow, bool noBrowser)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var request = flow.CreateAuthorizationCodeRequest(RedirectUri, out var verifier);
        request.State = state;
        var built = request.Build();
        var query = QueryHelpers.ParseQuery(built.Query).ToDictionary(p => p.Key, p => (string?)p.Value.ToString());
        query["access_type"] = "offline";
        query["prompt"] = "consent";
        query["include_granted_scopes"] = "true";
        var authorizationUrl = QueryHelpers.AddQueryString(built.GetLeftPart(UriPartial.Path), query);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // Do not log the OAuth callback query/code.
        builder.WebHost.UseUrls(RedirectUri.TrimEnd('/'));
        await using var app = builder.Build();
        app.MapGet("/", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var supplied = context.Request.Query["state"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(state)))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Invalid state. Return to the authorization page.");
                return;
            }
            var code = context.Request.Query["code"].ToString();
            if (string.IsNullOrEmpty(code) || context.Request.Query.ContainsKey("error"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Authorization was not completed.");
                completion.TrySetException(new SyncException("Google authorization was not completed"));
                return;
            }
            await context.Response.WriteAsync("Authorization response received. You may close this tab.");
            await context.Response.CompleteAsync();
            completion.TrySetResult(code);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await app.StartAsync(timeout.Token);
        try
        {
            Console.WriteLine("Open this Google authorization URL on this PC:");
            Console.WriteLine(authorizationUrl);
            if (!noBrowser)
                try { Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true }); }
                catch { Console.WriteLine("Open the URL above manually."); }
            var code = await completion.Task.WaitAsync(timeout.Token);
            return await flow.ExchangeCodeForTokenAsync("self", code, verifier, RedirectUri, timeout.Token);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

}
