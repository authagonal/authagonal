---
layout: default
title: Migration
locale: fr
---

# Migration depuis Duende IdentityServer

Le paquet `Authagonal.Migration` effectue une migration ponctuelle de Duende IdentityServer + SQL
Server vers les stockages d'Authagonal. Le même moteur est disponible de deux façons :

- **Exécuteur hébergé** (recommandé) : un service d'arrière-plan au sein de votre hôte Authagonal, qui
  exécute la migration une seule fois au déploiement, sous réserve de détenir le rôle de leader du cluster, sans bloquer le démarrage.
- **CLI** : `tools/Authagonal.Migration.Cli`, pour des exécutions locales ou hors ligne vers une cible Table Storage.

SqlClient ne se trouve que dans ce paquet ; les hôtes qui ne migrent pas n'en héritent donc jamais.

## Exécuteur hébergé {#hosted-runner}

Ajoutez-le après `AddAuthagonal` (il dépend des stockages, du fournisseur de secrets et du rôle de leader du cluster) :

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

Le second appel `Map` est obligatoire et distinct : ce paquet référence `Authagonal.Server`, si bien que
`MapAuthagonalEndpoints` ne peut pas l'atteindre. Sans lui, `GET /admin/migration/status` répond 404,
ce qui est indiscernable d'un refus de la politique `IdentityAdmin`, et l'exécution journalise un avertissement au démarrage
pour le signaler.

Configurez-le via la section `Migration` :

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

L'exécuteur :

