namespace Authagonal.Server.Services;

/// <summary>
/// The kind of outcome an operation reached. A caller maps it onto its own response: the library's admin
/// endpoints answer 400, 404, 409 and 502 for the four failures, and another host maps them however its
/// own surface needs.
/// </summary>
public enum OperationOutcome
{
    /// <summary>The operation succeeded and carries its value.</summary>
    Ok,

    /// <summary>The request was refused as malformed or not allowed as asked.</summary>
    Invalid,

    /// <summary>Something the request names does not exist.</summary>
    NotFound,

    /// <summary>The request collides with the state it found.</summary>
    Conflict,

    /// <summary>The operation could not finish because something it depends on failed.</summary>
    Failed,
}

/// <summary>
/// The outcome of a library operation, as a decision rather than as one caller's rendering of it: the
/// admin endpoint turns it into an HTTP response, and a host calling the same operation from its own
/// surface (a portal route, a tool) renders the same decision its own way.
/// </summary>
/// <remarks>
/// Deliberately no HTTP type. A caller that received an <c>IResult</c> could only inspect it by executing
/// it, so the operation returns what it decided and every caller maps <see cref="Outcome"/> itself.
/// </remarks>
public sealed record OperationResult<T>
{
    private OperationResult() { }

    /// <summary>What kind of outcome this is; <see cref="OperationOutcome.Ok"/> on success.</summary>
    public OperationOutcome Outcome { get; private init; }

    /// <summary>The value, when the operation succeeded.</summary>
    public T? Value { get; private init; }

    /// <summary>Machine-readable failure code (for example <c>slug_taken</c>), null on success.</summary>
    public string? Error { get; private init; }

    /// <summary>Human-readable failure detail, suitable for relaying to a person; null when the code says it all.</summary>
    public string? Message { get; private init; }

    /// <summary>The request fields a failure refers to, when it refers to particular ones.</summary>
    public IReadOnlyList<string>? Fields { get; private init; }

    /// <summary>True when <see cref="Outcome"/> is <see cref="OperationOutcome.Ok"/>.</summary>
    public bool Succeeded => Outcome == OperationOutcome.Ok;

    /// <summary>A success carrying <paramref name="value"/>.</summary>
    public static OperationResult<T> Ok(T value) => new() { Outcome = OperationOutcome.Ok, Value = value };

    /// <summary>A refusal of the request as asked.</summary>
    public static OperationResult<T> Invalid(string error, string? message = null, IReadOnlyList<string>? fields = null) =>
        Fail(OperationOutcome.Invalid, error, message, fields);

    /// <summary>Something the request names does not exist.</summary>
    public static OperationResult<T> NotFound(string error, string? message = null) =>
        Fail(OperationOutcome.NotFound, error, message);

    /// <summary>The request collides with existing state.</summary>
    public static OperationResult<T> Conflict(string error, string? message = null) =>
        Fail(OperationOutcome.Conflict, error, message);

    /// <summary>A dependency of the operation failed.</summary>
    public static OperationResult<T> Failed(string error, string? message = null) =>
        Fail(OperationOutcome.Failed, error, message);

    private static OperationResult<T> Fail(OperationOutcome outcome, string error, string? message,
        IReadOnlyList<string>? fields = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new() { Outcome = outcome, Error = error, Message = message, Fields = fields };
    }
}

/// <summary>One page of a cursor-paged listing.</summary>
public sealed class ResultPage<T>
{
    /// <summary>This page's rows.</summary>
    public List<T> Items { get; set; } = [];

    /// <summary>Pass back as <c>cursor</c> for the next page; null on the last page.</summary>
    public string? NextCursor { get; set; }
}
