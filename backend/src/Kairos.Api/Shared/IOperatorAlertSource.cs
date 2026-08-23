namespace Kairos.Api.Shared;

/// <summary>
/// One publisher on the operator's alert channel. Registered into the container; the route
/// collects every one of them and knows what none of them are.
/// </summary>
/// <remarks>
/// Collect, never throw. A source that cannot answer returns nothing, because one broken publisher
/// must not blank a panel that another has something true to say on — the same lesson the
/// Integrations listing learned when a single unreadable credential took the whole screen with it.
/// </remarks>
public interface IOperatorAlertSource
{
    public Task<IReadOnlyList<OperatorAlert>> CollectAsync(CancellationToken cancellationToken);
}
