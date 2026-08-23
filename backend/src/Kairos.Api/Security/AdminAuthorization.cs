using System.Security.Claims;

namespace Kairos.Api.Security;

/// <summary>
/// Who counts as the operator. The policy this feeds was <c>RequireAuthenticatedUser()</c> alone,
/// the absence of an authorization decision: every account the provider would authenticate was a
/// full operator.
/// </summary>
/// <remarks>
/// A group claim as the mandatory half, because adding a second operator is then a membership
/// change at the provider rather than a redeployment here. The subject allowlist is an optional
/// second gate, for a provider that hands the group out more widely than Kairos should trust.
/// </remarks>
public static class AdminAuthorization
{
    /// <summary>
    /// What the refusal carries, so a client branches on one thing rather than a status code that
    /// also means half a dozen other refusals. Signed in and not an operator is a different screen
    /// from signed out, and this is what tells them apart.
    /// </summary>
    public const string NotAnOperatorCode = "not_an_operator";

    /// <summary>The human half of the same refusal, for a log or a client with no screen.</summary>
    public const string NotAnOperatorTitle =
        "This account is signed in and is not an operator of this Kairos.";

    /// <summary>The configuration path, named so the startup failure can quote it back.</summary>
    public const string GroupPath = "Authorization:AdminGroup";

    public const string SubjectsPath = "Authorization:AdminSubjects";

    /// <summary>
    /// What an OIDC provider calls it when asked for the <c>groups</c> scope. Not mapped by
    /// MapInboundClaims — that table covers the registered JWT claims, and this one arrives with
    /// its own name and one claim per array element.
    /// </summary>
    public const string GroupClaimType = "groups";

    /// <summary>The scope that asks the provider to put the claim above in the token.</summary>
    public const string GroupScope = "groups";

    /// <summary>
    /// Configured out of the box so an operator meets a working boundary rather than a locked
    /// one, and so the failure below is reserved for a value somebody actively blanked.
    /// </summary>
    public const string DefaultGroup = "kairos-users";

    /// <summary>
    /// The subject as the token spells it. Rarely what reaches the principal: MapInboundClaims
    /// defaults to true and the inbound map rewrites <c>sub</c> to the ClaimTypes URI, so
    /// <see cref="IsOperator"/> reads this one first and falls back to that. Both arms are live —
    /// this is the arm a provider configured not to map its claims lands on.
    /// </summary>
    public const string SubjectClaimType = "sub";

    /// <summary>
    /// Read once at startup, and thrown for rather than defaulted when present and empty. An absent
    /// setting is a deployment that has not thought about groups yet and gets the default; a blank
    /// one is a deployment that tried and produced nothing, which silently means "no principal can
    /// ever satisfy this".
    /// </summary>
    public static string RequiredGroup(IConfiguration configuration)
    {
        var group = configuration[GroupPath];
        if (group is null)
        {
            return DefaultGroup;
        }

        if (string.IsNullOrWhiteSpace(group))
        {
            throw new InvalidOperationException(
                $"{GroupPath} is set to an empty value. It names the group an account must "
                + "carry to reach the admin surface; leave it unset for the default "
                + $"'{DefaultGroup}' or name a real group.");
        }

        return group;
    }

    /// <summary>
    /// Empty means the group alone decides, which is the common deployment. A blank entry is the
    /// same mistake <see cref="RequiredGroup"/> throws for, one setting over: no principal has an
    /// empty subject, so an allowlist containing one admits nobody and every admin route answers
    /// 403 with nothing anywhere saying why.
    /// </summary>
    public static IReadOnlyList<string> AllowedSubjects(IConfiguration configuration)
    {
        var subjects = configuration.GetSection(SubjectsPath).Get<string[]>() ?? [];
        if (Array.Exists(subjects, string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{SubjectsPath} has an empty entry. Each entry names one account allowed to "
                + "reach the admin surface; remove the entry to let the group alone decide, or "
                + "give it a real subject identifier.");
        }

        return subjects;
    }

    public static bool IsOperator(
        ClaimsPrincipal principal,
        string group,
        IReadOnlyList<string> subjects)
    {
        if (!principal.HasClaim(GroupClaimType, group))
        {
            return false;
        }

        if (subjects.Count == 0)
        {
            return true;
        }

        var subject = principal.FindFirstValue(SubjectClaimType)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return subject is not null && subjects.Contains(subject, StringComparer.Ordinal);
    }
}
