using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

/// <summary>
/// One <see cref="OrganizationMembership"/>, written as a full document into both
/// <c>OrganizationMembers</c> (PK="org|{organizationId}", RK=userId — lists by organization) and
/// <c>UserMemberships</c> (PK="user|{userId}", RK=organizationId — lists by user) — the same
/// forward/reverse full-document shape <c>ScimTokenEntity</c> uses, split across two tables instead of
/// two row keys in one because each side's partition needs to hold many rows for its own key.
/// </summary>
public sealed class OrganizationMembershipEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public required string OrganizationId { get; set; }
    public required string UserId { get; set; }
    public string? RolesJson { get; set; }
    public required string Status { get; set; }
    public string? InvitedByUserId { get; set; }
    public DateTimeOffset? InvitedAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Org-partitioned row: PK="org|{organizationId}" (env-prefixed by the store), RK=userId.</summary>
    public static OrganizationMembershipEntity FromModelForOrganization(OrganizationMembership membership) => new()
    {
        PartitionKey = OrgPartition(membership.OrganizationId),
        RowKey = membership.UserId,
        OrganizationId = membership.OrganizationId,
        UserId = membership.UserId,
        RolesJson = Pack(membership.Roles),
        Status = membership.Status,
        InvitedByUserId = membership.InvitedByUserId,
        InvitedAt = membership.InvitedAt,
        JoinedAt = membership.JoinedAt,
        CreatedAt = membership.CreatedAt,
        UpdatedAt = membership.UpdatedAt,
    };

    /// <summary>User-partitioned row: PK="user|{userId}" (env-prefixed by the store), RK=organizationId.</summary>
    public static OrganizationMembershipEntity FromModelForUser(OrganizationMembership membership) => new()
    {
        PartitionKey = UserPartition(membership.UserId),
        RowKey = membership.OrganizationId,
        OrganizationId = membership.OrganizationId,
        UserId = membership.UserId,
        RolesJson = Pack(membership.Roles),
        Status = membership.Status,
        InvitedByUserId = membership.InvitedByUserId,
        InvitedAt = membership.InvitedAt,
        JoinedAt = membership.JoinedAt,
        CreatedAt = membership.CreatedAt,
        UpdatedAt = membership.UpdatedAt,
    };

    public OrganizationMembership ToModel() => new()
    {
        OrganizationId = OrganizationId,
        UserId = UserId,
        Roles = Unpack(RolesJson),
        Status = Status,
        InvitedByUserId = InvitedByUserId,
        InvitedAt = InvitedAt,
        JoinedAt = JoinedAt,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    private static string? Pack(List<string> roles) =>
        roles.Count == 0 ? null : JsonSerializer.Serialize(roles, AzureJsonContext.Default.ListString);

    private static List<string> Unpack(string? packed) =>
        string.IsNullOrEmpty(packed) ? [] : JsonSerializer.Deserialize(packed, AzureJsonContext.Default.ListString) ?? [];

    /// <summary>The natural (un-prefixed) PartitionKey of the org-partitioned row.</summary>
    public static string OrgPartition(string organizationId) => $"org|{organizationId}";

    /// <summary>The natural (un-prefixed) PartitionKey of the user-partitioned row.</summary>
    public static string UserPartition(string userId) => $"user|{userId}";
}
