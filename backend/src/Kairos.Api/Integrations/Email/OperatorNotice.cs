namespace Kairos.Api.Integrations.Email;

/// <summary>
/// What the operator is being told about a suggestion nobody acted on. Two, because an accepted
/// one is not on this list: the meeting moved, the calendar says so, and a mail repeating an entry
/// the organizer already holds is the kind people filter — taking these two with it.
/// </summary>
public enum OperatorNotice
{
    Declined = 10,
    Expired = 20,
}
