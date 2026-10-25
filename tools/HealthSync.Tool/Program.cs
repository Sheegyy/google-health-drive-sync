using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HealthSync.Core;
using HealthSync.Tool;

try
{
    var root = Directory.GetCurrentDirectory();
    if (!File.Exists(Path.Combine(root, "Directory.Build.props")))
        throw new SyncException("Run this command from the repository root");
    string Option(string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length
        ? args[index + 1] : throw new SyncException($"Missing {key}");
    switch (args.FirstOrDefault())
    {
        case "bootstrap":
            await OAuthBootstrap.RunAsync(root, Option("--client-secrets"), args.Contains("--no-browser"),
                args.Contains("--drive-folder") ? Option("--drive-folder") : null, args.Contains("--resume"));
            break;
        case "run":
        {
            var settingsPath = Path.Combine(root, "src/HealthSync.Functions/local.settings.json");
            var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!["Values"]!.AsObject();
            foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value!.ToString());
            var options = SyncOptions.FromEnvironment();
            using var tokens = new GoogleTokenProvider(options);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var service = new HealthSyncService(new GoogleApiClient(http, tokens), options);
            Console.WriteLine("Drive updated. Record counts: " + JsonSerializer.Serialize(await service.RunAsync()));
            break;
        }
        case "cloud-settings":
        {
            var vault = Option("--vault");
            if (!Regex.IsMatch(vault, "^[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]$"))
                throw new SyncException("Invalid Key Vault name");
            var vaultOnly = args.Contains("--vault-only");
            var metadata = vaultOnly ? null
                : JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "private/bootstrap.json")))!;
            string Reference(string name) => $"@Microsoft.KeyVault(SecretUri=https://{vault}.vault.azure.net/secrets/{name})";
            var settings = new JsonObject
            {
                ["GOOGLE_CLIENT_ID"] = vaultOnly ? Reference("google-client-id") : metadata!["google_client_id"]!.ToString(),
                ["GOOGLE_CLIENT_SECRET"] = Reference("google-client-secret"),
                ["GOOGLE_REFRESH_TOKEN"] = Reference("google-refresh-token"),
                ["DRIVE_FILE_ID"] = vaultOnly ? Reference("drive-file-id") : metadata!["drive_file_id"]!.ToString(),
                ["HEALTH_SYNC_SCHEDULE"] = SyncOptions.DailyScheduleUtc,
                ["HEALTH_TIMEZONE"] = "Asia/Tokyo", ["HEALTH_LOOKBACK_DAYS"] = "14",
                ["HEALTH_INCLUDE_ALL_DATA"] = "true", ["HEALTH_ARCHIVE_ENABLED"] = "true",
                ["INCLUDE_HOURLY_HEART_RATE"] = "true"
            };
            await PrivateFiles.WriteAsync(Path.Combine(root, "private/azure.appsettings.json"), settings.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine("Key Vault reference settings written to private/azure.appsettings.json");
            break;
        }
        default:
            Console.WriteLine("Commands: bootstrap --client-secrets PATH [--no-browser] [--drive-folder ID] [--resume], run, cloud-settings --vault NAME [--vault-only]");
            break;
    }
}
catch (Exception ex)
{
    // Third-party exceptions may contain credential material; do not print them.
    Console.Error.WriteLine(ex is SyncException ? ex.Message : $"Operation failed ({ex.GetType().Name}); check settings and consent");
    Environment.ExitCode = 1;
}