1. Attend `StartupDelaySeconds` (les services d'initialisation terminent d'abord ; le démarrage n'est jamais bloqué).
2. Ne fait rien si un marqueur `Completed` non `DryRun` existe déjà pour `Version`.
3. Attend jusqu'à `LeaseWaitMinutes` de devenir leader du cluster (un seul pod exécute la migration).
4. Écrit un marqueur `Started`, exécute le moteur, puis un marqueur `Completed`/`Failed` accompagné du rapport.

La perte du rôle de leader en cours d'exécution annule le moteur ; le nouveau leader relance l'exécution, sans risque puisque chaque passe est
idempotente. Suivez la progression sur `GET /admin/migration/status` (protégé par la politique `IdentityAdmin`).

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(Pas de séparateur `--` après le nom de l'image.) Ou depuis les sources :

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## Ce qui est migré {#what-gets-migrated}

| Source (SQL Server) | Cible | Remarques |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Utilisateurs + index d'e-mail et de nom | Identifiants conservés tels quels. Regroupement des revendications : `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (variantes xmlsoap comprises) ; les revendications d'e-mail sont abandonnées ; tout le reste → attributs personnalisés. Les hachages de mot de passe nuls (utilisateurs uniquement en SSO externe) ne posent pas de problème. Les hachages BCrypt / ASP.NET Identity V3 sont vérifiés sans modification et convertis en PBKDF2 natif à la connexion suivante. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = ignoré (idempotent) |
| `AspNetRoles` + `AspNetUserRoles` | Rôles + liens utilisateur-rôle | Une table de correspondance identifiant de rôle→nom résout les attributions des utilisateurs |
| `ApiScopes` + `IdentityResources` | Scopes | Les noms existants (issus de l'initialisation) sont ignorés ; les revendications des scopes sont copiées |
| `Clients` Duende + tables enfants | Clients | Secrets étiquetés `SHA256$`/`SHA512$` selon la longueur du condensé (les autres sont abandonnés avec un avertissement) ; secrets expirés ignorés ; les clients issus de la configuration l'emportent (ignorés) |
| `ApiResources` Duende | (aplaties) | Audiences → clients créés par la migration ; revendications des ressources → scopes créés par la migration |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | Le CSV `AllowedDomains` est découpé en enregistrements de domaines SSO |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | Même découpage des domaines |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | Secret TOTP base32→protégé (`duende-totp`) ; codes de récupération hachés (`duende-rc-{n}`) ; utilisateur ignoré s'il a déjà une MFA |
| `PersistedGrants` Duende (jetons d'actualisation) | Octrois | **Impossible avec un Duende standard**, voir ci-dessous. Exige `MigrateRefreshTokens` *et* `SourceGrantKeysAreUnhashed` ; sinon, ignoré avec un avertissement, et les utilisateurs se reconnectent. |

## Options {#options}

| Option | Valeur par défaut | Description |
|---|---|---|
| `Enabled` | `false` | Interrupteur général de l'exécuteur hébergé |
| `DryRun` | `false` | Parcourt la source et produit le rapport de validation complet (jeu de caractères et longueur des identifiants, e-mails en double, inventaire des tables et colonnes, décomptes par passe) sans rien écrire |
| `Version` | `"1"` | Marqueur d'exécution. Incrémentez-le pour relancer un balayage différentiel. Seul un marqueur `Completed` non `DryRun` empêche une nouvelle exécution |
| `UsersMode` | `CreateOnly` | `CreateOnly` ignore les utilisateurs existants ; `Upsert` les écrase. **N'utilisez jamais `Upsert` après la bascule** : cela écrase les mots de passe rehachés et les nouvelles MFA |
| `MigrateClients` | `true` | Migre les clients OAuth. Les clients issus de la configuration l'emportent toujours et les clients existants sont ignorés |
| `MigrateRefreshTokens` | `false` | Inclut les jetons d'actualisation actifs. Exige `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Affirme que la colonne source `PersistedGrants.Key` contient les identifiants de jeton tels quels. Ne vaut true que pour un fork doté d'un stockage d'octrois personnalisé |
| `Source:ConnectionString` | *(aucune)* | Connexion SQL Server de la source Duende |
| `MaxDegreeOfParallelism` | `32` | Concurrence d'écriture bornée pour les passes à fort volume (utilisateurs, connexions externes, MFA, jetons d'actualisation). Réduisez-la pour les comptes modestes ou sujets à la limitation de débit ; `1` est entièrement séquentiel |
| `LeaseWaitMinutes` | `10` | Exécuteur hébergé : cesse d'attendre le rôle de leader du cluster après ce délai ; un redémarrage ultérieur réessaie |
| `StartupDelaySeconds` | `30` | Exécuteur hébergé : délai avant le lancement, afin que les services d'initialisation terminent et que le démarrage ne soit pas bloqué |

## Idempotence et balayages différentiels {#idempotency--delta-sweeps}

Chaque passe est idempotente (ignorée si l'élément existe, identifiants MFA déterministes) ; la migration peut donc être relancée sans risque.
Exécutez-la plusieurs jours avant la bascule, puis incrémentez `Version` pour un dernier balayage différentiel peu avant la bascule, afin de récupérer
les utilisateurs inscrits entre-temps. Les enregistrements existants sont ignorés (ou mis à jour en mode `Upsert`), jamais dupliqués.

## Ce qui n'est PAS migré {#what-is-not-migrated}

- **Les jetons d'actualisation actifs, avec un Duende standard.** Le `DefaultGrantStore` de Duende ne conserve jamais
  l'identifiant d'un jeton d'actualisation : `PersistedGrants.Key` contient `base64(SHA-256(handle + ":" + grantType))`, et
  l'identifiant présenté est de nouveau haché lors de la recherche. L'identifiant ne peut donc pas être retrouvé à partir de la
  base de données source, et les lignes migrées seraient définitivement inutilisables, ce qui est pire que de ne pas
  migrer : le rapport les compte comme créées et la panne ne se manifeste qu'à la première
  actualisation de jeton après la bascule. Prévoyez la bascule en comptant sur une reconnexion, ou exécutez une couche de double lecture pendant
  la période de transition. `SourceGrantKeysAreUnhashed` n'existe que pour un fork dont le stockage d'octrois
  conserve les identifiants tels quels, et un tel fork doit aussi se charger de convertir `PersistedGrants.Data` de la
  forme `RefreshToken` de Duende vers `RefreshTokenData`.
- **Jetons et groupes SCIM**, **provisionnements d'utilisateurs** : aucun équivalent dans Duende ; ils démarrent vides.
- **Clés de signature** : non automatisé. Pour que les jetons existants restent valides après la bascule, exportez la clé
  de signature RSA de Duende et importez-la dans la table `SigningKeys` peu avant la bascule.

## Stratégie de bascule {#cutover-strategy}

1. Déployez en mode inactif (`Enabled=false`).
2. `Enabled=true, DryRun=true` → redémarrage → examinez le rapport sur `/admin/migration/status`.
3. `DryRun=false` → redémarrage → vérifiez que le marqueur est `Completed` et testez quelques connexions.
4. Incrémentez `Version` pour le dernier balayage différentiel, puis faites pointer les clients et les BFF vers Authagonal. **Attendez-vous à une
   reconnexion forcée**, voir ci-dessus.
5. Surveillez ; retour arrière = faire de nouveau pointer vers le déploiement Duende, resté intact.

## Import d'utilisateurs NDJSON {#ndjson-user-import}

Une seconde source d'import, indépendante, dans le même paquet `Authagonal.Migration` : un fichier NDJSON plat
(un objet JSON par ligne) au lieu d'une connexion à une base de données active, et uniquement des utilisateurs, sans clients, rôles,
scopes ni configuration de fédération. Elle est conçue pour migrer la table d'utilisateurs propre à une application existante (un stockage
ASP.NET Identity fait maison, une table Rails/Devise exportée en bcrypt, une application Node en scrypt, ...), de sorte que les utilisateurs
continuent de se connecter avec leur ancien mot de passe, qui est rehaché de façon transparente en PBKDF2 natif à leur
prochaine connexion réussie : le même rehachage différé que celui sur lequel repose l'importateur Duende ci-dessus.

### Schéma des enregistrements {#record-schema}

Un objet JSON par ligne. `email` est le seul champ obligatoire ; tous les autres sont facultatifs. **Un champ de premier
niveau inconnu fait échouer la ligne** (strict par défaut), sauf si `--AllowUnknownFields true` est passé.

| Champ | Type | Remarques |
|---|---|---|
| `email` | string | Obligatoire. Doit être une adresse e-mail plausible. Clé de dédoublonnage insensible à la casse. |
| `username` | string | Aucune colonne `AuthUser` dédiée ; stocké dans `CustomAttributes["username"]`. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | Aucune colonne dédiée ; stocké dans `CustomAttributes["displayName"]`. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Vaut `false` par défaut en cas d'absence. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, stocké **tel quel**. Tout format que `PasswordHasher` reconnaît à la connexion (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`) est vérifié sans modification, puis converti en PBKDF2 natif. Aucun contrôle au-delà de la non-vacuité : un hachage mal formé échoue simplement à la vérification lors de la connexion, comme il le ferait hors migration. Omettez-le pour les utilisateurs uniquement SSO ou sans mot de passe. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | Fusionné dans `AuthUser.CustomAttributes` |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Actif par défaut en cas d'absence. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Vaut par défaut l'heure de l'import en cas d'absence. |
| `externalId` | string | → `AuthUser.ExternalId`, le même champ que celui où l'importateur Duende place l'identifiant utilisateur de la base de données source. |

Exemple de fichier (5 lignes) :

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI {#cli-1}

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

Même cible (Azure Table Storage) et même garde-fou sur les données personnelles en clair que la CLI Duende ci-dessus : cette source écrit
les lignes `AuthUser` directement dans Table Storage, sans `IFieldCipher`/`IIndexTokenizer` enregistré par un hôte ;
elle refuse donc de s'exécuter tant que `--AllowPlaintextPii true` ne confirme pas que la cible n'a configuré ni l'un ni l'autre (ou bien
branchez `NdjsonUserImportEngine` dans le conteneur d'injection de dépendances de l'hôte, où ces points d'extension sont résolus).
Contrairement à la CLI Duende, il n'existe pas de garde-fou `--AllowPlaintextSecrets` : cette source n'écrit jamais de graines TOTP
ni de secrets de clients OAuth, seulement des champs de profil utilisateur et un hachage de mot de passe stocké tel quel.

### Options {#options-1}

| Option | Valeur par défaut | Description |
|---|---|---|
| `--Input` | *(obligatoire)* | Chemin du fichier NDJSON |
| `--Target:ConnectionString` | *(obligatoire)* | Chaîne de connexion Azure Table Storage |
| `--DryRun` | `false` | Analyse et valide chaque ligne, résout les doublons par rapport à la cible et produit le rapport complet, sans rien écrire |
| `--OnDuplicate` | `skip` | Traitement d'une ligne dont l'e-mail (insensible à la casse) correspond déjà à un utilisateur existant : `skip` (le laisser intact, idempotent), `update` (fusionner les champs présents de la ligne dans l'utilisateur existant) ou `fail` (interrompre immédiatement l'exécution) |
| `--BatchSize` | `500` | Nombre de lignes entre deux messages de progression dans le journal. Ce n'est pas un mécanisme d'écriture par lots : `IUserStore` n'a pas d'API d'écriture en masse, donc chaque import ou mise à jour reste un appel au stockage |
| `--AllowUnknownFields` | `false` | Accepte et ignore les propriétés JSON de premier niveau absentes du schéma ci-dessus, au lieu de faire échouer la ligne |
| `--ContinueOnError` | `false` | Termine avec le code 0 même si une ou plusieurs lignes n'ont pas pu être analysées ou validées. Ne s'applique pas à `--OnDuplicate fail`, qui interrompt toujours l'exécution, quelle que soit la valeur de cette option |

### Rapport de synthèse et codes de sortie {#summary-output--exit-codes}

Le rapport est affiché en JSON : `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed` et les 20 premiers
`Failures` (`LineNumber` + `Reason`). Les lignes vides ne sont comptées nulle part. Codes de sortie :

- `0` : succès (ou `--ContinueOnError true` avec une ou plusieurs lignes en échec)
- `1` : une ou plusieurs lignes n'ont pas pu être analysées ou validées, et `--ContinueOnError` n'était pas défini
- `2` : l'exécution a été interrompue : `--OnDuplicate fail` a rencontré un e-mail existant, ou une option obligatoire manquait

### Idempotence {#idempotency}

Avec la valeur par défaut `--OnDuplicate skip`, relancer l'import avec un fichier inchangé n'a aucun effet la seconde fois :
chaque ligne dont l'e-mail existe déjà est comptée comme ignorée et rien n'est écrit. `update` peut lui aussi être relancé
sans risque (il réapplique toujours les mêmes champs) ; `fail` est destiné à un import ponctuel qui ne doit jamais
entrer silencieusement en collision avec des comptes existants.

### Ce qui n'est PAS importé {#what-is-not-imported}

- **Rôles, scopes, clients OAuth, configuration de fédération.** Cette source ne concerne que les utilisateurs : voir l'importateur
  Duende ci-dessus si vous en avez aussi besoin.
- **Identifiants MFA, connexions externes.** Ils ne font pas partie du schéma ; ajoutez-les via les parcours standard de configuration MFA
  ou de SSO après l'import.
