namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One enum for every table that names a provider — calendar, conference and busy alike. Google
/// does all three jobs, so a CalendarProvider and a ConferenceProvider would be two types
/// describing one fact.
/// </summary>
/// <remarks>
/// One value, and no speculative ones: the column stores an int, so adding a provider needs no
/// migration, and the gaps keep that addition safe. What a provider <em>can do</em> is answered by
/// which interfaces its adapter implements.
/// </remarks>
public enum IntegrationProvider
{
    Google = 10,
}
