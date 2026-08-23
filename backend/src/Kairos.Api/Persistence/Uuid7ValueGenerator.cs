using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Kairos.Api.Persistence;

/// <summary>
/// Mints UUIDv7 keys for entities added without one, wired onto every Guid key: otherwise EF falls
/// back to <c>GuidValueGenerator</c>, whose v4 keys scatter B-tree inserts instead of appending,
/// with no error to say so.
/// </summary>
/// <remarks>
/// <c>SequentialGuidValueGenerator</c> does not help: still v4, with its sequential bits in the
/// trailing bytes because SQL Server's uniqueidentifier sorts from there. This provider stores Guid
/// as hyphenated TEXT, so ordering is lexicographic and only v7's leading timestamp makes stored
/// order chronological.
/// </remarks>
public sealed class Uuid7ValueGenerator : ValueGenerator<Guid>
{
    /// <summary>Real values, not placeholders: the caller needs the id for the manage URL.</summary>
    public override bool GeneratesTemporaryValues => false;

    public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
}
