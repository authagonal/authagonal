using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Authagonal.Core.Models;

namespace Authagonal.AzureProvider.Entities;

public sealed class OrganizationEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string OrganizationsPartition = "org";

    public required string Slug { get; set; }
    public required string DisplayName { get; set; }
    public string? MetadataJson { get; set; }
    public string? BrandingJson { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowAutoMembership { get; set; }
    public bool RequireMembershipForTokens { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Full document: PK="org" (env-prefixed by the store), RK=Id.</summary>
    public static OrganizationEntity FromModel(Organization org) => new()
    {
        PartitionKey = OrganizationsPartition,
        RowKey = org.Id,
        Slug = org.Slug,
        DisplayName = org.DisplayName,
        MetadataJson = org.Metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(org.Metadata, AzureJsonContext.Default.DictionaryStringString),
        BrandingJson = org.BrandingJson,
        Enabled = org.Enabled,
        AllowAutoMembership = org.AllowAutoMembership,
        RequireMembershipForTokens = org.RequireMembershipForTokens,
        CreatedAt = org.CreatedAt,
        UpdatedAt = org.UpdatedAt,
    };

    public Organization ToModel() => new()
    {
        Id = RowKey,
        Slug = Slug,
        DisplayName = DisplayName,
        Metadata = string.IsNullOrEmpty(MetadataJson)
            ? []
            : JsonSerializer.Deserialize(MetadataJson, AzureJsonContext.Default.DictionaryStringString) ?? [],
        BrandingJson = BrandingJson,
        Enabled = Enabled,
        AllowAutoMembership = AllowAutoMembership,
        RequireMembershipForTokens = RequireMembershipForTokens,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    /// <summary>Slug index entity: PK="orgslug" (env-prefixed by the store), RK=Slug.</summary>
    public static OrganizationSlugEntity CreateSlugIndex(Organization org) => new()
    {
        PartitionKey = OrganizationSlugEntity.SlugsPartition,
        RowKey = org.Slug,
        OrganizationId = org.Id,
    };
}

/// <summary>
/// Slug lookup index for <see cref="OrganizationEntity"/> — mirrors
/// <see cref="ScimGroupExternalIdEntity"/>'s role: a separate table holding only the pointer, so
/// resolving the <c>organization</c> authorize parameter to an id is a point read rather than a scan.
/// </summary>
public sealed class OrganizationSlugEntity : ITableEntity
{
    public required string PartitionKey { get; set; }
    public required string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public const string SlugsPartition = "orgslug";

    public required string OrganizationId { get; set; }
}
