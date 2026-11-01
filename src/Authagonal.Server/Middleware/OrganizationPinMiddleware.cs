using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace Authagonal.Server.Middleware;

/// <summary>
/// Makes the organisation a host is pinned to authoritative for the authorization requests that arrive
/// on it: <see cref="ITenantContext.OrganizationIdIsAuthoritative"/> together with a non-empty
/// <see cref="ITenantContext.OrganizationId"/>.
/// </summary>
/// <remarks>
/// Two things, and the second is the one that matters. The authorize endpoints read the organisation
/// from the request and nothing else — there is no host-based selection in them — so a pinned host has
/// to supply it, which is the injection. But supplying it is only a convenience; refusing a request
/// that names a DIFFERENT organisation is the actual security property, because a host pinned to
/// organisation A must not be able to mint organisation-B tokens, and without the refusal any relying
/// party could simply send its own <c>organization</c> and have the pin ignored.
/// <para>
/// The pin is compared by resolved organisation, not by string: a request naming the pinned
/// organisation by slug while the host pins it by id is the same organisation and is not refused.
/// </para>
/// <para>
/// It acts on <c>/connect/authorize</c> whatever the method (the test is on the path alone, trailing
/// slash trimmed) and on <c>POST /connect/par</c>, and on nothing else. Path-gated on purpose: it costs
/// a store read, and it must not ride on <c>/assets/*</c>, the login document or any other traffic the
/// host serves. A host registers whatever sets <see cref="ITenantContext"/> before
/// <c>UseAuthagonal</c>, which places this after it.
/// </para>
/// </remarks>
public sealed class OrganizationPinMiddleware(RequestDelegate next, ILogger<OrganizationPinMiddleware> logger)
{
    /// <summary>
    /// The authorize-request parameter names that carry an organisation. All three are accepted by the
    /// authorize endpoints, so a pin that only inspected <c>organization</c> would be bypassable by
    /// sending <c>org_id</c> instead.
    /// </summary>
    private static readonly string[] OrganizationParameters = ["organization", "org_slug", "org_id"];

    /// <summary>The OIDC authorization endpoint — the only path that reads the organisation parameter off the query string.</summary>
    private const string AuthorizePath = "/connect/authorize";

    /// <summary>
    /// The pushed-authorization endpoint. A pushed request's parameters are stored here and read back from
    /// that payload when <c>/connect/authorize?request_uri=…</c> resumes — the query string of that
    /// resume call is never consulted for them — so this is where a pushed request has to be pinned.
    /// </summary>
    private const string PushedAuthorizationPath = "/connect/par";

    /// <summary>
    /// The PAR endpoint's own body bound, duplicated so this middleware never reads more of a body than
    /// that endpoint would. Reading first and bounding second would make this the unbounded read the
    /// endpoint's bound exists to prevent.
    /// </summary>
    private const long MaxPushedAuthorizationBodyBytes = 32 * 1024;

    /// <summary>The refusal body, written as a literal so the trimmer has no serialiser to preserve.</summary>
    private const string RefusalBody =
        "{\"error\":\"access_denied\",\"error_description\":\"The requested organization is not available on this domain.\"}";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Trailing slashes are stripped before comparing, because ASP.NET routing still matches
        // "/connect/authorize/" to MapGet("/connect/authorize"). Comparing the raw path meant one extra
        // character bypassed the pin entirely and reached the real authorize endpoint unpinned and
        // unrefused. The length guard keeps "/" itself from normalising to "".
        var routedPath = path.Length > 1 ? path.TrimEnd('/') : path;
        var isAuthorize = routedPath.Equals(AuthorizePath, StringComparison.OrdinalIgnoreCase);
        var isPushed = routedPath.Equals(PushedAuthorizationPath, StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method);
        if (!isAuthorize && !isPushed)
        {
            await next(context);
            return;
        }

        if (await ApplyPinnedOrganizationAsync(context, isPushed))
            return;

