using System.Text;
using System.Text.Json.Nodes;
using Authagonal.Server.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server;

/// <summary>
/// The standalone server's login document: <c>index.html</c> with the boot payload inlined.
/// </summary>
public static class AuthagonalLoginFallbackExtensions
{
    /// <summary>The longest one request waits for the stores before serving the plain document.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(10);

    private const string IndexFile = "index.html";
    private const string BrandingFile = "branding.json";

    /// <summary>
    /// Serves the web root's <c>index.html</c> for every path that is not a file, with the login boot
    /// payload (<see cref="LoginBootPayloadBuilder"/>) inlined just before <c>&lt;/head&gt;</c>. Use it in
    /// place of <c>MapFallbackToFile("index.html")</c>, after <see cref="AuthagonalExtensions.UseAuthagonal"/>.
    /// Opt-in: <see cref="AuthagonalExtensions.MapAuthagonalEndpoints"/> does not call it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without the payload the login app spends two serial round trips before first paint (branding.json,
    /// then the provider list). With it the server, which already knows the organisation the request is
    /// for, answers both in the document. The login app keeps its fetch fallback, so a document without a
    /// payload is the page as it has always been.
    /// </para>
    /// <list type="bullet">
    ///   <item><c>index.html</c> is read once, on the first request that needs it.</item>
    ///   <item><c>branding.json</c> is read on every request, so an operator's edit to a mounted file
    ///   applies at once, as it does for <c>/branding.json</c> itself. An absent file, or one that is not
    ///   a JSON object, is the empty object; a malformed one logs a warning.</item>
    ///   <item>The Turnstile site key is the one <c>/api/auth/providers</c> answers with.</item>
    ///   <item>Any failure, a store that is down included, serves the plain <c>index.html</c>. Reading the
    ///   stores is bounded to ten seconds, so an unreachable one costs a request that long at most.</item>
    /// </list>
    /// <para>
    /// <c>/</c> is answered by the default-files middleware with the static <c>index.html</c>, before this
    /// endpoint is reached, so the payload is on every login page but the bare root.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapAuthagonalLoginFallback(this IEndpointRouteBuilder endpoints)
    {
        var environment = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Authagonal.Server.LoginFallback");

        byte[]? shellBytes = null;
        string? shellText = null;
        var shellLock = new object();

        // index.html is baked into the image: read it once. Null when there is none.
        (byte[] Bytes, string Text)? Shell()
        {
            if (shellBytes is null)
            {
                var file = environment.WebRootFileProvider.GetFileInfo(IndexFile);
                if (!file.Exists)
                    return null;
                lock (shellLock)
                {
                    if (shellBytes is null)
                    {
                        using var stream = file.CreateReadStream();
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        shellText = new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                        shellBytes = buffer.ToArray();
                    }
                }
            }
            return (shellBytes, shellText!);
        }

        return endpoints.MapFallback("{*path:nonfile}", async (HttpContext context) =>
            {
                var shell = Shell();
                if (shell is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";

                string? injected = null;
                try
                {
                    var tenantBranding = await ReadBrandingAsync(environment.WebRootFileProvider, logger, context.RequestAborted);
                    var turnstile = context.RequestServices.GetRequiredService<ITurnstileKeyProvider>();
                    var builder = context.RequestServices.GetRequiredService<LoginBootPayloadBuilder>();

                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    bound.CancelAfter(BuildTimeout);
                    var payload = await builder
                        .BuildAsync(context.Request, tenantBranding, turnstile.SiteKey, bound.Token)
                        .WaitAsync(BuildTimeout, context.RequestAborted);

                    injected = LoginBootPayloadBuilder.InjectIntoShell(shell.Value.Text, payload);
                }
                catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
                {
                    // The page works without the payload: the login app fetches both itself.
                    logger.LogWarning(e, "The login document could not be given its boot payload; serving the plain index.html");
                }

                if (injected is not null)
                    await context.Response.WriteAsync(injected, new UTF8Encoding(false), context.RequestAborted);
                else
                    await context.Response.Body.WriteAsync(shell.Value.Bytes, context.RequestAborted);
            })
            .WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]));
    }

    /// <summary>
    /// The operator's <c>branding.json</c> from the web root: the empty object when it is absent, and
    /// when it is not a JSON object (a warning for the latter).
    /// </summary>
    private static async Task<JsonObject> ReadBrandingAsync(
        IFileProvider webRoot, ILogger logger, CancellationToken ct)
    {
        var file = webRoot.GetFileInfo(BrandingFile);
        if (!file.Exists)
            return new JsonObject();

        string text;
        await using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = await reader.ReadToEndAsync(ct);

        try
        {
            if (JsonNode.Parse(text) is JsonObject branding)
                return branding;
            logger.LogWarning("{File} is not a JSON object; serving the login page with no tenant branding", BrandingFile);
        }
        catch (System.Text.Json.JsonException e)
        {
            logger.LogWarning(e, "{File} is not valid JSON; serving the login page with no tenant branding", BrandingFile);
        }
        return new JsonObject();
    }
}
