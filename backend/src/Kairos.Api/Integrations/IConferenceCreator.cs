using Kairos.Api.Persistence.Entities;

namespace Kairos.Api.Integrations;

/// <summary>
/// Separate from <see cref="ICalendarWriter"/> because the two are separate capabilities: one
/// provider may do both, and an organizer may equally keep their calendar in one place and their
/// conferencing in another.
/// </summary>
/// <remarks>
/// The booking id is the whole input, and that is deliberate. A conference is identified by the
/// meeting it belongs to, not by whatever the calendar happened to call it, so an implementation
/// that derives its provider-side identity from the booking id is idempotent under retry without
/// the caller having to hold anything between two calls.
/// </remarks>
public interface IConferenceCreator
{
    public IntegrationProvider Provider { get; }

    /// <summary>
    /// The conference for this booking, creating it if it does not exist yet. Throws when the
    /// provider has none to give; the caller treats that as a meeting without a link rather than
    /// as a meeting that failed.
    /// </summary>
    public Task<ConferenceDetails> CreateAsync(Guid bookingId, CancellationToken cancellationToken);
}
