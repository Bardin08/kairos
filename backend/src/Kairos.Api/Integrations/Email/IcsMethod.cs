namespace Kairos.Api.Integrations.Email;

/// <summary>
/// The iCalendar METHOD, as an enum rather than a string, because a typo in a method name
/// is not an error a calendar client reports — it is an update the client silently ignores.
/// </summary>
public enum IcsMethod
{
    Request = 10,
    Cancel = 20,
}
