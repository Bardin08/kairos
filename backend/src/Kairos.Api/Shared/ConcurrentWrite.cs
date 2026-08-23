using Kairos.Api.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Shared;

/// <summary>A write another writer may have moved under, reported as a conflict rather than a 500.</summary>
/// <remarks>
/// Two shapes, one meaning. A tracked delete whose row is gone raises
/// <see cref="DbUpdateConcurrencyException"/>; an insert landing second raises a SQLite unique
/// violation, wrapped, recognised by extended error code rather than type — see
/// <see cref="SqliteErrors.IsUniqueViolation"/>. Everything else travels untouched: swallowing the
/// rest would answer 409 to a full disk.
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
