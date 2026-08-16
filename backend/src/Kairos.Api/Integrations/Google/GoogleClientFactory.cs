using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

namespace Kairos.Api.Integrations.Google;

/// <summary>
/// Everything Google-shaped about connecting and staying connected: the consent URL and its two
/// non-negotiable parameters, the code exchange, and an authorised client whose access token is
/// refreshed on demand. Nothing outside this folder names a Google type.
/// </summary>
/// <remarks>
/// A singleton, because the cache is the point: <see cref="UserCredential"/> holds the current
/// access token in memory and refreshes it when it goes stale, so one instance means one refresh
/// an hour instead of one per request. The token is never written anywhere — only the refresh
/// token is durable, and it is encrypted by <see cref="IntegrationCredentialStore"/>. Being a
/// singleton is also why the store is reached through a scope rather than injected: the store
/// rides on the request's DbContext.
/// </remarks>
public sealed class GoogleClientFactory : IIntegrationConnector, IDisposable
{
    /// <summary>
    /// Without it the exchange returns an access token and no refresh token, and the connection
    /// dies silently an hour later.
    /// </summary>
    private const string OfflineAccess = "offline";

    /// <summary>
    /// Not decoration. Google issues a refresh token on the <em>first</em> consent for a client
    /// and omits it on every re-authorisation unless consent is forced — so a reconnect after a
    /// revoke would store a credential that cannot be refreshed, and the failure would surface
    /// weeks later when the access token expires.
    /// </summary>
    private const string ForceConsent = "consent";

    /// <summary>The signed-in account's own calendar; its id is that account's address.</summary>
    private const string PrimaryCalendarId = "primary";

    /// <summary>
    /// The one refresh failure that is not worth retrying: the grant is gone, and only a new
    /// consent brings it back.
    /// </summary>
    private const string InvalidGrant = "invalid_grant";

    private const string ApplicationName = "Kairos";

    /// <summary>
    /// The flow's key for a stored token. Inert here — there is no data store attached, because
    /// the database is the store — but the API requires one, and one operator means one value.
    /// </summary>
    private const string UserId = "kairos";

    private readonly GoogleOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly GoogleAuthorizationCodeFlow _flow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UserCredential? _credential;

    public GoogleClientFactory(IOptions<GoogleOptions> options, IServiceScopeFactory scopes)
    {
        _options = options.Value;
        _scopes = scopes;
        _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _options.ClientId,
                ClientSecret = _options.ClientSecret,
            },

            // One scope, and the writable one: Part B creates the meeting as well as reading
            // busy time, and asking for read-only now would mean a second consent screen later.
            Scopes = [CalendarService.Scope.Calendar],
        });
    }

    public IntegrationProvider Provider => IntegrationProvider.Google;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId)
        && !string.IsNullOrWhiteSpace(_options.ClientSecret)
        && !string.IsNullOrWhiteSpace(_options.RedirectUri);

    public Uri BuildConsentUrl(string state)
    {
        var request = (GoogleAuthorizationCodeRequestUrl)
            _flow.CreateAuthorizationCodeRequest(_options.RedirectUri);

        request.AccessType = OfflineAccess;
        request.Prompt = ForceConsent;
        request.State = state;

        return request.Build();
    }

    public async Task CompleteAsync(string code, CancellationToken cancellationToken)
    {
        var token = await _flow.ExchangeCodeForTokenAsync(
            UserId, code, _options.RedirectUri, cancellationToken);

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new InvalidOperationException(
                "Google returned no refresh token. Revoke Kairos at "
                + "https://myaccount.google.com/permissions and connect again.");
        }

        // Doubles as the proof that the grant works and that the Calendar API is enabled on the
        // project: the alternative is a row that reads "connected" and a first booking that
        // discovers otherwise. The primary calendar's id is the account's address, which is
        // exactly the label the page wants and is not a secret.
        var label = await ReadPrimaryCalendarIdAsync(token, cancellationToken);

        await using var scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .WriteAsync(
                Provider,
                new GoogleCredentialPayload(token.RefreshToken, false),
                label,
                cancellationToken);

        // The cached token belongs to the grant that was just replaced.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _credential = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsReconnectRequiredAsync(CancellationToken cancellationToken) =>
        (await ReadPayloadAsync(cancellationToken))?.ReconnectRequired ?? false;

    /// <summary>
    /// An authorised client for the connected account, or null when there is nothing to
    /// authorise with — unconfigured, or never connected. Null rather than an exception because
    /// both are ordinary states of a self-hosted instance, and a booking must not fail over
    /// either. The token is refreshed here rather than lazily inside somebody else's HTTP call,
    /// so a dead grant is caught at a point that can record it.
    /// </summary>
    public async Task<UserCredential?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_credential is null)
            {
                var payload = await ReadPayloadAsync(cancellationToken);
                if (payload is null)
                {
                    return null;
                }

                _credential = new UserCredential(
                    _flow, UserId, new TokenResponse { RefreshToken = payload.RefreshToken });
            }

            try
            {
                await _credential.GetAccessTokenForRequestAsync(null, cancellationToken);
            }
            catch (TokenResponseException exception) when (exception.Error?.Error == InvalidGrant)
            {
                // Access was revoked or the account password changed. Recorded rather than only
                // thrown: nothing retries its way out of this, and the operator's fix is a button
                // on a page that has to know to offer it.
                _credential = null;
                await MarkReconnectRequiredAsync();
                throw;
            }

            return _credential;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _flow.Dispose();
        _gate.Dispose();
    }

    private async Task<string> ReadPrimaryCalendarIdAsync(
        TokenResponse token,
        CancellationToken cancellationToken)
    {
        using var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new UserCredential(_flow, UserId, token),
            ApplicationName = ApplicationName,
        });

        var calendar = await service.Calendars.Get(PrimaryCalendarId).ExecuteAsync(cancellationToken);
        return calendar.Id;
    }

    private async Task<GoogleCredentialPayload?> ReadPayloadAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .ReadAsync<GoogleCredentialPayload>(Provider, cancellationToken);
    }

    /// <summary>
    /// Deliberately uncancellable: it runs while a refresh is failing, and the request that
    /// triggered it may already be going away. Losing this write would leave the page saying
    /// "connected" about a credential that can never work again.
    /// </summary>
    private async Task MarkReconnectRequiredAsync()
    {
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>();

        var payload = await store.ReadAsync<GoogleCredentialPayload>(Provider, CancellationToken.None);
        if (payload is null)
        {
            return;
        }

        await store.WriteAsync(
            Provider, payload with { ReconnectRequired = true }, null, CancellationToken.None);
    }

    /// <summary>
    /// What Google needs to still be connected tomorrow, and the only shape that knows the words
    /// "refresh token". Private and nested because it never leaves this class: the store is
    /// generic over it, so it crosses no signature that anything else can see.
    /// </summary>
    private sealed record GoogleCredentialPayload(string RefreshToken, bool ReconnectRequired);
}
