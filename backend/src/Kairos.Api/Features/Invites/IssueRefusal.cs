namespace Kairos.Api.Features.Invites;

/// <summary>Why an invite was not issued. Mapped to a status code by the endpoint and nowhere else.</summary>
/// <remarks>Numbered from ten like every other enum here, and with no zero, so a value nobody set names no refusal.</remarks>
public enum IssueRefusal
{
    PrefillInvalid = 10,

    DeadlinePast = 20,
}
