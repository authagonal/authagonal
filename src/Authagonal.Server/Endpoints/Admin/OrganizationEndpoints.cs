using Authagonal.Server.Services;

namespace Authagonal.Server.Endpoints.Admin;

/// <summary>
/// The organisation and membership admin API: thin HTTP over <see cref="OrganizationOperations"/>, which
/// owns every rule, code and audit row, so each handler only binds the request, names the actor and renders
/// the outcome.
/// </summary>
public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/organizations")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Organizations");

        group.MapGet("/", ListOrganizations);
        group.MapPost("/", CreateOrganization);
        group.MapGet("/{idOrSlug}", GetOrganization);
        group.MapPut("/{id}", UpdateOrganization);
        group.MapDelete("/{id}", DeleteOrganization);

        group.MapGet("/{id}/members", ListMembers);
        group.MapPost("/{id}/members", AddMember);
        group.MapPut("/{id}/members/{userId}", UpdateMember);
        group.MapDelete("/{id}/members/{userId}", RemoveMember);

        group.MapPost("/{id}/domains", AddDomain);
        group.MapPost("/{id}/domains/{domain}/verify", VerifyDomain);
        group.MapDelete("/{id}/domains/{domain}", RemoveDomain);

        group.MapPost("/backfill", Backfill);

        // What is keyed on a user sits on the admin user group, beside the user's other admin routes.
        var users = app.MapGroup("/api/v1/profile")
            .RequireAuthorization("IdentityAdmin")
            .WithTags("Admin - Organizations");

        users.MapGet("/{userId}/organizations", ListUserOrganizations);

        return app;
    }

    private static async Task<IResult> ListOrganizations(
        string? cursor, int? limit, OrganizationOperations operations, CancellationToken ct)
    {
        var result = await operations.ListPageAsync(cursor, limit, ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.ResultPageOrganizationDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> CreateOrganization(
        CreateOrganizationRequest request,
        OrganizationOperations operations,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await operations.CreateAsync(
            request.Slug, request.Name, request.BrandingJson, request.Enabled, request.RequireMembershipForTokens,
            request.AllowAutoMembership, request.Metadata, AdminActor.Of(http), ct);
        if (!result.Succeeded)
            return OperationResultHttp.Error(result);

        return Created(http, $"/api/v1/organizations/{result.Value!.Id}",
            result.Value, AuthagonalJsonContext.Default.OrganizationDto);
    }

    private static async Task<IResult> GetOrganization(
        string idOrSlug, OrganizationOperations operations, CancellationToken ct)
    {
        var result = await operations.GetAsync(idOrSlug, ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.OrganizationDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> UpdateOrganization(
        string id,
        UpdateOrganizationRequest request,
        OrganizationOperations operations,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await operations.UpdateAsync(
            id, request.Name, request.BrandingJson, request.Slug, request.Enabled, request.RequireMembershipForTokens,
            request.AllowAutoMembership, request.Metadata, AdminActor.Of(http), ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.OrganizationDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> DeleteOrganization(
        string id, OrganizationOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.DeleteAsync(id, AdminActor.Of(http), ct);
        return result.Succeeded ? Results.NoContent() : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> ListMembers(
        string id, string? cursor, int? limit, OrganizationOperations operations, CancellationToken ct)
    {
        var result = await operations.ListMembersPageAsync(id, cursor, limit, ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.ResultPageMembershipDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> AddMember(
        string id,
        AddMemberRequest request,
        OrganizationOperations operations,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await operations.AddMemberAsync(
            id, request.UserId, request.Email, request.Status, request.Roles, AdminActor.Of(http), ct);
        if (!result.Succeeded)
            return OperationResultHttp.Error(result);

        return Created(http, $"/api/v1/organizations/{id}/members/{result.Value!.UserId}",
            result.Value, AuthagonalJsonContext.Default.MembershipDto);
    }

    private static async Task<IResult> UpdateMember(
        string id,
        string userId,
        UpdateMemberRequest request,
        OrganizationOperations operations,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await operations.UpdateMemberAsync(
            id, userId, request.Status, request.Roles, AdminActor.Of(http), ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.MembershipDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> RemoveMember(
        string id, string userId, OrganizationOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.RemoveMemberAsync(id, userId, AdminActor.Of(http), ct);
        return result.Succeeded ? Results.NoContent() : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> AddDomain(
        string id,
        AddOrganizationDomainRequest request,
        OrganizationOperations operations,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await operations.AddDomainAsync(id, request.Domain, AdminActor.Of(http), ct);
        if (!result.Succeeded)
            return OperationResultHttp.Error(result);

        return Created(http, $"/api/v1/organizations/{id}/domains/{result.Value!.Domain}",
            result.Value, AuthagonalJsonContext.Default.OrganizationDomainDto);
    }

    /// <summary>
    /// A miss answers 409 <c>verification_failed</c>, never 200 with <c>verified: false</c>, so a caller
    /// polling for success cannot mistake a miss for one.
    /// </summary>
    private static async Task<IResult> VerifyDomain(
        string id, string domain, OrganizationOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.VerifyDomainAsync(id, domain, AdminActor.Of(http), ct);
        return result.Succeeded
            ? TypedResults.Json(result.Value!, AuthagonalJsonContext.Default.OrganizationDomainDto)
            : OperationResultHttp.Error(result);
    }

    private static async Task<IResult> RemoveDomain(
        string id, string domain, OrganizationOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.RemoveDomainAsync(id, domain, AdminActor.Of(http), ct);
        return result.Succeeded ? Results.NoContent() : OperationResultHttp.Error(result);
    }

    /// <summary>
    /// <c>dryRun</c> defaults true even with no body at all: a nullable complex-type parameter binds to
    /// null on an empty request body instead of failing, so <c>request?.DryRun ?? true</c> covers both "no
    /// body" and "body present but dryRun omitted" (the request's own property default is also true).
    /// </summary>
    private static async Task<IResult> Backfill(
        BackfillRequest? request, OrganizationOperations operations, HttpContext http, CancellationToken ct)
    {
        var result = await operations.BackfillAsync(request?.DryRun ?? true, AdminActor.Of(http), ct);
        return TypedResults.Json(result, AuthagonalJsonContext.Default.BackfillResultDto);
    }

    private static async Task<IResult> ListUserOrganizations(
        string userId, OrganizationOperations operations, CancellationToken ct)
    {
        var items = await operations.ListUserOrganizationsAsync(userId, ct);
        return TypedResults.Json(
            new UserOrganizationListResponse { Items = [.. items] },
            AuthagonalJsonContext.Default.UserOrganizationListResponse);
    }

    /// <summary>
    /// 201 with a Location header and the body serialised through the source-generated context (where
    /// <c>Results.Created</c> would serialise by reflection).
    /// </summary>
    private static IResult Created<T>(
        HttpContext http, string location, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        http.Response.Headers.Location = location;
        return TypedResults.Json(value, typeInfo, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Body of <c>POST /api/v1/organizations</c>; see <see cref="OrganizationOperations.CreateAsync"/>.</summary>
    public sealed class CreateOrganizationRequest
    {
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? BrandingJson { get; set; }
        public bool? Enabled { get; set; }
        public bool? RequireMembershipForTokens { get; set; }
        public bool? AllowAutoMembership { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>Body of <c>PUT /api/v1/organizations/{id}</c>; see <see cref="OrganizationOperations.UpdateAsync"/>.</summary>
    public sealed class UpdateOrganizationRequest
    {
        public string? Name { get; set; }
        public string? BrandingJson { get; set; }
        public string? Slug { get; set; }
        public bool? Enabled { get; set; }
        public bool? RequireMembershipForTokens { get; set; }
        public bool? AllowAutoMembership { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>Body of <c>POST /api/v1/organizations/{id}/members</c>; see <see cref="OrganizationOperations.AddMemberAsync"/>.</summary>
    public sealed class AddMemberRequest
    {
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public string? Status { get; set; }
        public List<string>? Roles { get; set; }
    }

    /// <summary>Body of <c>PUT /api/v1/organizations/{id}/members/{userId}</c>; see <see cref="OrganizationOperations.UpdateMemberAsync"/>.</summary>
    public sealed class UpdateMemberRequest
    {
        public string? Status { get; set; }
        public List<string>? Roles { get; set; }
    }

    /// <summary>Body of <c>POST /api/v1/organizations/{id}/domains</c>; see <see cref="OrganizationOperations.AddDomainAsync"/>.</summary>
    public sealed class AddOrganizationDomainRequest
    {
        /// <summary>The email domain to claim, e.g. <c>acme.com</c>. Normalised (trimmed, lowercased,
        /// trailing dot dropped) before validation.</summary>
        public string? Domain { get; set; }
    }

    /// <summary>Body of <c>POST /api/v1/organizations/backfill</c>; see <see cref="OrganizationOperations.BackfillAsync"/>.</summary>
    public sealed class BackfillRequest
    {
        /// <summary>When true (the default) nothing is written and the counts describe what would happen.</summary>
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Answer of <c>GET /api/v1/profile/{userId}/organizations</c>.</summary>
    public sealed class UserOrganizationListResponse
    {
        /// <summary>Every organisation the user belongs to.</summary>
        public List<UserOrganizationDto> Items { get; set; } = [];
    }
}
