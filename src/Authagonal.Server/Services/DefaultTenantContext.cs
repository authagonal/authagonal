using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// Default single-tenant implementation of <see cref="ITenantContext"/>.
/// Reads the issuer from IConfiguration, suitable for standalone (non-Cloud) deployments.
/// </summary>
/// <remarks>
/// It also pins hosts to organisations from configuration alone. The section
/// <c>Organizations:PinnedHosts</c> maps a host name to an organisation id:
/// <c>Organizations__PinnedHosts__login.acme.example=ORG_ID</c> in the environment, or
/// <c>--Organizations:PinnedHosts:login.acme.example=ORG_ID</c> on the command line. The map is read on
/// every request, so an edit to a reloadable configuration source applies at once.
/// </remarks>
public sealed class DefaultTenantContext : ITenantContext
{
    /// <summary>The configuration section that maps a host name to the organisation it is pinned to.</summary>
    public const string PinnedHostsSection = "Organizations:PinnedHosts";

    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public string TenantId => "default";
    public string Issuer { get; }

    /// <summary>
    /// The constructor that existed before pins: a context with no request to read, so it never pins.
    /// Kept as its own overload (rather than an optional parameter) so code that constructs it by
    /// reflection or from a compiled caller keeps working.
    /// </summary>
    public DefaultTenantContext(IConfiguration configuration) : this(configuration, null)
    {
    }

    public DefaultTenantContext(IConfiguration configuration, IHttpContextAccessor? httpContextAccessor)
    {
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        Issuer = configuration["Issuer"]
            ?? throw new InvalidOperationException("Issuer is not configured");
    }

    /// <summary>
    /// The organisation id the request's host is pinned to, or null when the host has no entry, the
    /// entry is empty, or there is no request.
    /// </summary>
    /// <remarks>
    /// The host is compared with <see cref="StringComparison.OrdinalIgnoreCase"/> against
    /// <c>Request.Host.Host</c>, so a port plays no part. The entries are walked rather than looked up
    /// by key: a host name carries dots and an IPv6 literal carries colons, and a colon is the
    /// configuration key delimiter.
    /// </remarks>
    public string? OrganizationId
    {
        get
        {
            var host = _httpContextAccessor?.HttpContext?.Request.Host.Host?.TrimEnd('.');
            if (string.IsNullOrEmpty(host)) return null;

            foreach (var entry in _configuration.GetSection(PinnedHostsSection).GetChildren())
            {
                if (!string.Equals(entry.Key.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase)) continue;

                var id = entry.Value?.Trim();
                return string.IsNullOrEmpty(id) ? null : id;
            }

            return null;
        }
    }

    /// <summary>A host a configured entry pins is authoritative; a host with none is as it always was.</summary>
    public bool OrganizationIdIsAuthoritative => !string.IsNullOrEmpty(OrganizationId);
}
