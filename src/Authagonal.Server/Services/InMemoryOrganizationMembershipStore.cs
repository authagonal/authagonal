using Authagonal.Core.Models;
using Authagonal.Core.Stores;

namespace Authagonal.Server.Services;

/// <summary>
/// Default <see cref="IOrganizationMembershipStore"/> — empty, and read-only. The membership
/// counterpart to <see cref="InMemoryOrganizationStore"/>, registered for the same reason: DI
/// resolves on a host that has not adopted organisations.
/// </summary>
/// <remarks>
/// Empty here is never a fail-open. The membership gate only engages for a request that actually
/// selected an <see cref="Organization"/>, and with the default organisation store no request can
/// select one — so an empty membership store gates nothing because nothing is gated. It becomes
/// load-bearing only alongside a durable organisation store, and pairing a durable organisation
/// store with this one would refuse every login to every organisation, loudly, on the first attempt.
/// <para>
/// The writes refuse, like every other in-memory default over authorization state: a membership
/// revoked on one node would keep authorising tokens on every other.
/// </para>
/// </remarks>
public sealed class InMemoryOrganizationMembershipStore : IOrganizationMembershipStore
{
    private const string NotDurable =
        "The default in-memory organization membership store is read-only: it is process-local with no " +
        "cross-node invalidation, so a membership revoked on one node would keep authorising tokens on " +
        "every other. Register a durable IOrganizationMembershipStore before granting memberships.";

    public Task<OrganizationMembership?> GetAsync(string organizationId, string userId, CancellationToken ct = default) =>
        Task.FromResult<OrganizationMembership?>(null);

    public Task<IReadOnlyList<OrganizationMembership>> ListByUserAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrganizationMembership>>([]);

    public Task<IReadOnlyList<OrganizationMembership>> ListByOrganizationAsync(string organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrganizationMembership>>([]);

    public Task UpsertAsync(OrganizationMembership membership, CancellationToken ct = default) =>
        throw new NotSupportedException(NotDurable);

    public Task DeleteAsync(string organizationId, string userId, CancellationToken ct = default) =>
        throw new NotSupportedException(NotDurable);
}
