namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Where an invite stands. Never persisted — it is folded from three timestamps and the clock, so
/// that a lapsed invite is lapsed at the instant it lapses rather than when something notices.
/// </summary>
/// <remarks>
/// Numbered from ten like the enums that are stored, and with no zero, so a value nobody set
/// cannot read as <see cref="Open"/>.
/// </remarks>
public enum InviteState
{
    Open = 10,

    Consumed = 20,

    Revoked = 30,

    Expired = 40,
}
