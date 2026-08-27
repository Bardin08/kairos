namespace Kairos.Api.Features.Invites;

/// <summary>Whether a bag may be stored, and which key sank it.</summary>
public readonly record struct PrefillValidation(bool IsValid, string? UnknownKey)
{
    public static readonly PrefillValidation Valid = new(true, null);
}
