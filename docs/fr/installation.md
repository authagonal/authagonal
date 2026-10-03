---
layout: default
title: Installation
locale: fr
---

# Installation

## Docker (recommandé) {#docker-recommended}

Récupérez et lancez l'image précompilée :

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Pour le développement local avec Azurite (l'émulateur Azure Storage) :

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` est un réglage de développement.** Les §3.1/§3.2 de la RFC 6749 exigent TLS sur les endpoints d'autorisation et de jeton : Authagonal refuse donc les requêtes non https vers `/connect/*`, sauf si ce réglage est défini. Le schéma est lu après le traitement des en-têtes transférés : un proxy qui termine TLS et transmet `X-Forwarded-Proto: https` satisfait donc l'exigence avec ce réglage désactivé, et c'est ce que doit faire tout déploiement accessible à quelqu'un d'autre que vous. Lorsqu'il est activé, un observateur placé sur le chemin réseau lit le code d'autorisation, le secret client contenu dans l'en-tête `Authorization: Basic`, ainsi que les jetons d'accès et de rafraîchissement. Voir [Configuration](configuration#authentication).

## Compiler depuis les sources {#building-from-source}

### Prérequis {#prerequisites}

- SDK .NET 10
- Node.js 24+

Authagonal cible `net9.0` et `net10.0`, et exige à l'exécution un framework partagé **corrigé** : **9.0.18 ou 10.0.10 au minimum**. Voir la [checklist de sécurité pour la production](#production-security-checklist) pour la raison, et `Auth:RequireMinimumRuntime` pour transformer la vérification au démarrage en refus de démarrer.

### Compilation {#build}

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Build Docker {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## En tant que bibliothèque (NuGet) {#as-a-library-nuget}

Référencez les paquets Authagonal dans votre propre projet ASP.NET Core :

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Le paquet du fournisseur de stockage est interchangeable : `Authagonal.AzureProvider` pour Azure Table Storage (le câblage par défaut de `AddAuthagonal()`), `Authagonal.SqlProvider` pour PostgreSQL ou SQLite auto-hébergés (voir [Backend SQL](#sql-backend)), ou `Authagonal.AwsProvider` pour DynamoDB / S3 / Secrets Manager (voir [Backend AWS](#aws-backend)).

> **L'ordre d'enregistrement compte.** Un fournisseur de stockage doit être enregistré **avant** `AddAuthagonal()`. C'est cet enregistrement préexistant de `IUserStore` qui fait que `AddAuthagonal()` saute son câblage intégré vers Azure Table Storage ; un fournisseur enregistré après coup perd, sans avertissement, toutes les interfaces que `AddAuthagonal()` a déjà renseignées, car ces enregistrements utilisent `TryAdd`.
>
> Trois interfaces (`IOrganizationStore`, `IOrganizationMembershipStore` et `IScimGroupRoleMappingStore`) disposent d'implémentations de repli en mémoire, vides et en lecture seule, afin que l'injection de dépendances les résolve sur un hôte qui ne branche aucun store pour elles. `AddAuthagonal()` retire ces implémentations de repli avant l'enregistrement du fournisseur de stockage et les rétablit ensuite par `TryAdd` : le store durable d'un fournisseur l'emporte toujours, et l'implémentation de repli couvre quand même un hôte qui n'en a pas. Si vous enregistrez votre propre implémentation de l'une de ces trois interfaces, enregistrez-la avant `AddAuthagonal()`, comme n'importe quel autre store.

Composez-le ensuite dans votre `Program.cs` :

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Voir [Extensibilité](extensibility) pour tous les points de surcharge, et [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) pour un exemple complet.

### E-mail {#email}

L'expéditeur [Resend](https://resend.com) intégré s'active automatiquement lorsque `Email:ResendApiKey` et `Email:SenderEmail` sont configurés, sans enregistrement de service. Sans aucun `IEmailService`, les e-mails de vérification et de réinitialisation du mot de passe sont **ignorés sans avertissement** ; et comme la connexion exige par défaut un e-mail confirmé, les utilisateurs inscrits par eux-mêmes ne peuvent jamais se connecter (`UseAuthagonal` journalise un avertissement au démarrage). Définissez les clés `Email:*`, enregistrez votre propre `IEmailService` avant `AddAuthagonal()`, ou listez vos domaines dans `Auth:AutoConfirmEmailDomains` pour sauter la vérification (développement et test uniquement). Voir [Configuration → E-mail](configuration#email).

## Backend SQL {#sql-backend}

Pour fonctionner sur votre propre base de données plutôt que sur un service cloud, référencez `Authagonal.SqlProvider` et enregistrez-le **avant** `AddAuthagonal()` : ce sont ces enregistrements qui font que `AddAuthagonal()` saute son câblage vers Azure Table Storage :

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Les tables reproduisent une à une les structures Azure et DynamoDB et sont créées au démarrage si elles sont absentes (chaque instruction est `IF NOT EXISTS` : plusieurs pods peuvent l'exécuter en concurrence sans risque, et elle n'a aucun effet sur un schéma que vous avez provisionné vous-même). Aucune configuration `Storage:*` n'est nécessaire. Le trousseau de clés DataProtection est conservé dans la même base de données, si bien que les cookies et les jetons antifalsification survivent aux redémarrages et fonctionnent d'un pod à l'autre sans service supplémentaire.

SQLite sérialise les écritures : c'est donc un backend à nœud unique, pour lequel le bail en processus et le bus d'événements de cluster enregistrés par défaut sont la bonne combinaison. Un déploiement PostgreSQL sur plusieurs pods doit utiliser `clustering.UseSql(dataSource)` pour l'élection du leader.

> **Collation.** Sur PostgreSQL, les colonnes de clé sont fixées à `COLLATE "C"`. Le schéma de clés est ordonné octet par octet partout (bornes de préfixe, plages de partition par environnement, balayage d'expiration des octrois, pagination par jeu de clés), et une base de données créée avec une collation linguistique (`en_US.UTF-8` et les locales ICU sont les valeurs par défaut courantes) ordonnerait différemment la ponctuation et la casse, et renverrait sans avertissement les mauvaises lignes. Cette fixation rend la structure indépendante de la manière dont la base a été créée ; vous n'avez pas besoin de la créer d'une façon particulière.

> ⚠️ **Le matériel de clés réside dans cette base de données.** Sur Azure, la clé de signature des jetons se trouve dans Table Storage et le trousseau DataProtection dans un conteneur Blob, chacun avec un RBAC accordable séparément ; sur AWS, dans DynamoDB et S3. Sur SQL, les deux sont des tables derrière la même chaîne de connexion que tout le reste : traitez donc la chaîne de connexion comme l'équivalent de la clé de signature. Sinon, un `pg_dump`, un réplica en lecture, un rôle analytique disposant de `SELECT` ou une sauvegarde restaurée donnent à la fois la capacité d'émettre des jetons pour n'importe quel sujet et les clés derrière chaque cookie d'authentification. Enregistrez un `IFieldCipher` avant `AddAuthagonalPostgres()` pour chiffrer `SigningKeys.keyMaterialJson` au repos, et définissez `DataProtection:KeyVaultKeyId` ou `DataProtection:CertificateThumbprint` pour que le trousseau ne soit pas stocké avec un `<masterKey>` en clair : un nouveau déploiement qui conserve le trousseau sans l'un d'eux est refusé au démarrage, et un déploiement existant reçoit un avertissement de niveau `Critical` à chaque démarrage. Voir le [README du paquet](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys) pour ces deux options, ainsi que pour placer le trousseau dans un schéma séparé doté de son propre rôle.

Voir le [README du paquet](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) pour la structure des tables, les primitives de concurrence qui sous-tendent chaque garantie d'usage unique, et la manière d'ajouter un dialecte pour un autre moteur.

## Backend AWS {#aws-backend}

Pour fonctionner sur AWS plutôt que sur Azure, référencez `Authagonal.AwsProvider` et enregistrez l'ensemble AWS **avant** `AddAuthagonal()` : ce sont ces enregistrements qui font que `AddAuthagonal()` saute son câblage vers Azure Table Storage :

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Les tables DynamoDB reproduisent une à une la structure Azure et sont garanties au démarrage (opération idempotente, sans effet lorsqu'elles ont déjà été provisionnées par Terraform). Les identifiants sont résolus via la chaîne AWS standard (variables d'environnement / rôle d'instance EC2 / IRSA) : il n'y a donc pas de choix entre chaîne de connexion et identité managée, et aucune configuration `Storage:*` n'est nécessaire.

> ⚠️ **Clés DataProtection dans S3.** Sans client S3 ni bucket, le trousseau de clés Data Protection d'ASP.NET Core est conservé en mémoire, ce qui convient à un nœud unique en développement ; mais en production, les cookies et les jetons antifalsification cessent de fonctionner après un redémarrage et d'un nœud à l'autre. Passez toujours le client S3 et le bucket pour un déploiement AWS de production.

## SPA de connexion (npm) {#login-spa-npm}

L'interface de connexion est publiée sous forme de paquet npm pour la personnalisation :

```bash
npm install @authagonal/login react react-dom react-router
```

Le paquet contient du JS et du CSS compilés : importez directement les composants et les styles dans votre propre application React. Voir [Serveur personnalisé](custom-server) pour un guide complet.

`react`, `react-dom` et `react-router` sont des dépendances **peer** : le build les externalise, de sorte que les composants utilisent les exemplaires de votre application plutôt que les leurs. C'est ce qui permet aux pages exportées d'appeler `useNavigate` dans votre `<BrowserRouter>` et d'exécuter leurs hooks sur l'instance React qui les rend. Installez-les à côté du paquet ; ne le laissez pas apporter les siens.

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

Si votre SPA appelle des API avec un bearer token, conservez le jeton dans un BFF plutôt que dans le navigateur. Le BFF est publié sous forme de paquet NuGet (`Authagonal.Bff`) et de paquet npm (`@authagonal/bff`) ; aucun des deux ne fait partie de l'image du serveur. Voir [Backend-for-Frontend](bff).

## Checklist de sécurité pour la production {#production-security-checklist}

Avant d'exposer Authagonal à du trafic réel, vérifiez les points suivants. Chacun est détaillé sur la page [Configuration](configuration).

- **Utilisez un runtime .NET corrigé : 9.0.18 ou 10.0.10 au minimum.** Les correctifs de GHSA-37gx-xxp4-5rgx et GHSA-w3x6-4m5h-cxqf (une boucle infinie, et une paire XXE / épuisement de ressources, dans `System.Security.Cryptography.Xml`, toutes deux atteignables depuis l'endpoint ACS SAML **anonyme**) sont livrés dans le framework partagé, et non dans un paquet qu'Authagonal pourrait référencer : rien dans votre graphe de dépendances ne peut donc les garantir. Authagonal journalise un message `Critical` au démarrage lorsque le runtime en cours d'exécution est sous le seuil ; définissez `Auth:RequireMinimumRuntime = true` pour qu'il refuse de démarrer à la place. Les images de conteneur publiées utilisent déjà un runtime égal ou supérieur au seuil.
- **Placez Authagonal derrière un proxy qui termine TLS, et déclarez-le.** Authagonal doit se trouver derrière un reverse proxy / ingress qui termine TLS (ou terminer TLS lui-même). HSTS n'est émis qu'en HTTPS et `/connect/*` refuse le texte en clair : le proxy doit donc transmettre `X-Forwarded-Proto: https`, et cet en-tête est ignoré tant que vous ne définissez pas `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) avec le CIDR ou l'adresse de votre proxy. Utilisez `["0.0.0.0/0", "::/0"]` si le proxy n'a pas d'adresse fixe et que rien d'autre ne peut atteindre le processus. `ForwardedHeaders:ForwardLimit` vaut `1` par défaut (seul le dernier saut est approuvé).
- **Définissez `SecretProvider:VaultUri`.** Le fournisseur de secrets par défaut stocke en **texte en clair** : sans Key Vault, les secrets des clients OIDC amont et les graines TOTP / MFA sont stockés en clair dans Table Storage (et dans les sauvegardes). Configurez Key Vault pour tout déploiement de production.
- **Verrouillez l'API d'administration.** `AdminApi:Enabled` vaut **true** par défaut. Le scope d'administration (`AdminApi:Scope`, par défaut `authagonal-admin`) donne la gestion complète et l'emprunt d'identité des utilisateurs. Restreignez au niveau réseau l'accès aux routes d'administration `/api/v1/*` et contrôlez strictement à qui le scope d'administration est délivré, ou définissez `AdminApi:Enabled = false` si vous ne l'utilisez pas.
- **Protégez les endpoints internes.** Définissez `Cluster:Secret` pour que l'endpoint interne `/_internal/backchannel-logout` exige l'en-tête `X-Cluster-Secret` (comparé en temps constant). Sans secret, l'endpoint n'autorise **personne** et répond 404 : une adresse source n'est pas un identifiant, et le loopback est précisément ce que présente un reverse proxy installé sur le même hôte pour chaque requête qu'il transmet. `Cluster:AllowLoopbackWithoutSecret` réadmet un pair dont l'adresse est loopback avant le traitement des en-têtes transférés, pour le développement local uniquement. Rien dans le produit livré n'appelle cet endpoint, donc ce refus par défaut ne casse aucun flux natif ; définissez le secret si vous construisez dessus votre propre diffusion de pod à pod.
- **Chiffrez les sauvegardes.** Avec le fournisseur de secrets en texte clair, les sauvegardes contiennent des secrets. La table `SigningKeys` est exclue des sauvegardes par défaut ; si vous l'incluez via `Backup:IncludeSigningKeys`, la cible de sauvegarde doit être chiffrée au repos. Voir [Sauvegarde et restauration](backup-restore).

## Outil de migration {#migration-tool}

Pour migrer depuis Duende IdentityServer + SQL Server :

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Voir [Migration](migration) pour plus de détails.
