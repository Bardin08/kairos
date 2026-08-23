namespace Kairos.Api.Shared;

/// <summary>
/// Thrown by queries and writers; never caught by them. Carries a kind and a machine-readable
/// code and knows nothing about HTTP — no status code, no ASP.NET types, nothing that would
/// stop this exception being thrown from a background job or a CLI.
/// </summary>
public sealed class DomainException(
    DomainErrorKind kind,
    string code,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public DomainErrorKind Kind { get; } = kind;

    public string Code { get; } = code;

    /// <summary>
    /// The one code with no constant of its own until now, because it is minted here rather than
    /// by a caller. Named so nothing has to retype it — the clients key on these strings.
    /// </summary>
    public const string NotFoundCode = "not_found";

    public static DomainException NotFound(string what) =>
        new(DomainErrorKind.NotFound, NotFoundCode, $"{what} does not exist.");

    public static DomainException Invalid(string code, string message) =>
        new(DomainErrorKind.Invalid, code, message);

    /// <summary>
    /// A conflict, optionally carrying what made it one. The inner exception never reaches the
    /// caller — <see cref="DomainExceptionHandler"/> answers with the code and the message alone —
    /// but it is what the log line has to say when the conflict was recognised from a database
    /// error rather than decided in the open.
    /// </summary>
    public static DomainException Conflict(string code, string message, Exception? cause = null) =>
        new(DomainErrorKind.Conflict, code, message, cause);
}
