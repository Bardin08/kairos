namespace Kairos.Api.Shared;

/// <summary>
/// One publisher on the operator's alert channel. The route collects every one registered in the
/// container and knows none of them by name.
/// </summary>
/// <remarks>
/// Collect, never throw. A source that cannot answer returns nothing, because one broken publisher
/// must not blank a panel another has something true to say on — the lesson the Integrations
/// listing learned when a single unreadable credential took the whole screen with it.
/// </remarks>
public interface IOperatorAlertSource
{
    public Task<IReadOnlyList<OperatorAlert>> CollectAsync(CancellationToken cancellationToken);
}
