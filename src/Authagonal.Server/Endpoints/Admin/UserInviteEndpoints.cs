using Authagonal.Server.Services;
using Microsoft.Net.Http.Headers;

namespace Authagonal.Server.Endpoints.Admin;

/// <summary>
/// Inviting a person by email and sending the invite again: thin HTTP over <see cref="UserInviteOperations"/>,
/// which owns every rule, code and audit row, so each handler only binds the request, names the actor and
/// renders the outcome.
/// </summary>
public static class UserInviteEndpoints
{
    public static IEndpointRouteBuilder MapUserInviteAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/profile")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Users");

        group.MapPost("/invite", InviteUser);
        group.MapPost("/{userId}/resend-invite", ResendInvite);

        return app;
    }

    private static async Task<IResult> InviteUser(
        InviteUserRequest? request, UserInviteOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.InviteAsync(
            request?.Email, request?.FirstName, request?.LastName, request?.Locale,
            InviterLocale(http, request?.Locale), AdminActor.Of(http), ct,
            request?.OrganizationId, request?.OrganizationRoles);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.InvitedUser)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> ResendInvite(
        string userId, UserInviteOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.ResendInviteAsync(userId, InviterLocale(http, null), AdminActor.Of(http), ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.SentResult)
            : OperationResultHttp.Error(result);
    }

    /// <summary>
    /// The language to write the invite in: the calling administrator's, which is the first of their
    /// <c>Accept-Language</c> preferences the library ships a translation for, else the locale the request
    /// names for the invitee, else none (the email then falls back to the invitee's locale, then English).
    /// </summary>
    private static string? InviterLocale(HttpContext http, string? bodyLocale)
    {
        var preferred = http.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(v => v.Quality ?? 1.0)
            .Select(v => SupportedLocales.Resolve(v.Value.Value))
            .FirstOrDefault(c => c is not null);
        return preferred?.Name ?? Locales.Normalize(bodyLocale);
    }

    /// <summary>Body of <c>POST /api/v1/profile/invite</c>; see <see cref="UserInviteOperations.InviteAsync"/>.</summary>
    public sealed class InviteUserRequest
    {
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Locale { get; set; }
        public string? OrganizationId { get; set; }
        public List<string>? OrganizationRoles { get; set; }
    }
}
