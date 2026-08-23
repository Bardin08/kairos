namespace Kairos.Api.Shared;

/// <summary>
/// One publisher on the operator's alert channel. The route collects every one registered in the
/// container and knows none of them by name.
/// </summary>
/// <remarks>
/// Collect, never throw: a source that cannot answer returns nothing, so one broken publisher does
/// not blank a panel another has something true to say on — the lesson of a single unreadable
/// credential taking the whole Integrations listing with it.
/// </remarks>
public interface IOperatorAlertSource
{
    public Task<IReadOnlyList<OperatorAlert>> CollectAsync(CancellationToken cancellationToken);
}
