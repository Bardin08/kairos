namespace Kairos.Api.Features.Bookings;

/// <summary>Who booked. One today; BookingParticipant is what lets there be more later.</summary>
public readonly record struct BookerDetails(string Name, string Email, string TimeZoneId);
