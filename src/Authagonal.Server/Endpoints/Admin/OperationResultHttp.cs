using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Admin;

/// <summary>
/// Renders an <see cref="OperationResult{T}"/> failure as the admin API's error response.
/// </summary>
/// <remarks>
/// The operations decide the outcome kind and the code; this is the one place the admin surface turns
/// that decision into a status, so every endpoint built on an operation answers a given code the same way.
/// </remarks>
internal static class OperationResultHttp
{
    /// <summary>The status the admin API answers for <paramref name="outcome"/>.</summary>
    public static int StatusFor(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Invalid => StatusCodes.Status400BadRequest,
        OperationOutcome.NotFound => StatusCodes.Status404NotFound,
        OperationOutcome.Conflict => StatusCodes.Status409Conflict,
        OperationOutcome.Failed => StatusCodes.Status502BadGateway,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A success has no error response."),
    };

    /// <summary>
    /// <c>{ error, error_description }</c> at <see cref="StatusFor"/>'s status, with <c>fields</c> when the
    /// failure names any. The description is the operation's message, or the code itself when the operation
    /// gave none, so the body always carries both members.
    /// </summary>
    public static IResult Error<T>(OperationResult<T> result) =>
        TypedResults.Json(
            new OperationErrorResponse
            {
                Error = result.Error!,
                ErrorDescription = result.Message ?? result.Error!,
                Fields = result.Fields,
            },
            AuthagonalJsonContext.Default.OperationErrorResponse,
            statusCode: StatusFor(result.Outcome));
}
