using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace HealthSync.Core;

public sealed class GoogleTokenProvider : ITokenProvider, IDisposable
{
    private readonly GoogleAuthorizationCodeFlow _healthFlow;
    private readonly GoogleAuthorizationCodeFlow _driveFlow;
    private readonly UserCredential _healthCredential;
    private readonly UserCredential _driveCredential;

    public GoogleTokenProvider(SyncOptions options, HttpClient? tokenHttpClient = null)
    {
        _healthFlow = new ScopedFlow(options, SyncOptions.HealthScopes, tokenHttpClient);
        _driveFlow = new ScopedFlow(options, SyncOptions.DriveScopes, tokenHttpClient);
        _healthCredential = new UserCredential(_healthFlow, "health", new TokenResponse { RefreshToken = options.RefreshToken });
        _driveCredential = new UserCredential(_driveFlow, "drive", new TokenResponse { RefreshToken = options.RefreshToken });
    }

    public Task<string> GetAsync(CancellationToken cancellationToken) => GetAccessAsync(_healthCredential, cancellationToken);

    public Task<string> GetAsync(string requestUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SyncException("Google credentials require an approved HTTPS API endpoint");
        var credential = uri.Host switch
        {
            "health.googleapis.com" => _healthCredential,
            "www.googleapis.com" when uri.AbsolutePath.StartsWith("/drive/", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith("/upload/drive/", StringComparison.Ordinal) => _driveCredential,
            _ => throw new SyncException("Google credentials cannot be sent to this endpoint")
        };
        return GetAccessAsync(credential, cancellationToken);
    }

    private static async Task<string> GetAccessAsync(UserCredential credential, CancellationToken cancellationToken)
    {
        try { return await credential.GetAccessTokenForRequestAsync(null, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new SyncException($"Google token refresh failed ({ex.GetType().Name}); check token, client and publishing status");
        }
    }

    public void Dispose()
    {
        _healthFlow.Dispose();
        _driveFlow.Dispose();
    }

    private sealed class ScopedFlow(SyncOptions options, string[] scopes, HttpClient? tokenHttpClient)
        : GoogleAuthorizationCodeFlow(new Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = options.ClientId, ClientSecret = options.ClientSecret },
            Scopes = scopes
        }), IAuthorizationCodeFlow
    {
        async Task<TokenResponse> IAuthorizationCodeFlow.RefreshTokenAsync(string userId, string refreshToken,
            CancellationToken taskCancellationToken)
        {
            var response = await new RefreshTokenRequest
            {
                ClientId = options.ClientId, ClientSecret = options.ClientSecret,
                RefreshToken = refreshToken, Scope = string.Join(" ", scopes)
            }.ExecuteAsync(tokenHttpClient ?? HttpClient, TokenServerUrl, taskCancellationToken, Clock);
            if (!string.IsNullOrEmpty(response.Scope) &&
                !new HashSet<string>(scopes, StringComparer.Ordinal).SetEquals(response.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                throw new SyncException("Google returned scopes that do not match the requested API");
            response.RefreshToken ??= refreshToken;
            return response;
        }
    }
}
