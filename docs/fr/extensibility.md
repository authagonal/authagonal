---
layout: default
title: Extensibilité
locale: fr
---

# Extensibilité

Authagonal peut être hébergé comme bibliothèque dans votre propre projet ASP.NET Core, avec un contrôle total sur les implémentations des services.

## Méthodes d'extension {#extension-methods}

Trois méthodes intègrent Authagonal à n'importe quelle application ASP.NET Core :

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Hébergement multi-locataire {#multi-tenant-hosting}

Pour les déploiements multi-locataires, utilisez plutôt `AddAuthagonalCore()`. Elle enregistre les endpoints, les middlewares et les services de base, mais ignore le stockage et les services d'arrière-plan : c'est à vous de les fournir par locataire. La gestion des clés de signature utilise par défaut le singleton `ProtocolKeyManager` de `Authagonal.Protocol`, et un hôte qui enregistre son propre `IKeyManager` avant `AddAuthagonalCore()` le conserve :

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` et les interfaces de store (`IClientStore`, `IScimTokenStore`, etc.) sont résolus depuis `HttpContext.RequestServices` au moment de la requête : les enregistrements scoped fonctionnent donc correctement pour l'isolation par locataire.

### Intégrer `Authagonal.Protocol` seul {#embedding-authagonalprotocol-alone}

Un hôte qui ne veut que la surface du protocole OIDC (sa propre authentification, son propre pipeline, des endpoints `/connect/*` prêts à l'emploi) appelle `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` sans rien de `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` et `/connect/par` refusent aussi le http en clair dans cette configuration, conformément à la RFC 6749 §3.1/§3.2. Comme le package est intégré à un pipeline qui ne lui appartient pas, l'exigence est portée par les endpoints sous forme de filtre plutôt que de middleware : elle tient donc quelle que soit la composition de votre pipeline, et que vous mappiez toute la surface ou un endpoint à la fois. Deux conséquences à connaître avant la mise à niveau :

- **Derrière un proxy qui termine le TLS, appelez `UseForwardedHeaders` en déclarant le proxy.** Le filtre lit le schéma après le routage : un `X-Forwarded-Proto: https` transféré le satisfait donc. Sans ce middleware, votre hôte voit du texte en clair, ce qui signifie aussi que vos cookies ne sont pas marqués `Secure` et que vos URL absolues générées sont fausses : cela vaut la peine d'être corrigé plutôt que contourné. Renseignez `KnownProxies` / `KnownNetworks` lors de son enregistrement : ASP.NET Core interprète un ensemble de confiance vide comme « chaque appelant est un proxy de confiance », ce qui livre le schéma à quiconque peut joindre votre hôte. Si le corps du refus mentionne un `X-Forwarded-Proto` non appliqué, c'est ce middleware qu'il réclame.
- **Un hôte qui sert réellement la surface du protocole en http active l'option**, de la même manière que le serveur :

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

La découverte et le JWKS ne sont délibérément pas soumis à ce contrôle : ce sont des métadonnées publiques, et un client incapable de les lire ne peut pas apprendre qu'il lui faut du https.

Lorsque vous utilisez `AddAuthagonal()` (le serveur complet), vous n'avez pas à le définir séparément : `Auth:AllowInsecureHttp` est propagé pour vous dans les options du protocole, de sorte qu'un seul interrupteur gouverne toute la surface.

## Remplacer des services {#overriding-services}

Enregistrez vos implémentations personnalisées **avant** d'appeler `AddAuthagonal()`. Authagonal utilise `TryAdd` en interne, si bien que vos enregistrements sont prioritaires :

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` est particulier : c'est un pipeline à enregistrements multiples. Enregistrez autant de hooks que vous le souhaitez (avec n'importe quelle durée de vie, `AddScoped` compris) : tous s'exécutent dans l'ordre d'enregistrement. Le `NullAuthHook` sans effet n'est ajouté que si aucun hook n'a été enregistré au moment où `AddAuthagonal()` / `AddAuthagonalCore()` s'exécute : enregistrez donc toujours vos hooks en premier.

### Points d'extension {#extensibility-points}

| Interface | Par défaut | Rôle |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sans effet, ajouté uniquement si aucun hook n'est enregistré) | Hooks de cycle de vie pour les événements d'authentification : journalisation d'audit, validation personnalisée, webhooks. Plusieurs hooks peuvent être enregistrés ; tous s'exécutent dans l'ordre |
| `IEmailService` | `NullEmailService` (sans effet), ou l'expéditeur Resend intégré lorsque `Email:ResendApiKey` est configuré | Envoi des e-mails de vérification, de réinitialisation du mot de passe et d'avis de compte existant |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Provisionnement des utilisateurs dans les applications en aval |
| `ISecretProvider` | `PlaintextSecretProvider`, ou le `KeyVaultSecretProvider` intégré lorsque `SecretProvider:VaultUri` est configuré | Stockage réversible des secrets (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lit `IConfiguration`) | Résolution du locataire pour les déploiements multi-locataires |
| `IKeyManager` | `ProtocolKeyManager` (singleton, de `Authagonal.Protocol`) | Gestion des clés de signature ; à remplacer pour isoler les clés par locataire |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Résout les applications de provisionnement disponibles ; à remplacer pour une résolution dynamique ou par locataire |
| `IAuditLogger` | `NullAuditLogger` (sans effet) | Piste d'audit des changements de configuration et des événements relevant de la sécurité |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (singleton, de `Authagonal.Protocol`) | Valide le contexte fourni par l'appelant lors d'une émission `client_credentials` et impose des revendications sur le jeton, ou la refuse |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (singleton, de `Authagonal.Protocol`) | Correspondance du sujet pour l'échange de jetons RFC 8693 ; voir [Authentification agentique](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (scoped, lit `TurnstileOptions`) | Quelle sitekey et quel secret Turnstile s'appliquent à cette requête |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (singleton, refuse toutes les origines) | Origines autorisées à effectuer des appels cross-origin avec identifiants vers `/api/auth/*` |

Trois autres points d'extension se situent au **niveau du store** plutôt que dans l'injection de dépendances : `IFieldCipher`, `IIndexTokenizer` et `IChangeWriter` (tous dans `Authagonal.Core.Services`). Les fournisseurs de stockage les acceptent comme paramètres de constructeur facultatifs ; voir leurs sections ci-dessous.

## IAuthHook {#iauthhook}

L'interface `IAuthHook` fournit des hooks dans le cycle de vie de l'authentification. Les méthodes situées sur le chemin critique (authentification, création d'utilisateur, émission de jetons) peuvent lever une exception pour interrompre l'opération ; les méthodes plus récentes sont des notifications a posteriori. Plusieurs implémentations de `IAuthHook` peuvent être enregistrées, et toutes s'exécutent dans l'ordre d'enregistrement.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Paramètres {#parameters}

| Méthode | Remarques et valeurs de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (les hôtes peuvent passer leur propre valeur, par exemple une origine SCIM) |
| `OnUserDeletedAsync` | `"admin"` ; simple notification, l'enregistrement peut ne plus être lisible |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Types d'octroi : `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Appelée après la vérification du mot de passe ; renvoie la politique MFA effective de l'utilisateur. Par défaut : renvoie `clientPolicy` inchangée. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Mêmes méthodes que `OnMfaVerifiedAsync`. Ne se déclenche qu'après un premier facteur valide : des rafales sont donc un signal fort de tentative de contournement de la MFA (à distinguer de `OnLoginFailedAsync`, l'étape du mot de passe) |
| `OnEmailConfirmedAsync` | L'utilisateur a confirmé son adresse e-mail via le lien de vérification ; déjà enregistré |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"` ; l'identifiant est déjà actif |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"` ; `mfaDisabled` vaut true lorsque la suppression n'a laissé aucun facteur principal |
| `OnRecoveryCodesRegeneratedAsync` | L'ancien jeu de codes de récupération est invalidé |
| `OnPasswordChangedAsync` | Par exemple `"reset"` ; le changement est enregistré et les sessions existantes sont invalidées |
| `OnTokenIssuingAsync` | Contrôle préalable à l'émission, contrairement à `OnTokenIssuedAsync`. Se déclenche sur `authorization_code`, `refresh_token` et `device_code`, ainsi que sur les deux émissions agentiques (échange de jetons délégué, et `client_credentials` pour un client doté d'un profil d'agent). Levez une exception pour refuser : une exception ordinaire devient `access_denied` avec son message ; levez `ProtocolTokenException` pour nommer votre propre erreur OAuth. Au rafraîchissement, il s'exécute avant la rotation : un refus laisse donc le jeton de rafraîchissement présenté utilisable. Le contexte porte `ClientId`, `SubjectId`, `GrantType`, `Scopes`, `RequestedAuthorityJson`, ainsi que `OrganizationId` / `OrganizationSlug` lorsque la requête a sélectionné une organisation |
| `OnDelegationMintedAsync` | Un jeton délégué (identité composite) a été émis par échange de jetons ; simple notification |
| `OnApprovalRequestedAsync` | Un échange délégué a été mis en attente sur une action soumise à une politique de demande, et une approbation en attente a été créée |
| `OnApprovalResolvedAsync` | Une approbation en attente a été approuvée ou refusée par l'utilisateur |
| `OnAgentConsentChangedAsync` | `change` vaut `"granted"` ou `"revoked"` (consentement permanent accordé à un agent) |
| `OnConsentRevokedAsync` | Un utilisateur a révoqué une application autorisée ; le consentement et les octrois du client liés à la session ont déjà disparu. `grantsRemoved` indique combien ont été supprimés (0 signifie aucun) |
| `OnCapabilityTicketRedeemedAsync` | Un ticket de capacité a été échangé contre le jeton auquel il est lié |

### Exemple : journal d'audit {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Exemple : restriction de domaine {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

Un jeton `client_credentials` n'a pas de sujet : le point d'extension de l'échange de jetons ne peut donc pas l'atteindre. Ce point d'extension s'adresse à un service appelant de première partie dont le jeton doit nommer le contexte dans lequel il agit (une organisation, un locataire) sans utilisateur. Il s'exécute après la validation du client, de ses scopes et des éventuelles ressources RFC 8707, et avant l'émission du jeton.

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` contient les paramètres de formulaire non protocolaires de la requête de jeton (à valeur unique, le premier l'emporte), par exemple un `organization_id` envoyé par l'appelant.
- Renvoyez `ClientCredentialsClaimsResult.Allow(claims)` pour imposer `claims` sur le jeton (null ou vide le laisse inchangé), ou `ClientCredentialsClaimsResult.Reject(error, description)` pour refuser l'émission avec cette erreur OAuth.
- Les noms de revendications réservés du protocole restent bloqués à l'émission.
- Validez le rattachement fourni par l'appelant auprès de votre propre autorité ; ne le recopiez pas sur le jeton sans contrôle.
- Le `NullClientCredentialsClaimsTransformer` par défaut est enregistré avec `TryAddSingleton` : enregistrez le vôtre en premier pour le remplacer.

## ITurnstileKeyProvider {#iturnstilekeyprovider}

Les deux clés Turnstile proviennent d'un même objet, de sorte que le widget affiché par le navigateur et le secret auquel le serveur se fie pour vérifier ne peuvent jamais diverger. Le `OptionsTurnstileKeyProvider` par défaut lit `SiteKey` et `SecretKey` dans `TurnstileOptions`, ce qui convient à un hôte servant un seul domaine. Un hôte qui sert des domaines fournis par ses clients, alors que Cloudflare plafonne le nombre de noms d'hôte d'un widget, enregistre sa propre implémentation scoped qui renvoie la paire de clés du widget attribué à l'hôte demandeur.

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

Enregistré avec `TryAddScoped` : un enregistrement effectué avant `AddAuthagonal` l'emporte donc.

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

L'API d'authentification interactive (`/api/auth/*`) refuse par défaut les appels cross-origin avec identifiants, car elle est pilotée par l'application de connexion servie depuis la même origine. Un hôte qui permet à un locataire de construire son propre écran de connexion sur une autre origine implémente cette interface pour se porter garant d'origines précises.

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- Consultée à chaque requête et pour chaque origine ; la résolution du locataire a déjà eu lieu lorsqu'elle est appelée.
- Renvoyer true permet à cette origine de lire les réponses authentifiées des endpoints de compte, de session, de profil et de configuration de la MFA pour la personne connectée. Ne répondez que pour des origines que l'hôte contrôle ou a vérifiées, jamais pour une origine tirée de la requête.
- L'implémentation par défaut (`DenyInteractiveCorsOriginPolicy`, `TryAddSingleton`) renvoie false pour toutes les origines.

## ISecretProvider {#isecretprovider}

`ISecretProvider` (dans `Authagonal.Core.Services`) est le point d'extension de chiffrement réversible des secrets stockés, tels que les secrets client SSO, les mots de passe SMTP et les graines TOTP. `ProtectAsync` transforme un texte en clair en une référence que le store enregistre ; `ResolveAsync` retransforme la référence en texte en clair. Le `PlaintextSecretProvider` par défaut stocke les valeurs telles quelles (la référence EST la valeur).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Définir `SecretProvider:VaultUri` branche automatiquement le `KeyVaultSecretProvider` intégré (Azure Key Vault via `DefaultAzureCredential`). Pour tout autre cas, enregistrez votre propre implémentation avant `AddAuthagonal()`.

## Chiffrement des champs de données personnelles : IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` chiffre au repos les valeurs individuelles des champs de données personnelles des utilisateurs (téléphone, entreprise, attributs personnalisés, adresse e-mail et noms sur la ligne de profil). C'est un point d'extension au niveau du store : les fournisseurs de stockage le reçoivent comme paramètre de constructeur facultatif (par exemple `TableUserStore`), et en son absence c'est le `NullFieldCipher` transparent qui s'applique : le chiffrement est donc strictement optionnel, et les hôtes non configurés continuent de stocker du texte en clair.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Deux points du contrat comptent. `ProtectAsync` doit renvoyer un jeton chiffré autodescriptif (par exemple le `vault:v{n}:...` de Vault Transit), et `ResolveAsync` doit laisser passer sans modification toute valeur qu'il ne reconnaît pas comme son propre texte chiffré. C'est cette règle de transparence qui permet de déployer le chiffrement progressivement sur les lignes existantes : la lecture d'une ligne non migrée renvoie l'ancien texte en clair, et l'écriture suivante la protège à nouveau.

## Recherche par index aveugle : IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` permet de continuer à rechercher dans les champs chiffrés. Il transforme une valeur en clair normalisée en un jeton d'index aveugle déterministe et utilisable comme clé de table, généralement un HMAC à clé dont la clé est conservée hors de la base de données. Le déterminisme permet à une recherche par égalité de fonctionner encore (« email = x » devient « token = HMAC(x) »), tandis qu'un dump de la base de données ne permet ni de recalculer ni d'inverser un jeton. La recherche par préfixe s'ajoute par-dessus, en tokenisant séparément chaque préfixe d'une valeur, puisqu'un HMAC à clé détruit l'ordre et les parcours par plage.

> **Ce qu'un dump révèle encore.** « Ni recalculer ni inverser » vaut pour un jeton isolé, pas pour
> l'index dans son ensemble. Trois résidus subsistent, et il vaut mieux les connaître avant de s'y fier :
>
>   *(Corrigé.)* ~~**Structure.** L'index des préfixes écrit une ligne par préfixe : le nombre de lignes
>   d'un enregistrement est donc égal à la longueur du champ indexé.~~ Chaque valeur indexée écrit désormais un nombre fixe de lignes,
>   complété par des leurres qu'aucune requête ne peut produire et qu'un dump ne peut distinguer des vrais préfixes.
> - **Égalité et fréquence.** Les jetons sont déterministes par construction, c'est ce qui fait fonctionner
>   la recherche : un dump montre donc quels enregistrements partagent une valeur et à quel point chaque valeur est courante. L'index des domaines
>   répartit votre population par employeur, ce qui identifie souvent des personnes sans retrouver d'adresse.
> - **Texte en clair choisi.** Un attaquant capable à la fois de lire le store *et* de faire indexer des valeurs
>   (en créant un compte, en se faisant provisionner via SCIM) peut soumettre une valeur candidate et rechercher son jeton.
>   Cela permet de retrouver toute valeur devinable (domaines courants, prénoms courants), quel que soit l'endroit où est conservée la clé,
>   car l'oracle est le chemin d'écriture et non le chiffrement.
>
> La tokenisation protège contre le cas pour lequel elle a été conçue : quelqu'un qui détient un dump et rien d'autre,
> et qui tente de lire des adresses. Les deux résidus restants sont exactement ce qu'un oracle d'inscription
> livre de toute façon. S'ils sont inacceptables, laissez les tables d'index des préfixes et des domaines non configurées
> (la recherche par correspondance exacte ne porte ni l'un ni l'autre) plutôt que de supposer que le HMAC les couvre.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Comme `IFieldCipher`, il s'agit d'un paramètre de constructeur de store facultatif avec une implémentation transparente par défaut (`NullIndexTokenizer`) : les lignes d'index restent donc indexées sur le texte en clair tant que vous ne l'activez pas. Les jetons renvoyés doivent être utilisables comme valeurs PartitionKey/RowKey d'Azure Table (aucun des caractères `/ \ # ?` ni aucun caractère de contrôle).

## Capture du journal des modifications : IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (renommé depuis `ITombstoneWriter` en 0.6.0) enregistre la clé de chaque ligne modifiée dans une table dédiée de journal des modifications, afin que les sauvegardes incrémentielles trouvent ce qui a changé sans parcourir la colonne `Timestamp`, non indexée, des tables actives. Les suppressions sont capturées pour toutes les tables (un parcours des lignes actives ne peut pas voir une ligne disparue) ; les upserts sont capturés pour les tables que la sauvegarde lit dans le journal au lieu de les parcourir. Implémentations intégrées : `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) et `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrat d'ordre pour les implémenteurs et les appelants : écrivez le marqueur de suppression AVANT de supprimer la ligne de données. Un plantage dans l'ordre inverse fait disparaître la suppression de toutes les sauvegardes futures, car les suppressions sont la seule catégorie de mutation qu'un nouveau parcours ne peut pas réparer d'elle-même. Le plantage inverse est sans danger : une écriture ultérieure sur la clé appose un horodatage plus récent, et la fusion comme la restauration conservent les lignes écrites après le marqueur de suppression.

## Endpoints personnalisés {#custom-endpoints}

Ajoutez vos propres endpoints à côté de ceux d'Authagonal :

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Intégration de HashiCorp Vault Transit {#hashicorp-vault-transit-integration}

> **La signature des JWT n'est pas déléguée à Vault.** Cette section montrait auparavant un extrait d'injection de dépendances qui semblait
> l'activer. Enregistrer `VaultTransitCryptoProvider` n'a **aucun effet sur la signature des jetons** :
> `ProtocolKeyManager` appelle `ProtocolSigningKeyOps.BuildSigningCredentials`, qui construit une
> `ECDsaSecurityKey` à partir du matériel de `ISigningKeyStore`, et rien ne lui substitue une
> `VaultTransitSecurityKey`. Un hôte qui suivait l'ancien extrait voyait des jetons ES256 se vérifier avec le JWKS
> et en concluait raisonnablement que Vault les signait, alors que la clé privée était générée localement au premier démarrage
> et enregistrée dans le store de données principal, en clair sauf si un `IFieldCipher` se trouvait enregistré.
> Un accès en lecture à ce store équivaut à une usurpation complète de l'émetteur. Si vous êtes soumis à une exigence de conformité selon laquelle
> les clés de signature ne quittent jamais un HSM, cela ne la satisfait pas.
>
> Le serveur journalise désormais une erreur au démarrage s'il trouve `VaultTransitCryptoProvider` enregistré, afin que
> cette idée fausse ne puisse pas persister sans qu'on le remarque.
>
> Rendre cela réel demande plus qu'un enregistrement dans l'injection de dépendances : `ISigningKeyStore` devrait pouvoir représenter une clé sans
> matériel local (un *nom* de clé Transit plutôt qu'un scalaire privé), `BuildSigningCredentials` aurait besoin d'un
> point d'extension pour renvoyer une `VaultTransitSecurityKey`, `BuildJwksAsync` devrait publier la clé publique relue
> depuis Vault, et la rotation comme la publication anticipée devraient créer et promouvoir des versions de clés Transit au lieu de
> les générer localement. `VaultTransitClient`, `VaultTransitSecurityKey`, `VaultTransitSignatureProvider` et
> `VaultTransitCryptoProvider` sont conservés parce que ce sont les éléments qui fonctionnent ; c'est le câblage qui manque.

Ce à quoi `VaultTransitClient` **sert** aujourd'hui, ce sont les points d'extension de chiffrement et de HMAC : un
`IFieldCipher` adossé à Vault pour les données personnelles au repos, ou un `IIndexTokenizer` pour les index aveugles à clé :

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

Enregistrer un `IFieldCipher` est aussi ce qui fait taire `PlaintextSigningKeyWarning`, car les stores de clés de signature
font passer leur matériel de clé par ce même point d'extension, ce qui est aujourd'hui ce qui se rapproche le plus de l'affirmation
d'origine : la clé privée existe toujours localement, mais pas en clair.

`VaultTransitClient` fournit les opérations suivantes :

| Méthode | Description |
|---|---|
| `SignAsync(keyName, data)` | Signe des données avec une clé Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Vérifie une signature au format JWS via l'endpoint de vérification Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Chiffrement symétrique sous une clé `aes256-gcm96` ; renvoie des jetons `vault:v{n}:...` à stocker tels quels |
| `HmacAsync` / `HmacBatchAsync` | HMAC à clé sous une clé `hmac` (jetons d'index aveugle) |
| `CreateKeyAsync(keyName, type)` | Crée une nouvelle clé Transit (par défaut : `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garantit de façon idempotente qu'une clé existe avec le type souhaité (la recrée si le type diffère ; le type d'une clé Transit ne peut pas être modifié sur place) |
| `RotateKeyAsync(keyName)` | Fait passer une clé à une nouvelle version |
| `DeleteKeyAsync(keyName)` | Supprime une clé (active d'abord `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Lit les métadonnées, les versions et les clés publiques d'une clé |
| `KeyExistsAsync(keyName)` | Vérifie si une clé existe |

`VaultTransitCryptoProvider` s'intègre au `JsonWebTokenHandler` de .NET afin que la signature des JWT utilise Vault de manière transparente. `VaultTransitSecurityKey` et `VaultTransitSignatureProvider` gèrent l'intégration de bas niveau.

## E-mail {#email}

L'expéditeur Resend intégré s'active automatiquement lorsque `Email:ResendApiKey` est configuré (définissez aussi `Email:SenderEmail`). Sans aucun `IEmailService`, les e-mails sont abandonnés via `NullEmailService`, et comme le contrôle de connexion exigeant une adresse e-mail confirmée est activé par défaut, les utilisateurs inscrits par eux-mêmes ne pourraient jamais se connecter ; `UseAuthagonal()` journalise un avertissement bien visible au démarrage dans cette situation.

Pour utiliser un autre fournisseur, enregistrez votre propre `IEmailService` avant `AddAuthagonal()` :

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` déclare aussi `SendAccountExistsEmailAsync` (envoyé lorsque quelqu'un tente de s'inscrire avec une adresse e-mail déjà enregistrée, ce qui garde la réponse d'inscription neutre face à l'énumération des comptes). Cette méthode a une implémentation par défaut sans effet, de sorte que les implémentations existantes continuent de compiler.

## Voir aussi {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) : exemple complet et fonctionnel
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app) : exemple d'application cliente
