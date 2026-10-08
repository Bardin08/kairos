namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// The single configuration row. One organizer, one set of globals, so a singleton rather than
/// a table of rows nobody can tell apart. A link may override any of the four limits.
/// </summary>
public sealed class Settings
{
    /// <summary>The only key this table ever holds. Fixed rather than generated.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; }

    /// <summary>IANA zone the organizer works in. Every slot the API offers is derived from it.</summary>
    public required string TimeZoneId { get; set; }

    public int MarginMinutes { get; set; }

    public int MinNoticeMinutes { get; set; }

    public int DailyCap { get; set; }

    public int StartIncrementMinutes { get; set; }

    /// <summary>How long a proposed move holds its slot before it lapses. Hours, not minutes.</summary>
    public int ProposalHoldHours { get; set; }

    /// <summary>
    /// Who runs this instance, named on the landing page, the privacy policy and the terms. Null
    /// until the operator says, and the pages fall back to neutral wording.
    /// </summary>
    public string? OperatorName { get; set; }

    /// <summary>Where an invitee sends a privacy request. Google's consent-screen review needs one.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>The country whose law governs the terms, as a name: <c>Poland</c>.</summary>
    public string? Jurisdiction { get; set; }
}
