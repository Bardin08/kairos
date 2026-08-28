namespace Kairos.Api.Integrations.Email;

/// <summary>The moment the message is about. Job one of the accent, and the reason it is a block.</summary>
/// <param name="Was">
/// The time this meeting used to be at, for a reschedule and a proposal. Null everywhere else —
/// a message that shows a struck-through time nobody moved is worse than one that shows none.
/// </param>
public sealed record MailFact(string Time, string Date, string Zone, string? Was);

/// <summary>The single thing to press. Job two of the accent, and there is never more than one.</summary>
public sealed record MailAction(string Label, string Url);

/// <summary>A retreat or an alternative. Rendered as a plain link, because the accent has two jobs.</summary>
public sealed record MailSecondary(string Label, string Url);

/// <summary>One labelled fact. <paramref name="Url"/> makes the value a link rather than prose.</summary>
public sealed record MailDetail(string Label, string Value, string? Url = null);

/// <summary>
/// One message, before either renderer has seen it.
/// </summary>
/// <remarks>
/// Every optional member is optional because some real message does not have it: a cancellation
/// the operator triggered carries no manage token and therefore no action, a booking with no
/// conference has no join detail, and most cancellations carry no reason. The renderers are what
/// guarantee an absent member leaves no mark.
/// </remarks>
public sealed record MailBody
{
    public required string Headline { get; init; }

    public required MailFact Fact { get; init; }

    public required string Sentence { get; init; }

    public MailAction? Action { get; init; }

    public MailSecondary? Secondary { get; init; }

    public IReadOnlyList<MailDetail> Details { get; init; } = [];

    /// <summary>The operator's own words, unedited. Its heading disappears with it.</summary>
    public string? Reason { get; init; }

    public string? Footer { get; init; }
}
