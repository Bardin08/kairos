namespace Kairos.Api.Features.Invites;

/// <summary>Whether a bag may be stored, what was wrong with it, and which key was wrong.</summary>
/// <param name="Problem">Null on a bag that may be stored — which is what <see cref="IsValid"/> reads.</param>
/// <param name="OffendingKey">
/// The key that sank it. Named for the key rather than for the fault, because on two of the three
/// problems the key itself is perfectly acceptable.
/// </param>
public readonly record struct PrefillValidation(PrefillProblem? Problem, string? OffendingKey)
{
    public static readonly PrefillValidation Valid = new(null, null);

    public bool IsValid => Problem is null;
}
