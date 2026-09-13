using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Default <see cref="IOrganizationStore"/> — empty, and read-only. Keeps every host that has not
/// adopted organisations resolving DI without configuring a store; the cloud and the three durable
/// providers override it.
/// </summary>
/// <remarks>
/// Empty is the correct default and not a degraded one. With no organisation records, an
/// <c>organization</c> authorize parameter naming one is refused (there is nothing to name), no
/// membership gate engages, and an account carrying a legacy
/// <see cref="AuthUser.OrganizationId"/> keeps emitting <c>org_id</c> from that string exactly as it
/// did before this store existed. A deployment gets the previous behaviour, not a broken one.
/// <para>
/// The writes refuse, for the same reason <see cref="InMemoryScimGroupRoleMappingStore"/>'s do: this
/// is authorization state read on the token-issuance path, and a process-local dictionary with no
/// cross-node invalidation means an organisation DISABLED on one node keeps minting tokens on every
/// other for as long as those processes live. A host that wants writable organisations registers a
/// durable <see cref="IOrganizationStore"/>.
/// </para>
/// </remarks>
public sealed class InMemoryOrganizationStore : IOrganizationStore
{
    private const string NotDurable =
        "The default in-memory organization store is read-only: it is process-local with no cross-node " +
        "invalidation, so an organization disabled on one node would keep minting tokens on every other. " +
        "Register a durable IOrganizationStore before creating organizations.";

    public Task<Organization?> GetAsync(string organizationId, CancellationToken ct = default) =>
        Task.FromResult<Organization?>(null);

    public Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        Task.FromResult<Organization?>(null);

    public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Organization>>([]);

    public Task UpsertAsync(Organization organization, CancellationToken ct = default) =>
        throw new NotSupportedException(NotDurable);

    public Task DeleteAsync(string organizationId, CancellationToken ct = default) =>
        throw new NotSupportedException(NotDurable);
}
