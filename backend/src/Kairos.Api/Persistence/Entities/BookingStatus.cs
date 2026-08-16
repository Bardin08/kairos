namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// Numbered with gaps so a status can be inserted between two existing ones without renumbering
/// rows already in the database. Persisted as the underlying int.
/// </summary>
public enum BookingStatus
{
    Confirmed = 10,
    Cancelled = 20,
}
