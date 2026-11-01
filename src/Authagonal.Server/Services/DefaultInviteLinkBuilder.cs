using Authagonal.Core.Models;
using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

/// <summary>
/// The library's <see cref="IInviteLinkBuilder"/>: the issuer's own <c>/accept-invite</c> page, with the
/// organisation named by its slug when the invite is into one.
/// </summary>
public sealed class DefaultInviteLinkBuilder(ITenantContext tenantContext) : IInviteLinkBuilder
{
    /// <inheritdoc />
    public Task<string> BuildAcceptUrlAsync(string token, Organization? organization, CancellationToken ct = default)
    {
        var url = $"{tenantContext.Issuer}/accept-invite?p={Uri.EscapeDataString(token)}";
        if (organization is not null)
            url += $"&organization={Uri.EscapeDataString(organization.Slug)}";
        return Task.FromResult(url);
    }
}
