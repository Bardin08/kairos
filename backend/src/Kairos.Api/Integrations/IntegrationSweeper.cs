namespace Kairos.Api.Integrations;

/// <summary>
/// Asks every integration to prove its grant, then places the meetings a dead grant
/// left behind. No vendor namespace in this file, and adding one would be the bug.
/// </summary>
/// <remarks>
/// The refresh keeps nothing alive: access tokens are refreshed on demand anyway. What the tick
/// buys is that a dead grant is recorded on a day nobody opens a booking page, and that a grant
/// which works again resyncs without the operator pressing anything. It cannot rescue a grant
/// the provider has withdrawn, which is what a Google app in Testing status does after seven days.
/// </remarks>
public sealed class IntegrationSweeper(
    IServiceScopeFactory scopes,
    IEnumerable<IIntegrationConnector> connectors,
    ILogger<IntegrationSweeper> logger) : BackgroundService
{
    /// <summary>
    /// How long, past one access-token lifetime, a dead grant can go unrecorded on a day with no
    /// traffic, and how long a reconnect waits for its meetings to be placed. A healthy grant costs
    /// at most one token refresh per tick. A dead one costs a refused token request per refused
    /// meeting as well, up to <see cref="CalendarSyncService.MaxResyncPerPass"/>, because a refusal
    /// leaves no cached credential for the next placement to reuse.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "An integration sweep failed. The next tick will try again.");
            }
        }
    }

    /// <summary>One pass. Public so a test can drive it without a timer.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var connector in connectors)
        {
            await connector.RefreshAsync(cancellationToken);
        }

        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>()
            .ResyncAsync(linkId: null, cancellationToken);
    }
}
