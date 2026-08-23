using Kairos.Api.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Shared;

/// <summary>
/// A write that another writer may have moved under, reported as the conflict it is rather than
/// as a 500.
/// </summary>
/// <remarks>
/// Two shapes, one meaning. A tracked delete whose row is already gone raises
/// <see cref="DbUpdateConcurrencyException"/>; an insert that lands after the other writer's
/// raises a SQLite unique violation, which arrives wrapped and is recognised by extended error
/// code rather than by type — see <see cref="SqliteErrors.IsUniqueViolation"/>. The operator's
/// situation is identical in both, so the code and the sentence are too. Everything else travels
/// untouched: a wrapper that swallowed the rest would answer 409 to a full disk.
/// </remarks>
public static class ConcurrentWrite
{
    public static async Task<T> NamedAsync<T>(Func<Task<T>> write, string code, string message)
    {
        try
        {
            return await write();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw DomainException.Conflict(code, message);
        }
        catch (Exception failure) when (SqliteErrors.IsUniqueViolation(failure))
        {
            throw DomainException.Conflict(code, message);
        }
    }
}
