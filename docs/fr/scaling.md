---
layout: default
title: Mise à l'échelle
locale: fr
---

# Mise à l'échelle

Authagonal est conçu pour monter en charge verticalement comme horizontalement, sans configuration particulière.

## Sans état par conception {#stateless-by-design}

Tout l'état persistant est conservé dans le stockage sous-jacent (Azure Table Storage, DynamoDB avec le backend AWS, ou PostgreSQL avec le backend SQL auto-hébergé). Il n'existe aucun état en mémoire du processus qui nécessiterait des sessions persistantes (sticky sessions) ou une coordination entre instances :

- **Clés de signature** : chargées depuis Table Storage, rafraîchies toutes les heures
- **Codes d'autorisation et jetons d'actualisation** : stockés dans Table Storage, avec usage unique imposé
- **Prévention du rejeu SAML** : identifiants de requête suivis dans Table Storage avec suppression atomique
- **État OIDC et vérificateurs PKCE** : stockés dans Table Storage
- **Configuration des clients et des fournisseurs** : récupérée à chaque requête depuis Table Storage

## Chiffrement des cookies (Data Protection) {#cookie-encryption-data-protection}

Le trousseau de clés Data Protection d'ASP.NET Core protège le cookie d'authentification ; toutes les instances doivent donc en partager un seul. Il est persisté automatiquement, dans cet ordre :

1. `DataProtection:BlobUri`, s'il est défini (un blob explicite, authentifié avec `DefaultAzureCredential`).
2. Un conteneur `dataprotection` dans le compte désigné par `Storage:ConnectionString`, sauf s'il s'agit d'Azurite.
3. Avec l'identité managée (`Storage:TableServiceUri`), le point de terminaison blob associé du même compte, `https://{account}.blob.…/dataprotection/keys.xml`. L'identité doit disposer du rôle Storage Blob Data Contributor sur le compte.

Seul un point de terminaison de table non reconnu (Azurite, émulateurs à adressage par chemin) se rabat sur le stockage de fichiers propre à la machine, qui est éphémère et propre à chaque pod : un redémarrage déconnecte tout le monde et les réplicas ne peuvent pas lire les cookies les uns des autres. La vérification au démarrage journalise une entrée `Critical` lorsque cela se produit.

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Avec le backend AWS, passez un client S3 et un bucket à `AddAuthagonalAwsStorage` pour persister le trousseau dans S3 ; sans cela, le trousseau reste en mémoire et les cookies cessent de fonctionner au redémarrage et d'un nœud à l'autre. Voir [Installation → Backend AWS](installation#aws-backend). Avec le backend SQL, le trousseau est persisté par `AddAuthagonalPostgres` / `AddAuthagonalSqlite`.

Persister n'est pas chiffrer : le trousseau est du XML en clair, sauf si `DataProtection:KeyVaultKeyId` ou `DataProtection:CertificateThumbprint` est défini. Au démarrage, un trousseau non chiffré et encore dépourvu de clés est refusé, et un trousseau qui contient déjà des clés démarre avec une entrée de journal `Critical` (`DataProtection:AllowUnencryptedKeyRing=true` l'accepte délibérément). Voir [Configuration](configuration) pour le tableau complet des paramètres `DataProtection:*`.

## Caches par instance {#per-instance-caches}

Un petit nombre de valeurs très lues et rarement modifiées sont mises en cache en mémoire sur chaque instance afin de réduire les allers-retours vers Table Storage :

| Données | Durée de cache | Effet d'une donnée périmée |
|---|---|---|
| Documents de découverte OIDC | 60 minutes (configurable) | Prise en compte retardée de la rotation des clés de l'IdP |
| Métadonnées de l'IdP SAML | 60 minutes (configurable) | Idem |
| Origines CORS autorisées | 60 minutes (configurable) | Les nouvelles origines peuvent mettre jusqu'à une heure à être prises en compte |

Ces caches sont acceptables en production. Toutes les durées sont configurables via la section de configuration `Cache` ; voir [Configuration](configuration). Si vous avez besoin d'une prise en compte immédiate, redémarrez les instances concernées.

## Limitation de débit {#rate-limiting}

