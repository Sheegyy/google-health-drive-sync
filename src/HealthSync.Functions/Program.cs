using HealthSync.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton(_ => SyncOptions.FromEnvironment());
        services.AddSingleton<ITokenProvider, GoogleTokenProvider>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton(sp => new GoogleApiClient(sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ITokenProvider>()));
        services.AddSingleton<HealthSyncService>();
    }).Build();
await host.RunAsync();
