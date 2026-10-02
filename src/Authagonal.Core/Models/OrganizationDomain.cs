namespace Authagonal.Core.Models;

/// <summary>
/// An email domain an <see cref="Organization"/> has claimed, and whether that claim has been proven.
/// A verified domain is what lets <see cref="Organization.AllowAutoMembership"/> admit a user who was
/// never invited: their confirmed email address sits under a domain the organisation demonstrably
/// controls.
/// </summary>
/// <remarks>
/// The library stores the claim and reads <see cref="VerifiedAt"/>; it never performs the proof.
/// Verification (typically a DNS TXT record carrying <see cref="VerificationToken"/>) is the host's
/// job, because the host owns the resolver, the record naming convention and the retry policy, and
/// stamps <see cref="VerifiedAt"/> when it succeeds.
/// </remarks>
public sealed class OrganizationDomain
{
    private string _domain = "";

    /// <summary>
    /// The domain, stored normalised by <see cref="Normalize"/>: trimmed, lowercase, no trailing dot.
    /// Normalised in the setter so every provider persists and compares the same form, whoever wrote it.
    /// </summary>
    /// <remarks>
    /// Matched EXACTLY against the part of an email address after its last <c>@</c>. A verified
    /// <c>acme.com</c> does not admit <c>user@eu.acme.com</c>: a subdomain can be delegated to a party
    /// the organisation does not control, so each one must be claimed and verified on its own.
    /// </remarks>
    public required string Domain
    {
        get => _domain;
        set => _domain = Normalize(value);
    }

    /// <summary>
    /// The value the host asks the domain's owner to publish (for example in a DNS TXT record) to prove
    /// control. Opaque to the library.
    /// </summary>
    public string VerificationToken { get; set; } = "";

    /// <summary>When the claim was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the host proved control of the domain. Null while unverified, and an unverified domain
    /// grants nothing: only a domain with a value here can admit a user through
    /// <see cref="Organization.AllowAutoMembership"/>.
    /// </summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>
    /// The stored form of a domain: trimmed, lowercased (invariant), with any trailing dot removed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    public static string Normalize(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