Les points de terminaison exposés aux abus (inscription par IP, réinitialisation de mot de passe par adresse e-mail cible, SCIM par client, enregistrement dynamique de clients par IP ; voir [Configuration → Limitation de débit](configuration#rate-limiting)) sont protégés par un limiteur de débit intégré.

Par défaut, les limites sont appliquées **en mémoire, nœud par nœud**, derrière l'abstraction `IRateLimiter` ; avec N instances, le plafond effectif est donc N fois la valeur configurée. C'est délibéré : le limiteur est un garde-fou contre l'emballement d'un abus sur un seul nœud, et la limite globale qui fait foi relève de la périphérie (WAF / ingress / CDN), qui voit tout le trafic avant sa répartition de charge.

Ce compromis convient aux limites de volume, mais pas à un cas précis : un budget qui protège un **secret devinable**. Le `user_code` du flux device est une courte chaîne tirée d'un petit alphabet, et la limite de tentatives est la seule chose qui sépare un attaquant d'un code donnant accès à une session active. Un plafond qui se multiplie par le nombre de réplicas n'a pas la bonne forme dans ce cas, et il fait de la borne réelle une propriété de votre configuration d'ingress plutôt que du serveur.

Définissez **`Auth:DurableRateLimiting=true`** pour placer les compteurs dans le stockage que vous exploitez déjà, afin que tous les réplicas partagent un même budget. Cela coûte un aller-retour vers le stockage à chaque vérification de limite, utilise des fenêtres fixes (un budget de N autorise jusqu'à 2N à cheval sur une limite de fenêtre) et échoue en mode ouvert (laisse passer les requêtes) si le stockage est injoignable ; ce mécanisme vient donc s'ajouter à la règle de périphérie plutôt que la remplacer. Les lignes de compteurs sont nettoyées automatiquement sur les trois backends. Voir [Configuration → Limites à l'échelle du cluster](configuration#cluster-wide-limits-authdurableratelimiting).

## Clustering {#clustering}

Plusieurs instances se coordonnent au moyen d'une **élection de leader** et d'un **bus d'événements inter-nœuds**, tous deux reposant sur des backends interchangeables :

- **Élection de leader** : une élection fondée sur un bail (`Cluster:LeaseTtlSeconds`, 30 s par défaut, renouvelé à peu près à la moitié de cet intervalle). Un seul nœud détient le bail ; le rôle de leader est transféré automatiquement lorsque le leader disparaît. Les tâches réservées au leader ne s'exécutent que sur lui : la *désactivation* des clés de signature à leur expiration (lorsque `Auth:KeyRotationEnabled` est activé), le balayage de réconciliation des octrois (backend Azure uniquement), le remplissage rétroactif du chiffrement au repos (lorsque `Auth:AtRestBackfillEnabled` est activé ; un nœud non leader attend brièvement d'obtenir le rôle de leader, puis abandonne) et le balayage des compteurs de limitation de débit (backend Azure avec `Auth:DurableRateLimiting`). Avec `Cluster:Enabled=false`, le nœud unique est leader en permanence ; un déploiement autonome exécute donc toujours toutes ces tâches.
- **Bus d'événements** : notifications inter-nœuds (par exemple l'invalidation de cache dans les hôtes multi-locataires), interrogées toutes les `Cluster:PollIntervalSeconds` (3 s par défaut).

Chaque instance génère au démarrage un identifiant de nœud aléatoire de 12 caractères hexadécimaux pour s'identifier ; il n'est pas persisté.

### Backends {#backends}

**Par défaut, tout se passe en mémoire du processus** : un nœud unique est toujours son propre leader et les événements restent locaux, ce qui est correct pour une instance unique, sans aucune configuration. Les déploiements multi-nœuds substituent un vrai backend via le callback `configureClustering` de `AddAuthagonal` :

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` n'enregistrent que le bus d'événements et conservent le bail en mémoire (toujours leader) ; utilisez-les sur les nœuds qui doivent recevoir les événements du cluster mais ne doivent jamais briguer le rôle de leader.

> **Remarque :** avec le comportement par défaut en mémoire sur plusieurs nœuds, *chaque* nœud se croit leader. C'est sans conséquence pour la plupart des charges de travail, mais activez un véritable backend de bail avant d'activer `Auth:KeyRotationEnabled` sur plusieurs instances.

La **génération** des clés de signature est distincte de cette désactivation réservée au leader, et n'est pas pilotée par elle : chaque nœud appelle `EnsureActiveKeyAsync` au démarrage et à chaque rafraîchissement `Auth:SigningKeyCacheRefreshMinutes` ; avec `KeyRotationEnabled` désactivé, ce qui est le cas par défaut, le renouvellement à l'expiration des 90 jours est donc entièrement assuré par cette voie. La génération prend son propre bail de cluster de courte durée, si bien qu'il n'y a qu'un seul rédacteur partout où un véritable backend de bail est configuré. Avec le comportement par défaut en mémoire sur plusieurs nœuds, cette coordination n'existe pas, et deux nœuds qui rencontrent au même moment une clé expirée peuvent en générer une chacun ; les deux se retrouvent dans le JWKS et les jetons signés par l'une ou l'autre sont vérifiés avec succès, mais la clé signalée comme active peut osciller. C'est une raison de plus de configurer un véritable backend de bail pour les déploiements multi-nœuds.

Voir la page [Configuration](configuration#cluster) pour l'ensemble des paramètres du cluster.

### Déploiements multi-locataires {#multi-tenant-deployments}

En mode multi-locataire (`AddAuthagonalCore()`), `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` et les services d'initialisation de la configuration (clients, fournisseurs, scopes, rôles) ne sont pas enregistrés : ils font partie de la composition mono-locataire `AddAuthagonal()`, et c'est l'hôte qui gère ce travail locataire par locataire.

## Partition chaude de l'index des noms {#name-index-hot-partition}

La recherche par préfixe de nom dans l'administration repose sur les tables d'index `UserFirstNames` / `UserLastNames`, qui utilisent une **unique partition chaude**. À grande échelle, cela plafonne le débit d'écriture de l'index à environ 2 000 opérations par seconde, ce qui peut devenir un goulot d'étranglement pour la création et la mise à jour d'utilisateurs sous forte charge. Si vous n'exposez pas la recherche par nom dans l'administration, définissez `Storage:NameIndexesEnabled = false` pour supprimer entièrement ces écritures. Voir [Configuration](configuration).

## Proxy de confiance et points de terminaison internes {#trusted-proxy-and-internal-endpoints}

Lorsque plusieurs instances s'exécutent derrière un répartiteur de charge :

- **En-têtes de transfert** : la limitation de débit et le verrouillage de compte s'appuient sur l'IP du client, déterminée à partir de `X-Forwarded-For`. Définissez `ForwardedHeaders:KnownNetworks` sur le CIDR de votre ingress ou de vos pods afin que l'IP du client ne puisse pas être usurpée d'une instance à l'autre. `ForwardedHeaders:ForwardLimit` vaut `1` par défaut. Voir [Configuration](configuration#forwarded-headers-trusted-proxy).
- **Points de terminaison internes** : `/_internal/backchannel-logout` exige `Cluster:Secret` dans l'en-tête `X-Cluster-Secret` (comparé en temps constant). Sans lui, le point de terminaison n'autorise personne et répond 404 ; l'IP source n'est pas considérée comme un identifiant, car le loopback est ce que présente un reverse proxy sur le même hôte pour chaque requête relayée, et une plage privée correspond à toutes les charges de travail voisines d'un réseau de cluster partagé. `Cluster:AllowLoopbackWithoutSecret` est une option réservée au développement qui réadmet un pair loopback antérieur au relais. Le produit livré n'appelle jamais cette route (la diffusion de fin de session se fait en mémoire via `SessionTermination`) ; elle ne concerne donc qu'une diffusion que vous construisez vous-même.

## Recommandations de mise à l'échelle {#scaling-recommendations}

**Mise à l'échelle verticale** : augmentez le CPU et la mémoire d'une instance unique. Utile pour traiter davantage de requêtes simultanées par instance.

**Mise à l'échelle horizontale** : exécutez plusieurs instances derrière un répartiteur de charge. Aucune session persistante ni aucun cache partagé n'est requis. Chaque instance est entièrement indépendante.

**Mise à l'échelle jusqu'à zéro** : Authagonal prend en charge les déploiements pouvant descendre à zéro instance (par exemple Azure Container Apps avec `minReplicas: 0`). La première requête après une période d'inactivité subit un démarrage à froid de quelques secondes, le temps que le runtime .NET s'initialise et que les clés de signature soient chargées depuis le stockage.
