using System.Text.Json;
using System.Text.Json.Nodes;
using Authagonal.Core.Models;
using Microsoft.Extensions.Logging;

namespace Authagonal.Server.Services;

/// <summary>
/// Merges an <see cref="Organization.BrandingJson"/> over a tenant's branding, field by field.
/// </summary>
/// <remarks>
/// The contract is <see cref="Organization.BrandingJson"/>'s own: null means inherit everything, and a
/// host that renders it merges field by field, so an organisation that sets only a logo keeps the
/// tenant's colours.
/// <para>
/// It works on JSON rather than a typed model because the branding a host hands the login page is not
/// a shape this library owns. The standalone server's is an operator's <c>branding.json</c>, whose keys
/// the SPA reads and the library does not model; a typed model would drop them on the way through.
/// </para>
/// </remarks>
public static class OrganizationBranding
{
    private enum Kind
    {
        String,
        Boolean,
        /// <summary>A string, or an object whose values are all strings (language code to text).</summary>
        Localized,
        /// <summary>An array of <c>{ code: string, label: string }</c>.</summary>
        Languages,
    }

    /// <summary>
    /// What an organisation may set, by the name the login app reads it under: <c>BrandingConfig</c> in
    /// <c>login-app/src/branding.ts</c>, plus the five email-surface colours a multi-tenant host also
    /// keeps, and less <c>showRegistration</c>. The tenant's own policy decides whether the login page
    /// advertises registration, and an organisation is not the authority for that decision.
    /// <c>showForgotPassword</c> is not policy and an organisation may set it.
    /// </summary>
    private static readonly Dictionary<string, (string Name, Kind Kind)> AllowedKeys = Build(
        ("appName", Kind.String),
        ("logoUrl", Kind.String),
        ("primaryColor", Kind.String),
        ("supportEmail", Kind.String),
        ("showForgotPassword", Kind.Boolean),
        ("customCssUrl", Kind.String),
        ("welcomeTitle", Kind.Localized),
        ("welcomeSubtitle", Kind.Localized),
        ("languages", Kind.Languages),
        ("poweredBy", Kind.Boolean),
        ("darkMode", Kind.String),
        ("lightBg", Kind.String),
        ("lightCardBg", Kind.String),
        ("darkBg", Kind.String),
        ("darkCardBg", Kind.String),
        ("darkPrimaryColor", Kind.String),
        ("lightLogoBg", Kind.String),
        ("darkLogoBg", Kind.String),
        ("emailBg", Kind.String),
        ("emailHeaderColor", Kind.String),
        ("emailCardBg", Kind.String),
        ("emailFooterColor", Kind.String),
        ("emailLogoBg", Kind.String));

    private static Dictionary<string, (string Name, Kind Kind)> Build(params (string Name, Kind Kind)[] keys) =>
        keys.ToDictionary(k => k.Name, k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a new object holding <paramref name="tenantBranding"/> with the organisation's branding
    /// merged over it. The input is never changed.
    /// </summary>
    /// <remarks>
    /// A key that is present and not null overrides; one that is absent or null inherits and does not
    /// clear. A key that is not an allowed one is ignored, because the organisation record is shared
    /// with hosts whose branding model is not this one. Names are matched without regard to case and
    /// written under their canonical name. A value of the wrong kind keeps the tenant's value for that
    /// key and nothing else. <c>languages</c> replaces the tenant's list.
    /// <para>
    /// Every failure degrades to the tenant's branding rather than to an exception: malformed JSON, a
    /// document that is not an object, and an unusable value are operator mistakes on a record nobody
    /// validated at write time, and a login page that renders unstyled is recoverable where one that
    /// fails is not.
    /// </para>
    /// </remarks>
    public static JsonObject Merge(JsonObject tenantBranding, Organization organization, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(tenantBranding);
        ArgumentNullException.ThrowIfNull(organization);

        var merged = (JsonObject)tenantBranding.DeepClone();
        if (string.IsNullOrWhiteSpace(organization.BrandingJson))
            return merged;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(organization.BrandingJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Organization {OrganizationId} has malformed brandingJson; rendering tenant branding",
                organization.Id);
            return merged;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning(
                    "Organization {OrganizationId} has a brandingJson that is not a JSON object ({Kind}); rendering tenant branding",
                    organization.Id, document.RootElement.ValueKind);
                return merged;
            }

            foreach (var field in document.RootElement.EnumerateObject())
            {
                try
                {
                    MergeField(merged, field, organization, logger);
                }
                catch (InvalidOperationException)
                {
                    // A key or string value holding a lone surrogate escape parses but cannot be decoded;
                    // that field fails alone and the tenant's value stays. The name is not logged, being
                    // the thing that may be undecodable.
                    logger.LogWarning(
                        "Organization {OrganizationId} has an undecodable brandingJson key or value; keeping the tenant's",
                        organization.Id);
                }
            }

            return merged;
        }
    }

    private static void MergeField(JsonObject merged, JsonProperty field, Organization organization, ILogger logger)
    {
        if (field.Value.ValueKind == JsonValueKind.Null) return;
        if (!AllowedKeys.TryGetValue(field.Name, out var key)) return;

        if (ToNode(field.Value, key.Kind) is not { } value)
        {
            logger.LogWarning(
                "Organization {OrganizationId} has an unusable brandingJson value for {Field}; keeping the tenant's",
                organization.Id, field.Name);
            return;
        }

        // The tenant's document may carry the key under another casing; leaving it would
        // leave two keys the SPA would read as one.
        foreach (var existing in merged.Select(p => p.Key)
                     .Where(k => !string.Equals(k, key.Name, StringComparison.Ordinal)
                                 && string.Equals(k, key.Name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            merged.Remove(existing);

        merged[key.Name] = value;
    }

    /// <summary>The element as a node of the kind the key takes, or null when it is another kind.</summary>
    private static JsonNode? ToNode(JsonElement element, Kind kind)
    {
        switch (kind)
        {
            case Kind.String:
                return element.ValueKind == JsonValueKind.String ? JsonValue.Create(element.GetString()) : null;

            case Kind.Boolean:
                return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? JsonValue.Create(element.GetBoolean())
                    : null;

            case Kind.Localized:
                if (element.ValueKind == JsonValueKind.String)
                    return JsonValue.Create(element.GetString());
                if (element.ValueKind != JsonValueKind.Object) return null;

                var localized = new JsonObject();
                foreach (var entry in element.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String) return null;
                    localized[entry.Name] = JsonValue.Create(entry.Value.GetString());
                }

                return localized;

            case Kind.Languages:
                if (element.ValueKind != JsonValueKind.Array) return null;

                var languages = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !TryGetString(item, "code", out var code)
                        || !TryGetString(item, "label", out var label))
                        return null;

                    languages.Add((JsonNode)new JsonObject { ["code"] = code, ["label"] = label });
                }

                return languages;

            default:
                return null;
        }
    }

    /// <summary>A string property read without regard to case; false when absent or not a string.</summary>
    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.String) return false;

            value = property.Value.GetString()!;
            return true;
        }

        return false;
    }
}