        await next(context);
    }

    /// <summary>
    /// Returns true when the request was REFUSED and a response has already been written.
    /// </summary>
    private async Task<bool> ApplyPinnedOrganizationAsync(HttpContext context, bool isPushed)
    {
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext is null
            || !tenantContext.OrganizationIdIsAuthoritative
            || string.IsNullOrEmpty(tenantContext.OrganizationId))
            return false;

        var pinnedId = tenantContext.OrganizationId;

        // Resolved from the request's services rather than up front: a multi-tenant host registers the
        // store per tenant, and this middleware also runs for requests that never resolve a tenant.
        var organizations = context.RequestServices.GetService<IOrganizationStore>();
        var pinned = organizations is null
            ? null
            : await organizations.GetAsync(pinnedId, context.RequestAborted);

        if (pinned is null)
        {
            // Dangling pin: the organisation was deleted and the host outlived it, which is deliberate —
            // deleting an organisation does not delete the hosts pinned to it. Treat it as no pin at
            // all, so the host keeps working as the tenant-level host it was before, rather than
            // injecting an id the endpoint would then refuse and failing every login on the host.
            logger.LogWarning(
                "Host {Host} is pinned to organization {OrganizationId}, which no longer exists in tenant {TenantId}; serving it as a tenant-level host",
                context.Request.Host.Host, pinnedId, tenantContext.TenantId);
            return false;
        }

        return isPushed
            ? await PinPushedAuthorizationAsync(context, organizations!, pinned)
            : await PinAuthorizeQueryAsync(context, organizations!, pinned);
    }

    /// <summary>
    /// The direct <c>/connect/authorize</c> leg, whose parameters the endpoint reads off the query
    /// string. Returns true when the request was refused.
    /// </summary>
    private async Task<bool> PinAuthorizeQueryAsync(
        HttpContext context, IOrganizationStore organizations, Organization pinned)
    {
        var query = context.Request.Query;
        string? requested = null;
        foreach (var name in OrganizationParameters)
        {
            if (query[name].FirstOrDefault() is { } value && !string.IsNullOrWhiteSpace(value))
            {
                requested = value.Trim();
                break;
            }
        }

        if (requested is not null)
            return await RefuseUnlessPinnedAsync(context, organizations, pinned, requested);

        // A pushed request resumes here with a request_uri, and the endpoint then reads its parameters
        // from the stored payload — this query string is never consulted for them. Appending would be a
        // silent no-op; that leg is pinned at /connect/par instead.
        if (!string.IsNullOrWhiteSpace(query["request_uri"].FirstOrDefault()))
            return false;

        // Any blank alias the request carries names no organisation, but the endpoint would still read
        // it beside the pin as a repeated or conflicting parameter, so they are dropped first.
        var kept = query
            .Where(p => !OrganizationParameters.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.Select(v => new KeyValuePair<string, string?>(p.Key, v)));
        context.Request.QueryString = QueryString.Create(kept).Add("organization", pinned.Id);
        logger.LogDebug(
            "Pinned authorization request on {Host} to organization {OrganizationId}",
            context.Request.Host.Host, pinned.Id);
        return false;
    }

    /// <summary>
    /// The <c>POST /connect/par</c> leg, whose form becomes the stored payload the authorize call later
    /// reads. Returns true when the request was refused.
    /// </summary>
    /// <remarks>
    /// The form is not mutated through <c>HttpRequest.Form</c>: the PAR endpoint installs its own
    /// bounded <c>IFormFeature</c> before reading, which discards any form cached here and re-reads the
    /// body. So the BODY is rewritten instead, which is what that endpoint actually parses.
    /// </remarks>
    private async Task<bool> PinPushedAuthorizationAsync(
        HttpContext context, IOrganizationStore organizations, Organization pinned)
    {
        var request = context.Request;

        // application/x-www-form-urlencoded ONLY, and anything else is refused rather than forwarded
        // unexamined. HasFormContentType also admits multipart/form-data, whose fields QueryHelpers
        // cannot see at all — so a multipart body naming another organisation read as "names none",
        // sailed past the conflict check, and was then parsed perfectly well by the endpoint's own form
        // reader. Nothing legitimate is lost: the PAR endpoint requires urlencoded anyway, so a request
        // refused here would have been refused there.
        if (!IsFormUrlEncoded(request.ContentType))
            return await RefusePinnedAsync(context, pinned, "the content type cannot be pinned");

        // A DECLARED over-bound length is left to the endpoint's own 413, which is the better answer and
        // costs no read at all.
        if (request.ContentLength > MaxPushedAuthorizationBodyBytes)
            return false;

        // Everything else is read, including a body with no declared length. Skipping those was a hole:
        // chunked transfer-encoding and HTTP/2 both routinely omit Content-Length, so the most ordinary
        // shape of a pushed request went through unpinned and unrefused.
        var raw = await ReadBoundedBodyAsync(request, context.RequestAborted);
        if (raw is null)
            return await RefusePinnedAsync(context, pinned, "the body exceeds the size that can be pinned");

        var body = System.Text.Encoding.UTF8.GetString(raw);
        var fields = QueryHelpers.ParseQuery(body);

        string? requested = null;
        foreach (var name in OrganizationParameters)
        {
            if (fields.TryGetValue(name, out var values)
                && values.FirstOrDefault() is { } value
                && !string.IsNullOrWhiteSpace(value))
            {
                requested = value.Trim();
                break;
            }
        }

        if (requested is not null)
        {
            // Put the body back either way: the refusal path writes a response and never calls the
            // endpoint, but an accepted one must hand it a readable stream.
            request.Body = new MemoryStream(raw);
            request.ContentLength = raw.Length;
            return await RefuseUnlessPinnedAsync(context, organizations, pinned, requested);
        }

        // Blank aliases are dropped for the same reason as on the query: beside the pin they would be
        // stored as a repeated or conflicting organisation parameter.
        var keptBody = string.Join("&", fields
            .Where(f => !OrganizationParameters.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(f => f.Value.Select(v => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(v ?? ""))));
        var pinnedBody = System.Text.Encoding.UTF8.GetBytes(
            (keptBody.Length == 0 ? "" : keptBody + "&") + "organization=" + Uri.EscapeDataString(pinned.Id));
        request.Body = new MemoryStream(pinnedBody);
        request.ContentLength = pinnedBody.Length;
        logger.LogDebug(
            "Pinned pushed authorization request on {Host} to organization {OrganizationId}",
            context.Request.Host.Host, pinned.Id);
        return false;
    }

    /// <summary>
    /// Refuses the request unless <paramref name="requested"/> names the pinned organisation. Returns
    /// true when it was refused.
    /// </summary>
    /// <remarks>
    /// The requested value is resolved before comparing, by the rule <see cref="OrganizationSelector"/>
    /// uses everywhere else: a value carrying any uppercase character cannot be a slug (slugs are
    /// lowercase-only), so it is read as an id, exactly and once; an all-lowercase value could be either
    /// and is tried as a slug first. Comparing the raw strings instead would refuse a relying party that
    /// names the pinned organisation by its slug while the host pins its id.
    /// <para>
    /// The refusal is delivered directly rather than reflected to <c>redirect_uri</c>. Reflecting it
    /// would mean re-implementing the registered-redirect-URI check here, and bouncing an OAuth error to
    /// a URI this middleware has not validated against the client is the open redirect that check exists
    /// to prevent. The condition is a misconfiguration between a pinned host and a relying party, not an
    /// end-user state, so a visible error is the useful one.
    /// </para>
    /// <para>
    /// The requested value is caller-supplied and so is never echoed into the response or the log
    /// message — an error_description travels to the relying party and often onto a screen.
    /// </para>
    /// </remarks>
    private async Task<bool> RefuseUnlessPinnedAsync(
        HttpContext context, IOrganizationStore organizations, Organization pinned, string requested)
    {
        var resolved = await OrganizationSelector.ResolveBySlugThenIdAsync(
            organizations, requested, context.RequestAborted);

        if (resolved is not null && string.Equals(resolved.Id, pinned.Id, StringComparison.Ordinal))
            return false;

        return await RefusePinnedAsync(context, pinned, "the request named a different organization");
    }

    /// <summary>
    /// Writes the one refusal this middleware has and returns true, so every caller can
    /// <c>return await</c> it. The <paramref name="reason"/> is logged, never sent.
    /// </summary>
    /// <remarks>
    /// One body for every reason on purpose. A caller learns only that the request may not proceed on
    /// this host, not which of the checks stopped it — a conflicting organisation, a content type whose
    /// fields cannot be inspected, or a body too large to inspect are all "this request cannot be
    /// pinned, so it does not run", and telling them apart would only help someone looking for the shape
    /// that does not get inspected.
    /// </remarks>
    private async Task<bool> RefusePinnedAsync(HttpContext context, Organization pinned, string reason)
    {
        logger.LogWarning(
            "Refusing an authorization request on {Host}: the host is pinned to organization {OrganizationId} and {Reason}",
            context.Request.Host.Host, pinned.Id, reason);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(RefusalBody, context.RequestAborted);
        return true;
    }

    /// <summary>
    /// True when <paramref name="contentType"/> is <c>application/x-www-form-urlencoded</c> — the only
    /// body shape whose fields this middleware can read, and the only one the PAR endpoint accepts.
    /// </summary>
    private static bool IsFormUrlEncoded(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The whole request body, or null when it is larger than the PAR endpoint's own bound.
    /// </summary>
    /// <remarks>
    /// Reads incrementally and stops the moment the bound is passed, so an unmeasured body cannot be
    /// used to make this middleware buffer more than the endpoint itself would.
    /// </remarks>
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, ct);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPushedAuthorizationBodyBytes) return null;
        }

        return buffer.ToArray();
    }
}

public static class OrganizationPinMiddlewareExtensions
{
    /// <summary>
    /// Enforces an authoritative organisation pin (<see cref="ITenantContext.OrganizationIdIsAuthoritative"/>)
    /// on <c>/connect/authorize</c> and <c>POST /connect/par</c>. <c>UseAuthagonal</c> calls this.
    /// </summary>
    public static IApplicationBuilder UseOrganizationPin(this IApplicationBuilder app) =>
        app.UseMiddleware<OrganizationPinMiddleware>();
}
