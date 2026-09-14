using Authagonal.Core.Models;

namespace Authagonal.Core.Stores;

public interface IOidcProviderStore
{
    Task<OidcProviderConfig?> GetAsync(string connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<OidcProviderConfig>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Every connection belonging to <paramref name="organizationId"/> — the org-scoped subset of
    /// <see cref="GetAllAsync"/>.
    /// </summary>
    /// <remarks>
    /// Answered by listing and filtering rather than by an index row, deliberately: a tenant holds tens
    /// of connections, not millions, every durable implementation already scans this table for
    /// <see cref="GetAllAsync"/> on the login page's first-paint path, and an index would be a second
    /// row to keep consistent with a field an admin can change on any update.
    /// </remarks>
    Task<IReadOnlyList<OidcProviderConfig>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default);
    Task UpsertAsync(OidcProviderConfig config, CancellationToken ct = default);
    Task DeleteAsync(string connectionId, CancellationToken ct = default);
}
