using System.Security.Claims;

namespace Kairos.Api.Security;

/// <summary>
/// Who counts as the operator. The policy this feeds was <c>RequireAuthenticatedUser()</c> and
/// nothing else, which is not an authorization decision — it is the absence of one, and it made
/// every account the identity provider would authenticate a full operator.
/// </summary>
/// <remarks>
/// A group claim rather than a subject list as the mandatory half, because a group is what an
/// identity provider is for: adding a second operator is a membership change there instead of a
/// redeployment here. The subject allowlist is the optional second gate, for the deployment whose
/// provider hands out the group more widely than Kairos should trust.
/// </remarks>
public static class AdminAuthorization
{
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
    /// The subject, whichever spelling reaches the principal: the OIDC handler maps <c>sub</c> to
    /// the ClaimTypes URI, and a handler that does not is still answered.
    /// </summary>
    private const string SubjectClaimType = "sub";

    /// <summary>
    /// Read once at startup, and thrown for rather than defaulted when it is present and empty.
    /// An absent setting is a deployment that has not thought about groups yet and gets the
    /// default; a blank one is a deployment that tried and produced nothing, and the difference
    /// matters because the second silently means "no principal can ever satisfy this".
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

    /// <summary>Empty means the group alone decides, which is the common deployment.</summary>
    public static IReadOnlyList<string> AllowedSubjects(IConfiguration configuration) =>
        configuration.GetSection(SubjectsPath).Get<string[]>() ?? [];

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
