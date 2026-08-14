namespace Kairos.Api.Features.Bookings;

/// <summary>
/// The outcome of one booking attempt. On success <see cref="ManageToken"/> is the only time the
/// plaintext token exists — the database keeps its hash — and <see cref="Reason"/> is null. On
/// failure the reason is a stable machine code, not a sentence: the endpoint chooses the wording.
/// </summary>
public readonly record struct CreateBookingResult(bool Success, Guid Id, string? ManageToken, string? Reason);
