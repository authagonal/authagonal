using Authagonal.Core.Stores;

namespace Authagonal.Migration;

/// <summary>
/// The store abstraction the NDJSON import engine writes through. Bundled in its own record — mirroring
/// <see cref="DuendeMigrationStores"/> — so the CLI's target wiring and any future in-host caller can
/// supply the engine identically. Only <see cref="Users"/> today: this source imports users only, unlike
/// the Duende engine which also migrates clients, roles, scopes and federation config.
/// </summary>
public sealed record NdjsonUserImportStores
{
    public required IUserStore Users { get; init; }
}
