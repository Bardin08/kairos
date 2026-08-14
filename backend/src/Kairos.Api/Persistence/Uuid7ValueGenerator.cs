using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Kairos.Api.Persistence;

/// <summary>
/// Mints UUIDv7 keys for entities added without one. Wired onto every Guid key, because the
/// alternative is that EF falls back to its own <c>GuidValueGenerator</c> — which is v4, and a v4
/// key scatters B-tree inserts instead of appending, with no error at any point to say so.
/// </summary>
/// <remarks>
/// EF Core ships <c>SequentialGuidValueGenerator</c>, which does not help: it is still v4, and its
/// sequential bits sit in the trailing bytes because SQL Server's uniqueidentifier sorts from
/// there. This provider stores Guid as canonical hyphenated TEXT, so ordering is lexicographic
/// and only a leading timestamp — v7's — makes stored order chronological.
/// </remarks>
public sealed class Uuid7ValueGenerator : ValueGenerator<Guid>
{
    /// <summary>Real values, not placeholders: the caller needs the id for the manage URL.</summary>
    public override bool GeneratesTemporaryValues => false;

    public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
}
