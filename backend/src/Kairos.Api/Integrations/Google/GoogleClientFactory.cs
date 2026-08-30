using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

// Aliased because Google's factory interface collides by name with the one in System.Net.Http.
using GoogleHttpFactory = Google.Apis.Http.IHttpClientFactory;

namespace Kairos.Api.Integrations.Google;

/// <summary>
/// Nothing outside this folder names a Google type.
/// </summary>
/// <remarks>
/// A singleton, because the cache is the point: <see cref="UserCredential"/> holds the access
/// token in memory and refreshes it when stale, so one instance means one refresh an hour rather
/// than one per request. Only the refresh token is durable, encrypted by
/// <see cref="IntegrationCredentialStore"/>. Being a singleton is also why the store is reached
/// through a scope rather than injected: it rides on the request's DbContext.
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
    /// and omits it on re-authorisation unless consent is forced, so a reconnect after a revoke
    /// would store a credential that cannot be refreshed — surfacing weeks later at expiry.
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
    private readonly GoogleHttpFactory _httpClients;
    private readonly GoogleAuthorizationCodeFlow _flow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UserCredential? _credential;

    public GoogleClientFactory(
        IOptions<GoogleOptions> options,
        IServiceScopeFactory scopes,
        GoogleHttpFactory httpClients)
    {
        _options = options.Value;
        _scopes = scopes;
        _httpClients = httpClients;
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

            // The token refresh goes through this too. Given only to the adapter, a test would
            // fake every Calendar call and still reach Google's real token endpoint to obtain the
            // credential that makes them — a network call in a unit test, and a flaky one.
            HttpClientFactory = httpClients,
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

        // Doubles as proof that the grant works and the Calendar API is enabled on the project;
        // the alternative is a row reading "connected" and a first booking that discovers
        // otherwise. The primary calendar's id is the account's address, and is not a secret.
        var label = await ReadPrimaryCalendarIdAsync(token, cancellationToken);

        await using var scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .WriteAsync(
                Provider,
                new GoogleCredentialPayload(token.RefreshToken),
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

    /// <summary>
    /// An authorised client, or null when there is nothing to authorise with — unconfigured, never
    /// connected, or connected and paused. Null rather than an exception because all three are
    /// ordinary states of a self-hosted instance. The token is refreshed here rather than lazily
    /// inside somebody else's HTTP call, so a dead grant is caught at a point that can record it.
    /// </summary>
    public async Task<UserCredential?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // Ahead of the cache, and that placement is the whole of it. The credential store's own
        // gate is only consulted when the cache is empty, so on a process that has already served
        // one booking page a pause changed nothing until a restart — freebusy queries, calendar
        // writes and health stamps all carried on against a provider the page called Paused.
        //
        // A check rather than clearing the cache from the toggle: invalidation would put a Google
        // detail in the endpoint and break again for the next provider that caches anything. What
        // it costs is one indexed read immediately before an HTTP round trip, and the cache was
        // there to save token refreshes rather than selects.
        if (await IsPausedAsync(cancellationToken))
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

                // The success is recorded as well as the failure, and it has to be: a grant that
                // was refused and then reconnected has to stop being reported, and nothing else
                // in this process ever learns that the provider answered.
                await MarkAsync(store => store.MarkHealthyAsync(Provider, CancellationToken.None));
            }
            catch (TokenResponseException exception) when (exception.Error?.Error == InvalidGrant)
            {
                // Access was revoked or the account password changed. Recorded rather than only
                // thrown: nothing retries its way out of this, and the operator's fix is a button
                // on a screen that has to know to offer it.
                _credential = null;
                await MarkAsync(store => store.MarkUnhealthyAsync(
                    Provider,
                    CredentialHealth.ReconnectRequired,
                    InvalidGrant,
                    CancellationToken.None));

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
            HttpClientFactory = _httpClients,
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

    /// <summary>A scope of its own, for the reason <see cref="ReadPayloadAsync"/> opens one.</summary>
    private async Task<bool> IsPausedAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>()
            .IsPausedAsync(Provider, cancellationToken);
    }

    /// <summary>
    /// A scope for one health write. The store rides on the request's DbContext and this class is
    /// a singleton, so every write from here needs one of its own — the same reason
    /// <see cref="ReadPayloadAsync"/> opens one.
    /// </summary>
    /// <remarks>
    /// What it replaced read the payload, rewrote it with a flag flipped, and re-encrypted the
    /// refresh token to record something that was never Google's business. The state is a column
    /// now, so this touches no ciphertext at all.
    /// </remarks>
    private async Task MarkAsync(Func<IntegrationCredentialStore, Task> write)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await write(scope.ServiceProvider.GetRequiredService<IntegrationCredentialStore>());
    }

    /// <summary>
    /// Private and nested because it never leaves this class: the store is generic over it, so it
    /// crosses no signature that anything else can see.
    /// </summary>
    /// <remarks>
    /// It used to carry a reconnect flag. That is a property of the credential now, so a second
    /// provider inherits it — and a credential written before the move deserialises untouched,
    /// since an extra JSON property is ignored. No re-encryption and no data migration; the flag
    /// those rows carry is lost, and the next refresh against a dead grant re-derives it.
    /// </remarks>
    private sealed record GoogleCredentialPayload(string RefreshToken);
}
