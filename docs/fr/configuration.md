---
layout: default
title: Configuration
locale: fr
---

# Configuration

Authagonal se configure via `appsettings.json` ou des variables d'environnement. Les variables d'environnement utilisent `__` comme séparateur de section (par exemple `Storage__ConnectionString`).

## Paramètres requis {#required-settings}

Le stockage peut être configuré de deux façons : fournissez **soit** `Storage:ConnectionString`, **soit** `Storage:TableServiceUri` (la voie par identité managée, à privilégier en production).

| Paramètre | Variable d'environnement | Description |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Chaîne de connexion Azure Table Storage avec une clé de compte. Adaptée au développement / à Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint Table Storage par identité managée, par exemple `https://{account}.table.core.windows.net/`. Alternative à `Storage:ConnectionString`, **à privilégier en production** : l'authentification passe par `DefaultAzureCredential`, de sorte qu'aucune clé d'accès ne se retrouve jamais dans un secret. L'hôte doit accorder à l'identité de la charge de travail le rôle **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | L'URL de base publique de ce serveur (par exemple `https://auth.example.com`) |

## Stockage {#storage}

| Paramètre | Variable d'environnement | Par défaut | Description |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(aucun)* | Chaîne de connexion avec clé de compte (voir Paramètres requis). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(aucun)* | URI Table Storage par identité managée (voir Paramètres requis). Prioritaire sur `Storage:ConnectionString` lorsque les deux sont définis. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Indique s'il faut maintenir les tables d'index de recherche par préfixe `UserFirstNames` / `UserLastNames` qui soutiennent la recherche par préfixe de nom dans l'administration. Définissez `false` sur les hôtes qui n'exposent pas la recherche par nom dans l'administration, pour éviter ces écritures. **Remarque sur la montée en charge :** ces index utilisent une seule partition très sollicitée et plafonnent le débit à environ 2 000 opérations par seconde à grande échelle ; désactivez-les si vous n'avez pas besoin de la recherche par nom. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL de base vers laquelle l'endpoint `/connect/authorize` redirige pour la SPA de connexion (écrans de connexion, de renforcement de l'authentification et de consentement). Définissez-la lorsque l'interface de connexion est servie depuis une autre origine que le serveur ; par défaut, il s'agit du chemin relatif `/login` servi par la SPA fournie. |

## Authentification {#authentication}

| Paramètre | Par défaut | Description |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Durée de vie de la session par cookie (glissante) |
| `Authentication:AllowInsecureCookie` | `false` | Permet d'envoyer le cookie de session en http simple (`SameAsRequest` au lieu de `Always`). **Développement uniquement.** Le cookie EST la session, et `SameAsRequest` ne paraît équivalent que derrière un proxy qui termine le TLS : il dépend de l'arrivée d'un `X-Forwarded-Proto` jugé fiable, si bien qu'une ingress mal configurée, une sonde de santé en HTTP simple ou un proxy qui supprime l'en-tête produit un cookie non Secure, qui accompagne ensuite toute requête en clair vers le même hôte. L'échec est silencieux. |
| `Authentication:CookieDomain` | *(non défini)* | Rattache le cookie de session à un domaine parent, afin qu'il soit envoyé aux sous-domaines voisins (`app.example.com` aussi bien que `auth.example.com`). **Cela coûte le rattachement à l'origine :** le cookie ne peut plus porter le préfixe `__Host-`, qui est ce qui pousse le navigateur à le refuser s'il n'est pas Secure, avec `Path=/` et sans `Domain` ; tout sous-domaine capable de définir des cookies sur le parent, et quiconque peut en prendre le contrôle, entre donc dans le périmètre. Laissez ce paramètre non défini, sauf si une origine voisine a réellement besoin de la session. |
| `Auth:AllowInsecureHttp` | `false` | Permet aux endpoints OAuth (`/connect/*`) de répondre aux requêtes en http simple. **Développement uniquement.** La RFC 6749 §3.1/§3.2 exige le TLS sur les endpoints d'autorisation et de jeton : par défaut, une requête non https vers l'un d'eux est donc refusée avec `invalid_request`. Le schéma est évalué *après* le traitement des en-têtes transférés : un proxy qui termine le TLS et transfère `X-Forwarded-Proto: https` franchit donc ce contrôle sans ce paramètre, à condition que ce proxy soit déclaré dans [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms), faute de quoi l'en-tête est ignoré. Seul un déploiement réellement en clair (le `docker-compose.yml` fourni, la démo custom-server) en a besoin, et le serveur journalise un avertissement au démarrage chaque fois qu'il est activé. Propagé vers `AuthagonalProtocolOptions.AllowInsecureHttp`, il régit donc aussi les endpoints qui appartiennent à `Authagonal.Protocol` (voir [Extensibilité](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Refuse de démarrer lorsque le framework partagé .NET est antérieur au seuil de sécurité exigé par Authagonal (**9.0.18 / 10.0.10**). Ce seuil existe parce que les correctifs de GHSA-37gx-xxp4-5rgx et GHSA-w3x6-4m5h-cxqf (une boucle infinie et une paire XXE / épuisement de ressources dans `System.Security.Cryptography.Xml`, toutes deux atteignables depuis l'endpoint ACS SAML **anonyme**) sont livrés dans le runtime, et non dans un package que cette bibliothèque pourrait épingler : aucune de vos dépendances ne peut donc les garantir. À `false`, un runtime ancien produit un journal `Critical` et le serveur démarre : refuser par défaut transformerait une montée de version d'Authagonal en panne sur un parc dont le runtime a un correctif de retard. Définissez-le à `true` là où ne pas démarrer est préférable à servir du XML non authentifié sur un runtime non corrigé. |
| `Auth:MaxFailedAttempts` | `5` | Nombre de tentatives de connexion échouées avant le verrouillage du compte |
| `Auth:LockoutDurationMinutes` | `10` | Durée du verrouillage du compte une fois le maximum de tentatives échouées atteint |
| `Auth:MaxLoginAttemptsPerIp` | `30` | Tentatives de mot de passe autorisées par adresse source sur `Auth:LoginWindowMinutes` et, séparément, par adresse e-mail soumise sur la même fenêtre. Le verrouillage par compte ne peut pas borner une attaque par pulvérisation (une tentative contre chacun de milliers de comptes), et chaque tentative non authentifiée coûte un PBKDF2 complet : ce paramètre borne donc les deux. Son dépassement donne `429 too_many_attempts` (`AuthEndpoints.cs:107-119`). |
| `Auth:LoginWindowMinutes` | `5` | Fenêtre de `Auth:MaxLoginAttemptsPerIp` |
| `Auth:MaxRegistrationsPerIp` | `5` | Nombre maximal d'inscriptions par adresse IP dans la fenêtre |
| `Auth:RegistrationWindowMinutes` | `60` | Fenêtre de limitation du débit des inscriptions |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Nombre maximal d'e-mails de réinitialisation du mot de passe par adresse cible dans la fenêtre (indexé sur l'adresse e-mail, et non sur l'IP de l'appelant, afin qu'une adresse ne puisse pas être bombardée d'e-mails) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Nombre maximal de demandes de mot de passe oublié par IP source dans la fenêtre. Le plafond par adresse e-mail borne le courrier envoyé à une victime ; celui-ci borne un appelant qui parcourt une liste d'adresses, ce qui reviendrait sinon à un envoi anonyme et illimité de courrier depuis votre domaine d'expédition vérifié, plus une lecture du store par adresse. |
| `Auth:PasswordResetWindowMinutes` | `60` | Fenêtre de limitation du débit des réinitialisations de mot de passe |
| `Auth:DurableRateLimiting` | `false` | Conserve les compteurs de limitation de débit dans le store configuré, afin que tous les réplicas partagent un même budget au lieu que chaque nœud tienne le sien. Coûte un aller-retour vers le store par contrôle ; un déploiement à un seul nœud n'y gagne rien. Exige un fournisseur qui fournit `IRateLimitCounterStore` (Azure, SQL, AWS). Sinon, l'hôte refuse de démarrer plutôt que de revenir silencieusement à des limites par nœud. Voir [Limites à l'échelle du cluster](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(vide)* | Domaines de messagerie (tableau de chaînes) dont les inscriptions en libre-service sont confirmées automatiquement et sautent l'e-mail de vérification. Vide (la valeur par défaut) signifie que chaque inscription doit être vérifiée. Destiné uniquement au développement et aux tests ; n'y listez jamais un domaine capable de recevoir du vrai courrier. |
| `Auth:AllowPasswordlessAccountClaim` | `false` | L'inscription d'une adresse e-mail qui appartient à un compte existant **sans identifiant local** (fédéré ou provisionné juste-à-temps) prépare un mot de passe sur ce compte au lieu de renvoyer la réponse de doublon neutre face à l'énumération. L'identifiant préparé et les éventuels attributs restent inactifs jusqu'à ce que le demandeur clique sur un nouvel e-mail de vérification : connaître l'adresse e-mail d'un compte fédéré ne suffit donc pas pour en prendre le contrôle. Un compte qui a déjà un mot de passe n'est jamais concerné. Voir [Mise à niveau des utilisateurs](user-upgrade). |
| `Auth:ClaimAllowedAttributeKeys` | *(vide)* | Clés d'attributs personnalisés qu'une réclamation sans mot de passe peut reporter de la requête d'inscription vers le compte réclamé. Vide autorise toutes les clés (rétrocompatibilité) ; listez des clés pour restreindre ce qu'une réclamation peut injecter dans le provisionnement en aval et dans les jetons. |
| `Auth:EmailVerificationExpiryHours` | `24` | Durée de vie du lien de vérification de l'adresse e-mail |
| `Auth:PasswordResetExpiryMinutes` | `60` | Durée de vie du lien de réinitialisation du mot de passe |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Durée de vie du jeton de défi MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Durée de vie du jeton de configuration de la MFA (pour l'enrôlement imposé) |
| `Auth:WebAuthnAllowedHosts` | *(vide)* | Hôtes autorisés à agir comme partie de confiance WebAuthn. Vide accepte n'importe quel hôte (les déploiements existants continuent de fonctionner) et constitue une faille : l'identifiant de RP et l'origine attendue sont sinon dérivés de la requête en cours de validation. Sur un déploiement multi-locataire, listez chaque hôte de locataire. Voir [MFA](mfa). |
| `Auth:Pbkdf2Iterations` | `100000` | Nombre d'itérations PBKDF2 pour le hachage des mots de passe |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Durée minimale en temps réel imposée à une connexion échouée avant le renvoi de `invalid_credentials`, mesurée depuis le début de la requête. Ferme l'oracle temporel d'énumération des utilisateurs : un compte inexistant est vérifié contre un hachage factice au format PBKDF2 natif, mais un compte réel peut encore détenir un hachage importé bcrypt, Scrypt.NET ou ASP.NET Identity V3 d'un coût différent ; un travail égal est donc impossible, et c'est un temps écoulé égal qui est imposé. Augmentez-la au-delà du hachage le plus lent que contient le déploiement, par exemple si vous avez importé du bcrypt d'un coût supérieur à 11, un hachage Scrypt.NET `$s2$` avec un `N` élevé, ou si vous avez porté `Pbkdf2Iterations` bien au-delà de la valeur par défaut. Un unique avertissement est journalisé la première fois qu'une connexion échouée la dépasse. `0` désactive ce délai et rouvre l'oracle. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Fenêtre de tolérance optionnelle (en secondes) pour la réutilisation concurrente d'un jeton de rafraîchissement. `0` (par défaut) conserve la posture stricte : toute réutilisation d'un jeton de rafraîchissement déjà consommé révoque tous les jetons de ce couple utilisateur + client. Définissez `> 0` pour traiter une réutilisation dans la fenêtre comme une nouvelle tentative idempotente (les jetons successeurs sont renvoyés), ce qui est utile pour les clients mobiles dont la connectivité est instable. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Active l'endpoint d'enregistrement dynamique de clients `POST /connect/register` (RFC 7591). Désactivé par défaut, car un enregistrement ouvert peut être détourné dans les déploiements multi-locataires. Voir [Enregistrement dynamique de clients](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(vide)* | Scopes qu'un demandeur anonyme peut s'attribuer, en plus des scopes OIDC intégrés toujours enregistrables (`openid`, `profile`, `email`, `phone`, `offline_access`). Vide signifie les scopes intégrés et rien d'autre : l'existence d'un scope dans le store ne vaut pas permission, pour un client auto-enregistré, de le déclarer. Les scopes soumis à des rôles ne sont jamais enregistrables. Voir [Enregistrement dynamique de clients](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Durée de vie d'une clé de signature avant sa rotation automatique (les clés sont en ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Fréquence de rechargement des clés de signature depuis le stockage |
| `Auth:KeyRotationEnabled` | `false` | Active la rotation automatique des clés de signature |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Fréquence à laquelle vérifier si la clé active doit être renouvelée |
| `Auth:KeyRotationLeadTimeDays` | `14` | Effectue la rotation lorsque la clé active expire dans ce nombre de jours |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalle entre deux vérifications du tampon de sécurité du cookie |
| `Auth:AllowedInternalTargets` | *(vide)* | Destinations internes depuis lesquelles Authagonal peut récupérer des données sur les chemins dont **vous** avez fourni l'URL : métadonnées SAML amont, découverte OIDC amont, callbacks de provisionnement. Vide signifie que toute adresse interne est refusée. Voir [Récupérations sortantes](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Fait passer ces mêmes récupérations configurées par l'opérateur par le proxy HTTP ambiant, en acceptant que le contrôle d'adresse ne puisse pas voir au-delà. Ne s'applique jamais à un `jwks_uri` ou à une URI de déconnexion back-channel enregistrés par un client. Voir [Récupérations sortantes](#outbound-fetches-ssrf-guard). |
| `Auth:AtRestBackfillEnabled` | `false` | Exécute une fois au démarrage, sur le leader du cluster, le rattrapage du chiffrement au repos. Il réécrit chaque ligne d'utilisateur existante et ses lignes d'index dérivées du profil selon le schéma de stockage au repos actuel : c'est la voie de migration pour activer `IFieldCipher` / `IIndexTokenizer` sur un déploiement qui contient déjà des données (voir [Extensibilité](extensibility#pii-field-encryption-ifieldcipher)). Enregistrer un chiffreur seul ne chiffre que les lignes écrites ensuite. Il représente un volume d'écriture réel, il est idempotent et s'exécute une fois par processus : désactivez-le dès que le journal signale une exécution complète. |
| `Auth:MaxScimGroupsPerClient` | `5000` | Nombre maximal de groupes SCIM qu'un client de provisionnement peut posséder ; au-delà, la création est refusée. Le stockage des groupes n'est pas indexé : une table non bornée ferait payer son coût à chaque émission de jeton. |
| `Auth:MaxScimGroupMembers` | `10000` | Nombre maximal de membres d'un groupe SCIM ; au-delà, la création, le remplacement et la modification partielle sont refusés. |

## Protection des données {#data-protection}

Les clés ASP.NET Core Data Protection (qui chiffrent le cookie de session) doivent être partagées entre les instances ; voir [Montée en charge](scaling#cookie-encryption-data-protection). Options de persistance, par ordre de priorité :

| Paramètre | Par défaut | Description |
|---|---|---|
| `DataProtection:BlobUri` | *(aucun)* | URI Azure Blob explicite pour le trousseau de clés (par exemple `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). L'authentification passe par `DefaultAzureCredential` : c'est la voie à privilégier en production, avec `Storage:TableServiceUri`. |
| *(repli)* | *(aucun)* | Lorsque `DataProtection:BlobUri` n'est pas défini, le trousseau est persisté automatiquement : dans un conteneur `dataprotection` du compte désigné par `Storage:ConnectionString` (sauf s'il s'agit d'Azurite) ou, sur la voie par identité managée, vers l'endpoint blob dérivé de `Storage:TableServiceUri` (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), ce qui exige le rôle Storage Blob Data Contributor sur le même compte. Seul un endpoint de table non reconnu (Azurite, émulateurs à chemin) se rabat sur le stockage de fichiers propre à chaque machine, qui est éphémère et propre à chaque pod ; `KeyRingStartupCheck` journalise alors une erreur Critical. |

Sur le backend AWS, passez un client S3 + un bucket à `AddAuthagonalAwsStorage` pour persister le trousseau dans S3 ; voir [Installation → backend AWS](installation#aws-backend). Sur le backend SQL, le trousseau est persisté par `AddAuthagonalPostgres` / `AddAuthagonalSqlite` ; voir [Installation → backend SQL](installation#sql-backend).

Persister n'est pas chiffrer. Quel que soit le backend qui détient le trousseau, il est écrit en XML en clair (clé maîtresse comprise), sauf si l'un des paramètres suivants est défini. Ce trousseau protège le cookie d'authentification : une lecture du store permet donc de forger une session pour n'importe quel utilisateur.

| Paramètre | Par défaut | Description |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(aucun)* | URI de clé Azure Key Vault utilisée pour envelopper le trousseau. L'authentification passe par `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(aucun)* | Empreinte d'un certificat du magasin de la machine utilisé pour envelopper le trousseau. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Accepte délibérément un trousseau en clair. Rappelé au niveau `Critical` à chaque démarrage, afin d'apparaître lors d'un audit et pas seulement dans un fichier de configuration. |

Le démarrage applique cette règle à partir des options du trousseau *résolues* : elle s'applique donc de manière identique aux dépôts Azure, AWS, SQL et à tout dépôt enregistré par l'hôte. Un déploiement qui persiste le trousseau sans chiffrement et **sans encore aucune clé** est refusé, de sorte que l'état non sécurisé n'est jamais créé ; un déploiement dont le trousseau **a déjà des clés** démarre et journalise au niveau `Critical`, car un refus ferait tomber un déploiement en cours d'exécution lors d'une montée de version. En développement, aucun refus n'a lieu.

## Cache et délais {#cache-and-timeouts}

| Paramètre | Par défaut | Description |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Durée de mise en cache des origines CORS autorisées |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Durée de mise en cache du document de découverte OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Durée de mise en cache des métadonnées de l'IdP SAML |
| `Cache:OidcStateLifetimeMinutes` | `10` | Durée de vie du paramètre state de l'autorisation OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Durée de vie de l'ID d'AuthnRequest SAML (prévention du rejeu) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Délai d'expiration du contrôle de santé de Table Storage |
| `Cache:HealthCheckCacheSeconds` | `5` | Durée pendant laquelle la réponse de `/health` est réutilisée avant une nouvelle interrogation du stockage (correspond au `Cache-Control: max-age` annoncé par l'endpoint). `0` interroge à chaque requête, ce qui rouvre l'amplification anonyme que le cache referme. |

## Services d'arrière-plan {#background-services}

| Paramètre | Par défaut | Description |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Délai initial avant le premier nettoyage des jetons expirés |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalle de nettoyage des jetons expirés |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Délai initial avant la première réconciliation des octrois |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalle de réconciliation des octrois |

### Purges des éléments expirés (Azure Table) {#expiry-sweeps-azure-table}

Azure Table Storage n'a pas de TTL : sur le backend Azure, le serveur exécute donc un `TableExpirySweepService` par table (toutes les 15 minutes, uniquement sur le leader du cluster) sur `MfaChallenges`, `RevokedTokens` et `UpstreamRefreshTokens`, et supprime les lignes dont l'expiration est passée. Il ne s'agit que de rétention : chacune de ces lignes est déjà refusée à la lecture par son propre contrôle d'expiration. Une ligne sans expiration déclarée (possible dans `UpstreamRefreshTokens`) n'est délibérément jamais purgée. DynamoDB et SQL nettoient nativement ces trois mêmes tables. Rien à configurer.

## Protection contre les bots (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

Optionnelle. Lorsqu'une clé secrète est définie, la connexion, l'inscription, le mot de passe oublié et la réinitialisation du mot de passe vérifient un `turnstileToken` auprès de Cloudflare avant tout traitement ; sans clé secrète, rien ne change et aucun widget n'est affiché.

| Paramètre | Par défaut | Description |
|---|---|---|
| `Turnstile:SiteKey` | *(non défini)* | Sitekey publique, transmise à l'interface de connexion (`turnstileSiteKey` sur `GET /api/auth/providers`) pour qu'elle puisse afficher le widget |
| `Turnstile:SecretKey` | *(non défini)* | Secret pour la vérification côté serveur. Non défini ou vide, Turnstile est entièrement désactivé |

Un hôte qui sert des domaines fournis par ses clients ne peut pas utiliser une seule paire de clés (Cloudflare plafonne le nombre de noms d'hôte d'un widget) ; il remplace [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider). Voir [API d'authentification](auth-api#providers) pour l'erreur `captcha_failed`.

## Rôles {#roles}

Les rôles sont définis dans le tableau `Roles` et initialisés au démarrage, avec les clients, les scopes et
les fournisseurs. Leur initialisation compte surtout lorsqu'un scope est soumis à
[`AllowedRoles`](scopes#role-gated-scopes) : un scope soumis à un rôle que rien ne crée est fermé
à tout le monde, y compris à l'opérateur qui l'a configuré, et l'échec est silencieux : le scope n'est
tout simplement jamais accordé.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Champ | Description |
|---|---|
| `Name` | Le nom du rôle, tel qu'utilisé dans `Scope.AllowedRoles` et dans la revendication `roles` du jeton |
| `Description` | Lisible par un humain ; mise à jour lors des démarrages suivants lorsque l'initialisation en indique une |
| `Members` | Adresses e-mail placées dans le rôle à chaque démarrage. Une adresse sans utilisateur correspondant est ignorée avec un avertissement et retentée au démarrage suivant, de sorte que le démarrage ne dépend jamais d'un compte que personne n'a encore créé |

L'initialisation est **additive et idempotente**. Elle ne supprime jamais un rôle et ne révoque jamais une appartenance : la configuration
n'est pas la source de vérité sur qui détient quoi, de sorte qu'un rôle accordé via l'API d'administration survit au
redémarrage suivant.

## Clients {#clients}

Les clients sont définis dans le tableau `Clients` et initialisés au démarrage. Chaque client peut avoir :

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "SecretHashes": ["pbkdf2-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "Audiences": ["https://api.example.com"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "RestrictedToOrganizationIds": [],
      "InitiateLoginUri": "https://app.example.com/login",
      "ClientUri": "https://app.example.com",
      "IsDefaultApplication": false
    }
  ]
}
```

L'initialisation procède par **lecture-fusion-écriture** : un champ que l'initialisation n'indique pas conserve la valeur stockée, de sorte qu'un redémarrage n'annule jamais une modification faite via l'API d'administration (un client désactivé reste désactivé, un secret renouvelé survit, `Audiences` et le JWKS du client sont préservés). Un champ que l'initialisation indique est écrasé à chaque démarrage.

Remarques sur les champs (d'après `ClientSeedService.ClientSeedConfig`) :

- **Alias.** `ClientId`/`Id`, `ClientName`/`Name`, `AllowedGrantTypes`/`GrantTypes`, `AllowedScopes`/`Scopes`, `AllowedCorsOrigins`/`CorsOrigins` et `RequireClientSecret`/`RequireSecret` sont interchangeables. Un objet `SeedClient` unique est également lu comme une entrée supplémentaire.
- **Secrets.** Indiquez soit `SecretHashes` (déjà hachés), soit `ClientSecret` (en clair, haché au démarrage, utilisé uniquement en l'absence de hachages). L'initialisation n'est appliquée que lorsqu'elle en fournit un : un secret renouvelé via l'API d'administration survit donc au redémarrage suivant. Il n'existe pas de clé `ClientSecretHashes` dans le format d'initialisation.
- **`BackChannelLogoutUri`** : l'adresse à laquelle les jetons de déconnexion back-channel sont envoyés par POST ; voir [Déconnexion back-channel](#back-channel-logout).
- **`RestrictedToOrganizationIds`** : les identifiants des organisations avec lesquelles le client peut être utilisé. Vide signifie sans restriction ; une entrée unique sélectionne aussi cette organisation pour une requête qui n'en désigne aucune (voir [Organisations](organizations)).
- **`InitiateLoginUri`, `ClientUri`, `IsDefaultApplication`** : alimentent la liste `/api/auth/apps` et le bouton « continuer vers l'application » de l'écran de connexion.
- **Non initialisables.** `RequireConsent`, `ProvisioningApps`, `RequirePushedAuthorizationRequests`, le JWKS du client et les champs de déconnexion front-channel n'ont pas de clé dans le format d'initialisation : la configuration ne peut donc pas les définir. Les clés inconnues sont ignorées sans avertissement.
- Une initialisation dont les scopes ou les audiences enfreignent les règles des scopes réservés ou des audiences est refusée avec un journal d'erreur et ignorée.

### Audiences et indicateurs de ressource (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` est la liste d'autorisation du client pour le paramètre `resource` (RFC 8707) et pour le paramètre `audience` d'un échange de jetons (RFC 8693). Ce qui franchit ce contrôle devient la revendication `aud` du jeton d'accès émis ; en l'absence de `resource` dans la requête, `aud` se rabat sur `Audiences`, et en l'absence des deux, c'est le `client_id`.

Une liste `Audiences` vide signifie **« aucune »** pour tout client qui a réellement répondu à la question : un client dont la requête de création portait le champ `audiences`, que ce soit par enregistrement dynamique (où ce champ est une extension Authagonal de la RFC 7591), par l'API d'administration ou par la configuration d'initialisation. Un tel client ne peut désigner aucune `resource`, sur aucun chemin : l'autorisation, `client_credentials` et l'échange de jetons concordent.

Un enregistrement dynamique qui **omet** `audiences` (tout client RFC 7591 standard, c'est-à-dire tout client MCP) ne s'est jamais vu poser la question. Sa liste est « non définie », et il peut désigner n'importe quelle URI absolue comme `resource` ; la spécification d'autorisation MCP en dépend. La même lecture s'applique aux clients stockés avant l'apparition de `AudiencesDeclared`, car durcir tous les clients stockés lors d'une mise à niveau casserait des flux qui fonctionnent aujourd'hui.

| Client | `Audiences` vide signifie |
|---|---|
| La requête de création portait `audiences` (champ d'extension DCR, API d'administration, initialisation) | **refus** : aucune `resource` ne peut être désignée |
| Enregistrement DCR qui a omis `audiences` | **« non définie »** : toute URI absolue est acceptée comme `resource` |
| Stocké avant l'apparition de `AudiencesDeclared` | **« non définie »** : toute URI absolue est acceptée comme `resource` |

**Mettre à jour un ancien client** se fait par un `PUT` sur l'API d'administration des clients avec `audiencesDeclared: true` (et les `audiences` auxquelles il doit être limité). Cet indicateur ne fait que durcir : une mise à jour peut le définir mais pas l'effacer, de sorte qu'une modification sans rapport ne ramènera jamais silencieusement un client à la lecture permissive.

La conséquence pour les anciennes lignes mérite d'être énoncée clairement plutôt qu'enfouie :

> Un client préexistant sans `Audiences` configurées peut désigner **n'importe quelle** URI absolue comme `resource` sur l'endpoint d'autorisation ou avec `client_credentials`, et recevoir un jeton d'accès dont le `aud` est cette valeur, signé par la clé de ce locataire, portant le `sub` de l'utilisateur à l'origine de la requête et tous les scopes autorisés pour ce client.

Une liste `audiences` déclarée est validée au moment de son écriture : au plus 20 entrées d'au plus 512 caractères, chacune étant une URI absolue avec un schéma explicite et sans fragment. Les valeurs de `resource` sont soumises à la même forme ; notez qu'un simple chemin comme `/admin` n'est **pas** accepté, même si l'analyseur `Uri` de .NET le considère sous Linux comme une URI `file:` absolue.

Désigner une ressource ne donne pas accès à celle-ci. Mais cela signifie que le serveur d'autorisation ne peut pas être le seul rempart entre un client et une API qu'il n'était pas censé appeler. Par conséquent :
- **Les serveurs de ressources DOIVENT fonder l'autorisation sur `scope`** (ou sur leur propre modèle), et non sur `iss` + `aud` + `sub` seuls. Un jeton qui désigne votre API dans `aud` prouve que le client a demandé votre API. Il ne prouve pas que le client a le droit de l'appeler, et ce serveur ne peut pas le lui faire prouver.
- **Les serveurs de ressources DOIVENT valider `aud` par rapport à leur propre identifiant**, et pas seulement vérifier « qu'une valeur est présente ».
- **Définissez `Audiences` sur chaque client qui doit être limité à un ensemble fixe d'API.** Une fois ce paramètre configuré, une `resource` non listée est refusée avec `invalid_target` sur l'endpoint d'autorisation et avec `client_credentials`. C'est le seul endroit où la restriction peut être appliquée.
- **Ajoutez `audiencesDeclared: true` aux clients créés avant son apparition**, afin que leur liste d'audiences vide signifie « aucune » plutôt que « n'importe laquelle ».
- **Un client auto-enregistré peut déclarer `audiences`** lors de son enregistrement, et il est tenu à ce qu'il déclare, y compris à une liste vide. `Auth:DynamicClientRegistrationEnabled` reste désactivé par défaut ; voir [Enregistrement dynamique de clients](client-registration).

### Types d'octroi {#grant-types}

| Type d'octroi | Cas d'usage |
|---|---|
| `authorization_code` | Connexion interactive d'un utilisateur (applications web, SPA, applications mobiles) |
| `client_credentials` | Communication de service à service |
| `refresh_token` | Renouvellement des jetons (exige `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Octroi d'autorisation d'appareil (RFC 8628) pour les appareils aux capacités de saisie limitées |

### Utilisation des jetons de rafraîchissement {#refresh-token-usage}

| Valeur | Comportement |
|---|---|
| `OneTime` (par défaut) | Chaque rafraîchissement émet un nouveau jeton de rafraîchissement et invalide l'ancien. Par défaut (`Auth:RefreshTokenReuseGraceSeconds = 0`), toute réutilisation d'un jeton consommé révoque immédiatement tous les jetons de ce couple utilisateur + client : **aucune** fenêtre de tolérance n'est active par défaut. Donnez à `Auth:RefreshTokenReuseGraceSeconds` une valeur positive pour activer une fenêtre de tolérance aux nouvelles tentatives. |
| `ReUse` | Le même jeton de rafraîchissement est réutilisé jusqu'à son expiration. |

### Applications de provisionnement {#provisioning-apps}

Le tableau `ProvisioningApps` d'un client (lu au moment de l'autorisation, `AuthorizeEndpoint.cs:578` ; l'initialisation par la configuration ne le lie pas et les routes client de l'API d'administration ne le transportent pas : c'est donc l'hôte qui le définit sur l'enregistrement de client stocké) référence des identifiants d'applications définis dans la section de configuration `ProvisioningApps`. Lorsqu'un utilisateur donne son autorisation via ce client, il est provisionné dans ces applications via TCC. Voir [Provisionnement](provisioning) pour plus de détails.

## Scopes {#scopes}

Des [scopes OAuth](scopes) personnalisés peuvent être initialisés à partir du tableau `Scopes`. Chaque entrée fait l'objet d'un upsert par `Name` au démarrage (une entrée sans `Name` est ignorée avec un avertissement) :

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

Un champ que vous définissez l'emporte sur la valeur stockée à chaque démarrage ; un champ que vous omettez conserve la valeur stockée. La configuration peut donc ajouter ou modifier `UserClaims` et `AllowedRoles`, mais pas les vider (utilisez `PUT /api/v1/scopes/{name}` pour cela). La signification des champs est décrite dans [Modèle de scope](scopes#scope-model).

## Applications de provisionnement {#provisioning-apps-1}

Définissez les applications en aval dans lesquelles les utilisateurs doivent être provisionnés :

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Voir [Provisionnement](provisioning) pour la spécification complète du protocole TCC.

## Politique MFA {#mfa-policy}

L'authentification multifacteur est imposée client par client via la propriété `MfaPolicy` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par défaut) | Aucun défi MFA, même si l'utilisateur est enrôlé dans la MFA |
| `Enabled` | Soumet à un défi les utilisateurs enrôlés dans la MFA ; n'impose pas l'enrôlement |
| `Required` | Soumet à un défi les utilisateurs enrôlés ; impose l'enrôlement aux utilisateurs sans MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Lorsque `MfaPolicy` vaut `Required` et que l'utilisateur ne s'est pas enrôlé dans la MFA, la connexion renvoie `{ mfaSetupRequired: true, setupToken: "..." }`. Le jeton de configuration authentifie l'utilisateur auprès des endpoints de configuration de la MFA (via l'en-tête `X-MFA-Setup-Token`) afin qu'il puisse s'enrôler avant d'obtenir une session par cookie.

Les connexions fédérées (SAML/OIDC) respectent aussi la politique MFA : un utilisateur enrôlé dans la MFA passe par le défi MFA une fois que l'IdP externe l'a authentifié, et `Required` impose l'enrôlement aux utilisateurs fédérés sans MFA.

### Surcharge par IAuthHook {#iauthhook-override}

La méthode `IAuthHook.ResolveMfaPolicyAsync` peut surcharger la politique du client utilisateur par utilisateur :

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Politique de mot de passe {#password-policy}

Personnalisez les exigences de robustesse des mots de passe :

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propriété | Par défaut | Description |
|---|---|---|
| `MinLength` | `8` | Longueur minimale du mot de passe |
| `MinUniqueChars` | `2` | Nombre minimal de caractères distincts |
| `RequireUppercase` | `true` | Exige au moins une lettre majuscule |
| `RequireLowercase` | `true` | Exige au moins une lettre minuscule |
| `RequireDigit` | `true` | Exige au moins un chiffre |
| `RequireSpecialChar` | `true` | Exige au moins un caractère non alphanumérique |

La politique est appliquée lors de la réinitialisation du mot de passe et de la création d'utilisateurs par l'administration. L'interface de connexion récupère la politique active sur `GET /api/auth/password-policy` pour afficher dynamiquement les exigences.

## Fournisseurs SAML {#saml-providers}

Définissez les fournisseurs d'identité SAML dans la configuration. Ils sont initialisés au démarrage :

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propriété | Requise | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilisé dans des URL comme `/saml/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (ConnectionId par défaut) |
| `EntityId` | Oui | L'entity ID de SP de **ce serveur**, c'est-à-dire l'identifiant que vous enregistrez auprès de l'IdP, et non l'entity ID propre de l'IdP |
| `MetadataLocation` | Oui | URL du XML de métadonnées SAML de l'IdP. Doit être en https et routable publiquement, sauf si l'hôte figure dans [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard) : ce document porte les certificats par rapport auxquels chaque assertion est validée. Si votre IdP ne publie aucun endpoint de métadonnées en https, définissez plutôt `metadataXml` via l'[API d'administration](admin-api) ; l'initialisation par la configuration n'a pas de clé pour cela. |
| `AllowedDomains` | Non | Domaines de messagerie routés vers ce fournisseur via le SSO |
| `OrganizationId` | Non | Limite cette connexion à une seule [organisation](organizations). Null (par défaut) en fait une connexion au niveau du locataire ; seules les connexions au niveau du locataire enregistrent leurs `AllowedDomains` comme routes de domaine SSO |
| `JitProvisioningEnabled` | Non | Crée un utilisateur lors de sa première connexion. `false` par défaut |
| `AllowUninvitedJit` | Non | Permet au provisionnement juste-à-temps de créer un utilisateur dans une organisation à laquelle il n'a pas été invité. `false` par défaut |
| `ChallengeMfaAfterLogin` | Non | Applique le défi de la politique MFA de l'application après la connexion auprès de l'IdP. `true` par défaut |
| `ProvisioningAttributeParams` | Non | Attributs de l'assertion transmis au provisionnement en aval |
| `AllowUnsolicitedResponses` | Non | Accepte sur cette connexion les réponses initiées par l'IdP (non sollicitées). `false` par défaut |

Les booléens sont écrits à partir de l'initialisation à chaque démarrage, valeur par défaut comprise : une connexion initialisée qu'un opérateur a modifiée via l'API d'administration voit donc ces valeurs rétablies au redémarrage suivant. Les champs pour lesquels l'initialisation n'a pas de clé (`SpCertificate`, `SignAuthnRequests`, `NameIdFormat`, `MetadataXml`, `IconUrl`) sont préservés.

## Fournisseurs OIDC {#oidc-providers}

Définissez les fournisseurs d'identité OIDC dans la configuration. Ils sont initialisés au démarrage :

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propriété | Requise | Description |
|---|---|---|
| `ConnectionId` | Oui | Identifiant stable (utilisé dans des URL comme `/oidc/{connectionId}/login`) |
| `ConnectionName` | Non | Nom d'affichage (ConnectionId par défaut) |
| `MetadataLocation` | Oui | URL du document de découverte OpenID Connect de l'IdP |
| `ClientId` | Oui | Identifiant de client OAuth2 enregistré auprès de l'IdP |
| `ClientSecret` | Oui | Secret de client OAuth2 (protégé via `ISecretProvider` au démarrage) |
| `RedirectUrl` | Non | **Ignoré.** L'URI de redirection est dérivée à chaque requête sous la forme `{Issuer}/oidc/callback` : c'est *celle-ci* qu'il faut enregistrer auprès de l'IdP. Une valeur indiquée ici n'a aucun effet et est journalisée comme ignorée. |
| `AllowedDomains` | Non | Domaines de messagerie routés vers ce fournisseur via le SSO |
| `OrganizationId` | Non | Limite cette connexion à une seule [organisation](organizations) ; null signifie au niveau du locataire |
| `JitProvisioningEnabled` | Non | Crée un utilisateur lors de sa première connexion. `false` par défaut |
| `AllowUninvitedJit` | Non | Permet au provisionnement juste-à-temps de créer un utilisateur dans une organisation à laquelle il n'a pas été invité. `false` par défaut |
| `UseUpstreamSubjectAsUserId` | Non | Utilise le `sub` amont comme identifiant local de l'utilisateur. `false` par défaut |
| `ShowOnLogin` | Non | Affiche un bouton pour cette connexion sur l'écran de connexion. `true` par défaut ; les connexions routées par domaine sont de toute façon atteintes en saisissant d'abord l'adresse e-mail |
| `ChallengeMfaAfterLogin` | Non | Applique le défi de la politique MFA de l'application après la connexion auprès de l'IdP. `true` par défaut |
| `AutoLinkExistingByEmail` | Non | Lie une première connexion à un compte local existant ayant la même adresse e-mail. `false` par défaut |
| `PassthroughParams`, `ProvisioningAttributeParams` | Non | Paramètres transmis à l'IdP / au provisionnement en aval |
| `RevalidateOnRefresh` | Non | Revérifie la session amont lorsqu'un jeton de rafraîchissement est utilisé. `false` par défaut |
| `IsExternalConnection`, `SessionExpClaim` | Non | Paramètres de session fédérée ; voir [Sessions fédérées](federated-sessions) |
| `InteractionPath` | Non | Chemin de l'application de connexion (par exemple `/guest`) affiché avant qu'une requête `idp_hint` non authentifiée soit fédérée via cette connexion. Vide fédère directement |

L'initialisation OIDC écrase davantage de valeurs que l'initialisation SAML, à chaque démarrage. Les booléens sont écrits à partir de l'initialisation, valeur par défaut comprise : une connexion initialisée qu'un opérateur a modifiée via l'API d'administration voit donc ces valeurs rétablies au redémarrage suivant. Il en va de même pour `AllowedDomains`, `PassthroughParams`, `ProvisioningAttributeParams`, `SessionExpClaim` et `InteractionPath` : une clé omise par l'initialisation est remise à vide ou à sa valeur par défaut au lieu de conserver la valeur stockée. Seuls `IconUrl` et `CreatedAt` sont toujours préservés, tandis que `ConnectionName` et `OrganizationId` le sont lorsque l'initialisation les omet.

> **Remarque :** les fournisseurs peuvent aussi être gérés à l'exécution via l'[API d'administration](admin-api). Les fournisseurs initialisés par la configuration font l'objet d'un upsert à chaque démarrage : les modifications de configuration prennent donc effet au redémarrage.

## Fournisseur de secrets {#secret-provider}

Les secrets de client OIDC amont et les graines TOTP / MFA peuvent être stockés dans Azure Key Vault plutôt qu'en clair :

| Paramètre | Description |
|---|---|
| `SecretProvider:VaultUri` | URI du Key Vault (par exemple `https://my-vault.vault.azure.net/`). S'il n'est pas défini, c'est le fournisseur **en clair** qui est utilisé, et les secrets sont stockés tels quels dans Table Storage. |
| `SecretProvider:RequireVaultReferences` | `false` par défaut. À `true`, une référence stockée sans préfixe de coffre (`kv:` pour Key Vault, `sm:` pour AWS Secrets Manager) constitue une **erreur** au lieu d'être acceptée comme valeur en clair. Activez-le une fois la migration vers le coffre terminée. |

Une fois configuré, les valeurs de secrets qui ressemblent à des références Key Vault sont résolues à l'exécution. L'authentification utilise `DefaultAzureCredential`.

### Migrer vers un coffre, puis fermer la porte {#migrating-into-a-vault-and-closing-the-door-afterwards}

Les deux fournisseurs adossés à un coffre renvoient telle quelle une référence sans préfixe, en la traitant comme une valeur en clair écrite avant que le déploiement ne dispose d'un coffre. C'est ce qui permet de migrer un système en fonctionnement secret par secret plutôt que d'un seul coup, mais laissé ouvert, c'est une voie de rétrogradation permanente : tout ce qui peut écrire une colonne de configuration (une migration à moitié terminée, un chemin d'administration qui stocke une valeur brute là où une référence est attendue, un attaquant disposant d'un accès au stockage mais pas au coffre) remplace un secret protégé par le coffre par une valeur de son choix, et celle-ci se vérifie parfaitement, car pour une référence sans préfixe, la référence *est* la valeur.

Définissez `SecretProvider:RequireVaultReferences` une fois la migration terminée. La résolution d'une référence sans préfixe lève alors une exception au lieu de renvoyer discrètement du texte en clair. Le définir alors que le fournisseur résolu est le fournisseur en clair est refusé au démarrage, car cette combinaison n'a aucun état fonctionnel : chaque référence écrite par le fournisseur en clair est sans préfixe.

Le serveur journalise aussi un avertissement au démarrage chaque fois qu'un hôte hors développement se retrouve avec le fournisseur en clair.

> ⚠️ **Production : définissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par défaut fonctionne **en clair**. Lorsque `SecretProvider:VaultUri` n'est pas défini, les secrets de client OIDC amont et les graines TOTP / MFA sont écrits en clair dans Azure Table Storage, et apparaissent donc en clair dans toute [sauvegarde](backup-restore). Pour tout déploiement en production, configurez `SecretProvider:VaultUri` afin que ces secrets soient stockés dans Key Vault.

## API d'administration {#admin-api}

| Paramètre | Par défaut | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Activée par défaut.** Définissez `false` pour désactiver tous les endpoints d'administration (ils ne seront pas enregistrés). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT requis pour accéder aux endpoints d'administration. Modifiez-le pour qu'il corresponde au nom de votre scope existant (par exemple `projects-identity-admin` pour les migrations depuis IdentityServer). |

> ⚠️ **L'API d'administration est activée par défaut et dispose de privilèges très élevés.** Le scope d'administration accorde une gestion complète et l'usurpation d'identité des utilisateurs : quiconque détient un jeton portant `AdminApi:Scope` peut émettre des jetons pour n'importe quel utilisateur, gérer les clients, et lire et écrire toute la configuration. Restreignez au niveau réseau l'accès aux endpoints d'administration (les routes d'administration `/api/v1/*`) et contrôlez strictement qui peut recevoir le scope d'administration. Par mesure de défense en profondeur, ce scope est *réservé* : il ne peut jamais être accordé à un client OAuth (voir [API d'administration](admin-api)) et ne peut pas être émis via l'endpoint d'usurpation d'identité. Définissez `AdminApi:Enabled = false` si l'API d'administration n'est pas utilisée.

## Consentement {#consent}

Le consentement par client peut être activé avec la propriété `RequireConsent` :

| Valeur | Comportement |
|---|---|
| `false` (par défaut) | L'autorisation se poursuit immédiatement après l'authentification |
| `true` | Un écran de consentement listant les scopes demandés est présenté à l'utilisateur. Le consentement est conservé 5 ans et n'est redemandé que lorsque de nouveaux scopes sont demandés. |

Les utilisateurs peuvent consulter et révoquer leurs octrois de consentement via `GET /consent/grants` et `DELETE /consent/grants/{clientId}`.

## Déconnexion back-channel {#back-channel-logout}

Enregistrez une `BackChannelLogoutUri` sur un client pour recevoir les notifications OIDC Back-Channel Logout 1.0. Lorsqu'un utilisateur se déconnecte, Authagonal envoie un jeton de déconnexion signé (JWT) à l'URI enregistrée de chaque client.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## E-mail {#email}

L'expéditeur d'e-mails intégré utilise [Resend](https://resend.com) et **s'active automatiquement** lorsque `Email:ResendApiKey` est configuré, sans qu'il soit nécessaire d'enregistrer un service. Pour utiliser un autre fournisseur, enregistrez votre propre implémentation de `IEmailService` avant d'appeler `AddAuthagonal()` (elle est prioritaire, quelles que soient les clés `Email:*`).

| Paramètre | Description |
|---|---|
| `Email:ResendApiKey` | Clé d'API Resend. Lorsqu'elle est définie, l'expéditeur Resend intégré est utilisé. |
| `Email:SenderEmail` | Adresse e-mail de l'expéditeur |
| `Email:SenderName` | Nom d'affichage de l'expéditeur (`"Authagonal"` par défaut) |

> ⚠️ **Sans expéditeur d'e-mails, l'inscription en libre-service ne fonctionne pas.** Lorsque `Email:ResendApiKey` n'est pas défini et qu'aucun `IEmailService` personnalisé n'est enregistré, un service sans effet abandonne silencieusement tous les e-mails : les e-mails de vérification et de réinitialisation du mot de passe n'arrivent jamais, et comme la connexion exige par défaut une adresse e-mail confirmée, les utilisateurs inscrits par eux-mêmes ne peuvent jamais se connecter. `UseAuthagonal` journalise un avertissement au démarrage dans cette situation. Échappatoire pour le développement et les tests : `Auth:AutoConfirmEmailDomains` confirme automatiquement les inscriptions pour les domaines listés.

Les e-mails destinés aux adresses `@example.com` sont ignorés silencieusement (pratique pour les tests).

## Cluster {#cluster}

La couche de clustering fournit une **élection de leader** (afin que les tâches réservées au leader, comme la rotation des clés de signature, s'exécutent sur exactement un nœud) et un **bus d'événements inter-nœuds**, derrière des backends interchangeables. Par défaut, tout se passe en processus : un nœud unique est toujours son propre leader, ce qui est le bon réglage pour un nœud unique et pour le développement local, sans aucune configuration.

| Paramètre | Variable d'environnement | Par défaut | Description |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interrupteur général. À `false`, le nœud fonctionne de manière autonome (toujours leader, bus d'événements en processus). |
| `Cluster:Secret` | `Cluster__Secret` | *(aucun)* | Secret partagé exigé sur l'endpoint purement interne `/_internal/backchannel-logout`. Lorsqu'il est défini, les appelants doivent le présenter dans l'en-tête `X-Cluster-Secret` (comparé en temps constant). Lorsqu'il **n'est pas défini, l'endpoint n'autorise personne** et répond 404 : une adresse source n'est pas un identifiant, et le loopback est précisément ce que présente un reverse proxy situé sur le même hôte pour chaque requête qu'il transfère, y compris celles venues d'Internet. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Option pour le développement : sans `Cluster:Secret`, accepte un appelant dont **l'adresse du pair avant le traitement des en-têtes transférés** est loopback. Les plages privées restent refusées : dans un réseau de cluster partagé, cela reviendrait à faire confiance à toutes les charges de travail voisines. Ne le définissez pas sur un hôte situé derrière un reverse proxy. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Indique si ce nœud exécute la boucle de renouvellement du bail et peut devenir leader. À `false`, le nœud rejoint quand même le cluster et consomme le bus d'événements ; il ne brigue simplement jamais le bail. Convient à un nœud qui doit recevoir les événements du cluster mais ne doit jamais assurer le rôle de leader. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Durée du bail de leader. Renouvelé à environ la moitié de cet intervalle. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Fréquence à laquelle le backend du bus d'événements interroge les messages publiés par les autres nœuds. |

**Les déploiements à plusieurs nœuds** branchent un véritable backend via le callback `configureClustering` de `AddAuthagonal` / `AddAuthagonalCore` :

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` n'enregistrent que le bus d'événements et conservent le bail en processus, pour les nœuds qui doivent recevoir les événements du cluster mais ne doivent jamais briguer le rôle de leader.

Voir [Montée en charge](scaling) pour le comportement du rôle de leader et du bus d'événements entre instances.

## En-têtes transférés (proxy de confiance) {#forwarded-headers-trusted-proxy}

Authagonal indexe la limitation de débit et le verrouillage des comptes sur l'IP du client, et n'émet HSTS que sur les requêtes HTTPS. Derrière un reverse proxy ou une ingress, l'IP et le schéma réels du client arrivent dans les en-têtes `X-Forwarded-For` / `X-Forwarded-Proto`. Ces paramètres déterminent **à quels sauts de proxy il est fait confiance** pour définir ces valeurs, afin qu'un appelant ne puisse pas falsifier `X-Forwarded-For` pour usurper l'IP du client.

| Paramètre | Variable d'environnement | Par défaut | Description |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Nombre de sauts de proxy à prendre en compte en partant de la droite de la chaîne `X-Forwarded-For`. La valeur par défaut de `1` ne fait confiance qu'au seul saut ajouté par votre ingress et ignore tout ce qui se trouve plus à gauche dans la chaîne. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (tableau) | *(vide)* | Plages CIDR (tableau de chaînes, par exemple `"10.0.0.0/8"`) autorisées à définir les en-têtes transférés. Indiquez-y le CIDR de votre proxy, de votre ingress ou de vos pods. C'est sa déclaration qui permet à `X-Forwarded-Proto` d'être pris en compte ; voir ci-dessous. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (tableau) | *(vide)* | Adresses IP de proxys individuels (tableau de chaînes) autorisées à définir les en-têtes transférés. À utiliser en complément ou à la place de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Les deux en-têtes ne bénéficient pas de la même confiance {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` ajuste l'**IP du client**, la clé sur laquelle reposent la limitation de débit, le verrouillage et la protection de `/_internal`. Sans aucune déclaration, Authagonal le prend en compte depuis le loopback et les plages RFC1918 et journalise un avertissement. C'est une valeur par défaut au mieux, et elle vaut mieux que le comportement du framework avec un ensemble de confiance vide, qui consiste à accepter l'en-tête de *n'importe quel* appelant.

`X-Forwarded-Proto` modifie le **schéma**, et le schéma décide si `/connect/*` répond tout court (RFC 6749 §3.1/§3.2), si les cookies sont marqués `Secure` et si les URL absolues générées sont en https. Il n'est pris en compte **que** depuis un proxy que vous avez déclaré dans `KnownNetworks` / `KnownProxies`. Une adresse privée n'est pas une déclaration : Authagonal est livré sous forme de bibliothèque et ne peut pas voir le réseau sur lequel il a été déployé, si bien que « le pair a une adresse privée » n'est qu'une supposition sur la topologie. Sur un réseau local plat, un VPC partagé ou un pont de conteneurs partagé, toutes les charges de travail voisines se trouvent dans ces plages et pourraient affirmer `https` pour une requête arrivée en clair.

**Si votre proxy n'a pas d'adresse fixe** (une ingress Kubernetes, un équilibreur de charge dont l'adresse change, une plateforme qui ne vous communique pas le CIDR du saut), déclarez chaque pair comme proxy :

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

C'est sans danger exactement lorsque rien d'autre que le proxy ne peut joindre le processus, ce qui est l'hypothèse sur laquelle un tel déploiement repose déjà. L'écrire la place là où elle peut être relue, au lieu de laisser la bibliothèque la deviner. Si d'autres charges de travail *peuvent* joindre Kestrel directement, elles peuvent falsifier le schéma et l'IP du client avec ce réglage : épinglez plutôt le véritable CIDR.

### Proxy non déclaré : tous les quotas par source sont partagés {#undeclared-proxy-every-per-source-quota-is-shared}

Les limites de débit indexées sur l'adresse de l'appelant (connexion, inscription, mot de passe oublié, enregistrement dynamique de clients, ACS SAML) doivent savoir quel client a effectué la requête. Derrière un reverse proxy, il s'agit de l'IP du client transférée, et celle-ci ne constitue une preuve que si vous avez déclaré le proxy qui l'a écrite. Sans aucune déclaration, Authagonal indexe ces quotas sur le pair qu'il observe réellement, c'est-à-dire, derrière un proxy, le proxy lui-même : **tous les clients partagent un même budget, et n'importe quel appelant peut l'épuiser pour tout le monde** (la valeur par défaut pour la connexion est de 30 tentatives par tranche de 5 minutes).

C'est délibéré et non un bogue, et cela ne peut pas être corrigé dans le serveur. L'alternative, indexer quand même sur la valeur transférée, offre à un appelant un nouveau budget à chaque requête en faisant varier un en-tête, car derrière un équilibreur de charge L4, le saut transféré le plus à droite *est* l'en-tête de l'appelant lui-même. Laquelle des deux situations est la vôtre, c'est exactement ce que la déclaration indique au serveur, et rien d'autre ne le peut. Déclarez le proxy et les quotas deviennent propres à chaque client.

> ⚠️ **Un proxy qui termine le TLS est requis, et il doit être déclaré.** Authagonal doit fonctionner derrière un reverse proxy qui termine le TLS (ou terminer lui-même le TLS). HSTS (`Strict-Transport-Security`) n'est émis que sur les requêtes HTTPS, et les endpoints OAuth refusent purement et simplement les requêtes en clair, sauf si `Auth:AllowInsecureHttp` est défini : le proxy doit donc transférer `X-Forwarded-Proto: https` **et** figurer dans `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` pour que HSTS soit envoyé et que `/connect/*` réponde tout court. Ne rien déclarer est l'échec de mise à niveau le plus courant : l'en-tête arrive, rien n'est habilité à en tenir compte, et chaque requête `/connect/*` répond 400 sur un déploiement qui est pourtant bel et bien en TLS. Le journal de démarrage le signale, tout comme le corps du refus.

## Récupérations sortantes (protection contre la SSRF) {#outbound-fetches-ssrf-guard}

Authagonal effectue, à l'initiative du serveur, des requêtes HTTP vers des URL qu'il n'a pas choisies : les métadonnées SAML ou le document de découverte OIDC d'un IdP amont, le `jwks_uri` d'un client lors d'une authentification `private_key_jwt`, une URI de déconnexion back-channel, un callback de provisionnement. Certaines de ces URL sont fournies par la personne qui a enregistré un client, et une URL désignant `169.254.169.254` ou un hôte situé dans votre cluster devient alors une requête qu'Authagonal effectue pour le compte d'un attaquant.

Chacune de ces récupérations est protégée deux fois. Le **contrôle de l'URL** refuse les schémas autres que http(s), les adresses internes littérales et les noms `localhost` / `.local` / `.internal`, au moment où l'URL est acceptée (une écriture d'administration, un enregistrement dynamique de client), là où l'erreur peut être attribuée à la personne qui l'a saisie. Le **contrôle de l'adresse** s'exécute au niveau du socket : il résout l'hôte, refuse chaque adresse renvoyée qui est interne, et se connecte à une adresse qu'il a effectivement vérifiée au lieu de rendre le nom au système d'exploitation. C'est ce second contrôle qu'une vérification textuelle ne peut pas assurer, car un nom d'hôte n'est pas un texte sur lequel l'attaquant est tenu d'être honnête : `logout.attacker.test` passe toutes les règles de suffixe et d'adresse littérale, puis répond avec l'adresse du service de métadonnées du cloud. Comme une redirection est une nouvelle connexion, le contrôle de l'adresse est réexécuté à chaque saut.

Les deux sont activés par défaut, et la plupart des déploiements ne les remarquent jamais. Deux situations les rendent visibles.

### Atteindre délibérément une destination interne {#reaching-an-internal-destination-on-purpose}

Se fédérer avec un IdP joignable uniquement sur votre réseau privé, ou provisionner une application qui s'exécute dans le même cluster, est refusé par exactement la même règle que celle qui arrête l'attaque. Nommez ces destinations :

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Forme de l'entrée | Autorise |
|---|---|
| `idp.corp.internal` | Cet hôte exact, et toutes les adresses vers lesquelles il se résout |
| `*.corp.internal` | Tout hôte sous ce suffixe, et toutes les adresses vers lesquelles ils se résolvent |
| `10.4.0.0/16`, `fd00:1234::/48` | Ce réseau, quel que soit le nom |
| `10.4.1.7` | Cette adresse unique, quel que soit le nom |

Sous forme de variable d'environnement : `Auth__AllowedInternalTargets__0`, `__1`, et ainsi de suite. Une entrée CIDR mal formée provoque un échec au démarrage au lieu de n'autoriser silencieusement rien.

**Cette liste ne concerne que les URL que vous avez fournies.** La récupération des métadonnées SAML amont, la découverte OIDC amont (y compris les `token_endpoint`, `userinfo_endpoint` et `jwks_uri` que désigne ce document) et les callbacks de provisionnement. Elle ne concerne délibérément **pas** un `jwks_uri` ou une URI de déconnexion back-channel enregistrés par un client, pour lesquels un hôte interne ne correspond jamais à un déploiement : ouvrir une cible de fédération ne peut donc pas ouvrir aussi le service de métadonnées à une requête `/connect/token` anonyme. Il n'existe pas d'interrupteur global de désactivation.

Notez que le https reste exigé sur les deux URL de métadonnées de fédération, indépendamment de cette liste. Ce document porte les clés et les certificats par rapport auxquels chaque assertion amont est validée, et un réseau privé n'est pas un canal sécurisé.

> ⚠️ **Hôtes multi-locataires : vérifiez qui écrit l'URL des métadonnées avant de lister quoi que ce soit.** Cette liste est limitée aux cibles que *vous* avez configurées, et dans un déploiement mono-locataire, l'administrateur des connexions, c'est vous. Si vous exploitez Authagonal pour d'autres (un SaaS dans lequel les administrateurs de locataires configurent leurs propres connexions SAML/OIDC via le portail ou l'API d'administration), alors `MetadataLocation` est fourni par **le client**, et chaque entrée que vous ajoutez ici est accessible à tout locataire qui fait pointer une connexion vers elle. Laissez-la vide sur un tel hôte (la valeur par défaut) et, si un locataire a réellement besoin d'un IdP sur site, donnez-lui un chemin de sortie qui aboutit hors de votre réseau plutôt que d'en ouvrir un depuis l'intérieur.

### Si votre sortie réseau exige un proxy HTTP {#if-your-egress-requires-an-http-proxy}

Le contrôle de l'adresse est rattaché à `SocketsHttpHandler.ConnectCallback`, et lorsqu'un proxy est en place, .NET invoque ce callback avec l'endpoint du **proxy** et jamais avec celui de la cible : le contrôle inspecterait donc le proxy, le trouverait parfaitement routable et autoriserait tout. Il échouerait en mode ouvert précisément dans les réseaux les plus susceptibles d'avoir un proxy. Les clients protégés définissent donc `UseProxy = false`, et dans un réseau qui passe uniquement par un proxy, leurs récupérations échouent.

`Auth:AllowOutboundProxy` refait passer par le proxy les récupérations configurées par l'opérateur (métadonnées SAML, découverte OIDC, callbacks de provisionnement). Vous conservez le contrôle de l'URL et perdez pour elles le contrôle de l'adresse : un nom d'hôte qui se résout vers une adresse interne n'est plus intercepté. Ce paramètre ne concerne **pas** la récupération du `jwks_uri` d'un client ni l'envoi des déconnexions back-channel : ces cibles sont choisies par la personne qui enregistre le client et atteignables depuis des requêtes anonymes, il n'existe donc aucun interrupteur pour elles. Un réseau qui doit les faire passer par un proxy a besoin d'une passerelle de sortie filtrant la SSRF placée devant elles.

`UseAuthagonal()` journalise un avertissement au démarrage lorsqu'il trouve `HTTPS_PROXY`, `HTTP_PROXY` ou `ALL_PROXY` défini, en indiquant quels clients le contournent ; sinon, le symptôme est « le SSO a cessé de fonctionner » sans rien qui désigne la cause.

### Ce qui n'est pas protégé {#what-is-not-guarded}

Les clients sortants du BFF et l'envoi des e-mails. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` relève de votre propre configuration, dont l'exemple documenté est une adresse interne ; le client de jetons du BFF dialogue avec l'autorité que vous avez configurée, et le proxy refuse déjà toute cible composée qui sortirait de l'autorité amont configurée : un appelant ne peut donc pas orienter ces requêtes. `Resend` envoie ses requêtes vers une constante définie à la compilation. Ces trois clients utilisent normalement le proxy ambiant.

## Limitation de débit {#rate-limiting}

Des limites de débit intégrées protègent les endpoints exposés aux abus :

| Endpoint | Limite | Fenêtre | Indexée sur |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 minutes (`Auth:LoginWindowMinutes`) | Adresse source et, séparément, adresse e-mail soumise |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 heure (`Auth:RegistrationWindowMinutes`) | IP du client |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 heure (`Auth:PasswordResetWindowMinutes`) | Adresse e-mail cible |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 heure (`Auth:PasswordResetWindowMinutes`) | IP du client |
| `POST /connect/register` (lorsqu'il est activé) | 10 | 1 heure | IP du client |
| Endpoints SCIM | 200 | 1 minute | Client SCIM |

Par défaut, les limites sont appliquées **en processus, nœud par nœud** (derrière le point d'extension `IRateLimiter`) : avec N instances, le plafond effectif vaut donc N fois la valeur configurée. Considérez-les comme un filet de sécurité et appliquez la limite globale qui fait foi en périphérie (WAF / ingress / CDN). Voir [Montée en charge](scaling#rate-limiting).

### Limites à l'échelle du cluster (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

Définissez `Auth:DurableRateLimiting` à `true` pour déplacer les compteurs dans le store que le déploiement
utilise déjà, afin que tous les réplicas partagent un même budget et que le plafond cesse d'être multiplié par le nombre d'instances.

| | en processus (par défaut) | durable |
|---|---|---|
| Plafond sur N réplicas | N fois la valeur configurée | la valeur configurée |
| Coût par contrôle | aucun | un aller-retour vers le store |
| Survit au redémarrage d'un pod | non | oui |
| Backends | tous | Azure Table, SQL, DynamoDB |

Cela vaut la peine de l'activer lorsqu'un budget protège quelque chose de devinable, avant tout le `user_code` du flux d'appareil, pour lequel
la limite de tentatives est la seule chose qui sépare un attaquant d'un code qui accorde une session active, et où un
budget qui croît avec le nombre de réplicas n'a pas la bonne forme. C'est moins utile pour les limites de volume, pour lesquelles la
périphérie reste de toute façon la borne qui fait foi.

Détails importants en production :

- **Ce n'est pas gratuit.** Chaque contrôle de limitation de débit devient un aller-retour vers le store, y compris sur les chemins de connexion, de jeton
  et SCIM. Un déploiement à un seul nœud n'y gagne rien (dans ce cas, la limite par nœud *est* la limite à l'échelle du cluster) et doit
  le laisser désactivé.
- **Fenêtres fixes : les rafales peuvent donc chevaucher une limite.** Un budget de N signifie « N par fenêtre, et jusqu'à 2N
  à cheval sur une limite », et les budgets fournis disposent de cette marge. C'est ce qui permet au compteur d'être un simple
  incrément atomique sur chaque backend, propriété sur laquelle repose l'exactitude.
- **Il échoue en mode ouvert.** Si le store est injoignable, la requête est autorisée et une erreur est journalisée : le
  limiteur protège le chemin de connexion et ne doit pas devenir un moyen de le faire tomber. Conservez la règle en périphérie.
- **L'hôte ne démarrera pas** si vous définissez ce paramètre sans un fournisseur qui fournit `IRateLimitCounterStore`.
  Il refuse plutôt que de revenir discrètement à la limitation par nœud que vous venez de désactiver.
- **Les lignes de compteurs sont nettoyées automatiquement** : par le TTL natif pour DynamoDB, par `SqlExpiryReaper` pour SQL, et pour Azure
  Table par une purge exécutée uniquement sur le leader (Table Storage n'a ni TTL ni arithmétique côté serveur ; c'est donc aussi
  le backend où un incrément coûte une lecture plus une écriture conditionnelle).

## CORS {#cors}

CORS est configuré dynamiquement et **limité par chemin** : l'ancienne description en une ligne (« les origines de tous
les clients enregistrés sont automatiquement autorisées ») décrivait bien plus que ce que fait réellement le fournisseur.

- **Les origines enregistrées par les clients** (`AllowedCorsOrigins` sur un client) ne sont prises en compte que sous `/connect/` et
  `/.well-known/`. Elles n'ouvrent **pas** `/api/auth/`, `/api/v1/` ni `/scim/`. Un client désactivé n'apporte
  rien, et une origine mal formée est écartée.
- **Les identifiants ne sont jamais autorisés** sous `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` ou `/approvals`, pour
  aucune origine, qu'elle soit configurée par l'opérateur ou enregistrée par un client. Un client navigateur qui les appelle avec
  `credentials: 'include'` depuis une autre origine échouera quelle que soit la configuration ; utilisez un
  backend-for-frontend (voir le package `@authagonal/bff`) plutôt que des appels cross-origin avec identifiants.
- Les politiques résolues sont mises en cache pendant 60 minutes.

Ainsi, une origine ajoutée aux `AllowedCorsOrigins` d'un client fait fonctionner `/connect/*` mais pas `/api/v1/*`.
C'est délibéré : ces chemins transportent le cookie de session et la surface d'administration.

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` dialogue avec le moteur de secrets Transit de Vault : signature, vérification, chiffrement, déchiffrement et HMAC à clé.
C'est la brique de base d'un `IFieldCipher` ou d'un `IIndexTokenizer` adossé à Vault, que vous enregistrez vous-même.

**La signature des JWT n'est pas déléguée à Vault.** `ProtocolKeyManager` signe toujours avec la clé de
`ISigningKeyStore`, et aucun point d'extension ne permet de lui substituer une clé Vault. Voir
[Extensibilité](extensibility) pour ce que cela exigerait.

Cela se configure par programmation lorsque vous l'hébergez comme bibliothèque.

## Exemple complet {#full-example}

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
