using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Features.Legal;

/// <summary>
/// The operator's half of the legal pages, as saved on the settings screen. The text itself ships
/// in the frontend, because it describes the software and changes only with a release.
/// </summary>
public sealed record LegalResponse(string? OperatorName, string? ContactEmail, string? Jurisdiction);

public static class LegalEndpoints
{
    public static RouteGroupBuilder MapLegal(this RouteGroupBuilder group)
    {
        group.MapGet("/legal", GetAsync).WithName("GetLegal")
            .RateLimited(RateLimitClass.AnonymousRead);
        return group;
    }

    private static Task<LegalResponse> GetAsync(KairosDbContext context, CancellationToken cancellationToken) =>
        context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => new LegalResponse(row.OperatorName, row.ContactEmail, row.Jurisdiction))
            .SingleAsync(cancellationToken);
}
