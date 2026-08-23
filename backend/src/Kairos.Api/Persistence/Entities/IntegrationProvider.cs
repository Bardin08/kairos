namespace Kairos.Api.Persistence.Entities;

/// <summary>
/// One enum for every table that names a provider — calendar, conference and busy alike.
/// Google is one integration that happens to do all three jobs, so splitting it into a
/// CalendarProvider and a ConferenceProvider would be two types describing one fact.
/// </summary>
/// <remarks>
/// One value, and no speculative ones: the column stores an int, so adding a provider later needs
/// no migration, and declaring providers that may never be integrated would be a roadmap written
/// into a type definition. The gaps are what keep that addition safe. What a provider <em>can
/// do</em> is answered by which interfaces its adapter implements.
/// </remarks>
public enum IntegrationProvider
{
    Google = 10,
}
