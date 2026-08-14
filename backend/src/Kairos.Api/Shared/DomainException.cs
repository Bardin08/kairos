namespace Kairos.Api.Shared;

/// <summary>
/// Thrown by queries and writers; never caught by them. Carries a kind and a machine-readable
/// code and knows nothing about HTTP — no status code, no ASP.NET types, nothing that would
/// stop this exception being thrown from a background job or a CLI.
/// </summary>
public sealed class DomainException(DomainErrorKind kind, string code, string message)
    : Exception(message)
{
    public DomainErrorKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static DomainException NotFound(string what) =>
        new(DomainErrorKind.NotFound, "not_found", $"{what} does not exist.");

    public static DomainException Invalid(string code, string message) =>
        new(DomainErrorKind.Invalid, code, message);

    public static DomainException Conflict(string code, string message) =>
        new(DomainErrorKind.Conflict, code, message);
}
