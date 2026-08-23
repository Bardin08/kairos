namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Where a proposed move is in its life. Numbered with gaps for the reason
/// <see cref="BookingStatus"/> gives: a state can be inserted between two existing ones without
/// renumbering rows already in the database.
/// </summary>
public enum ProposalState
{
    /// <summary>Live, and holding its slot until <see cref="BookingProposal.ExpiresAt"/>.</summary>
    Open = 10,

    Accepted = 20,

    Declined = 30,

    /// <summary>The invitee never answered. Set by the sweeper, never by a request.</summary>
    Expired = 40,

    /// <summary>
    /// Closed without an answer: the operator took it back, the meeting was cancelled underneath
    /// it, or an accept failed its re-check and had to put everything back.
    /// </summary>
    Withdrawn = 50,
}
