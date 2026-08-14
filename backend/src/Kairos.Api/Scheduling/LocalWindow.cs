using NodaTime;

namespace Kairos.Api.Scheduling;

/// <summary>An availability window in the organizer's wall-clock time. Never UTC.</summary>
public readonly record struct LocalWindow(LocalDate Date, LocalTime Start, LocalTime End);
