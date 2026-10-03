---
layout: default
title: Sauvegarde et restauration
locale: fr
---

# Sauvegarde et restauration

Authagonal fournit deux outils en ligne de commande pour sauvegarder et restaurer les données d'Azure Table Storage. Ce sont deux applications console .NET situées dans le répertoire `tools/`, et toutes deux sont de fines surcouches du paquet NuGet `Authagonal.Backup`. Les hôtes qui ont besoin de sauvegardes planifiées, multi-locataires ou ailleurs que sur un système de fichiers peuvent utiliser directement la bibliothèque (voir [Utiliser la bibliothèque](#using-the-library)).

## Sauvegarde {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Options {#options}

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaîne de connexion Azure Table Storage (ou définissez la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Répertoire de sortie (par défaut : `./backups`) |
| `--incremental` | Ne sauvegarder que les entités modifiées depuis la dernière sauvegarde |
| `--tables <t1,t2,...>` | Liste de tables séparées par des virgules (par défaut : toutes les tables Authagonal) |
| `--prefix <prefix>` | Préfixe des noms de table (pour un stockage multi-locataire) |
| `--gzip` | Compresser les fichiers de sauvegarde avec gzip (`.jsonl.gz`) |
| `--encryption-key <base64>` | Clé de chiffrement de clés AES-256 de 32 octets. Chiffre chaque fichier de données. Conservez-la **en dehors** de la cible de sauvegarde. Lit aussi `BACKUP_ENCRYPTION_KEY` (à privilégier ; voir ci-dessous). |
| `--manifest-key <base64>` | Clé HMAC d'au moins 32 octets. Signe le manifeste afin que la restauration puisse prouver que les hachages enregistrés n'ont pas été réécrits en même temps que les fichiers. Conservez-la **en dehors** de la cible de sauvegarde. Lit aussi `BACKUP_MANIFEST_KEY` (à privilégier ; voir ci-dessous). |
| `--dry-run` | Afficher ce qui serait sauvegardé, sans rien écrire |

### Format de sortie {#output-format}

Chaque sauvegarde crée un répertoire horodaté :

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

Avec `--prefix`, les sauvegardes sont imbriquées un niveau plus bas, sous le préfixe : `backups/acmecorp/20260329-120000/`.
C'est ce qui évite que les sauvegardes complètes de deux locataires, écrites dans le même répertoire `--output` au cours de la même
seconde, n'entrent en collision. L'identifiant de sauvegarde lui-même reste un simple horodatage `yyyyMMdd-HHmmss[-incr]` d'une
résolution d'une seconde, sans préfixe ; sans cette imbrication, deux préfixes sauvegardés au cours de la
même seconde recevraient donc le même identifiant, et par conséquent le même répertoire. Faites pointer `--input` vers le
répertoire imbriqué pour restaurer à partir de celui-ci (`--input backups/acmecorp/20260329-120000`) ; les exécutions sans préfixe ne
sont pas concernées et conservent la structure à plat présentée ci-dessus.

Chaque fichier `.jsonl` contient un objet JSON par ligne (un par entité de table). Avec `--gzip`, les fichiers sont compressés au format `.jsonl.gz`. Le fichier `_manifest.json` enregistre l'identifiant de la sauvegarde, l'horodatage, le mode (`full` ou `incremental`), la compression, le point de reprise incrémental, le nombre d'entités par table, le nombre de marqueurs de suppression, les tables éventuellement lues via le journal des modifications (`ChangeLogTables` ; null signifie une couverture complète par balayage), ainsi que les hachages SHA-256 des fichiers pour la vérification d'intégrité.

Les sauvegardes incrémentales écrivent aussi un fichier `_tombstones.jsonl(.gz)` qui enregistre les suppressions intervenues depuis le point de reprise : une ligne par ligne supprimée, avec `Table`, `PartitionKey`, `RowKey` et `DeletedAt`. La restauration les rejoue afin que les lignes supprimées ne réapparaissent pas (voir [Rejeu des marqueurs de suppression](#tombstone-replay)).

Les valeurs des entités font l'aller-retour à l'identique : chaque ligne sauvegardée porte un marqueur de format `"@v"` et une annotation explicite `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) pour chaque colonne que JSON ne peut pas représenter sans ambiguïté : la restauration réécrit donc les types d'origine plutôt que des valeurs converties en chaînes ou dont le type a été redéduit.

### Vérification d'intégrité {#integrity-verification}

Chaque manifeste de sauvegarde comporte un dictionnaire `FileHashes` qui associe chaque nom de fichier à son hachage SHA-256. Pendant la restauration, chaque fichier est vérifié par rapport à son hachage enregistré (à partir de la même lecture que celle dont les entités sont appliquées, de sorte que les octets vérifiés sont bien ceux qui sont écrits) avant que la moindre de ses données n'atteigne une table. Un fichier qui échoue à la vérification, un fichier de données absent du manifeste ou un fichier listé dans le manifeste mais absent du stockage interrompent tous la restauration. Les sauvegardes écrites avant l'existence du hachage d'intégrité (sans `FileHashes`) ne peuvent pas être vérifiées et sont refusées, sauf avec `--allow-unverified`. La vérification peut être désactivée par programmation via `RestoreOptions.VerifyIntegrity` (par défaut `true`).

### Passer les clés par variable d'environnement, pas sur la ligne de commande {#pass-the-keys-by-environment-variable-not-on-the-command-line}

Les deux outils lisent `BACKUP_ENCRYPTION_KEY` et `BACKUP_MANIFEST_KEY`, et une sauvegarde planifiée doit les utiliser.

Une option devient la ligne de commande du processus. Dans Kubernetes, cela signifie que la spécification du CronJob contient littéralement la
clé de chiffrement de clés en base64 et la clé HMAC : toute personne disposant de `get`/`list` sur les cronjobs ou les pods de cet espace de noms peut lire les deux
avec `kubectl get cronjob -o yaml`, soit un ensemble de principaux bien plus large que les détenteurs du Secret, et un droit
couramment accordé aux tableaux de bord en lecture seule et aux comptes de service de CI. Les mêmes valeurs sont visibles dans
`/proc/<pid>/cmdline` pour tout processus du nœud, ainsi que dans l'historique du shell ou le journal de CI qui a assemblé la
commande. `--connection-string` dispose d'une voie par variable d'environnement exactement pour cette raison ; ce n'était pas le cas des deux clés qui protègent
l'archive.

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

Une option l'emporte toujours si les deux sont définies : une restauration ponctuelle interactive ne demande donc aucun changement.

Les hachages établissent que l'archive correspond au manifeste, et non que l'un ou l'autre est authentique : le manifeste se trouve sur la même cible que les données, si bien que quiconque peut réécrire `Clients.jsonl.gz` peut aussi réécrire la ligne qui enregistre son hachage. `--manifest-key` comble cette faille : la sauvegarde calcule un HMAC du manifeste, la restauration le vérifie, et la clé réside à un endroit que l'auteur de la sauvegarde ne peut pas atteindre. **La restauration échoue par défaut en mode fermé** : sans `--manifest-key`, elle refuse au lieu d'avertir, et `--allow-unauthenticated-manifest` est la dérogation explicite pour les archives écrites avant la signature des manifestes.

### Sauvegardes incrémentales {#incremental-backups}

Passez `--incremental` pour ne sauvegarder que les entités modifiées depuis la dernière sauvegarde réussie. L'outil filtre sur la propriété intégrée `Timestamp` d'Azure Table Storage et suit le point haut dans un fichier `.lastbackup` du répertoire de sortie.

En l'absence de fichier `.lastbackup`, la première exécution incrémentale effectue une sauvegarde complète.

Chaque filtre incrémental sur `Timestamp` retranche une petite marge de sécurité (`BackupDefaults.WatermarkSkewMargin`, 5 minutes) avant de filtrer. Le point de reprise provient de l'horloge de l'appelant, alors que les horodatages des lignes sont apposés par le service de stockage : une modification validée à l'intérieur du décalage d'horloge échapperait sinon à cette exécution et à toutes les suivantes. Relire la marge coûte quelques lignes en double à chaque exécution, que la sémantique d'upsert de la restauration dédoublonne.

### Tables par défaut {#default-tables}

L'outil de sauvegarde inclut par défaut toutes les tables Authagonal (`BackupDefaults.Tables`) :

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`, `UserRoles` et `UpstreamRefreshTokens` font délibérément partie de l'ensemble : sans elles, un déploiement restauré serait discrètement plus faible que celui qui a été sauvegardé (les clients agents perdent leur plafond et leurs contrôles de consentement, les rôles sont définis mais personne ne les détient, les jetons de rafraîchissement amont disparaissent).

Les tables transitoires (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) sont exclues par défaut, car leurs entrées sont bornées par la durée de vie des jetons ; incluez-les explicitement avec `--tables` si nécessaire. La table de journal des modifications `Tombstones` est gérée à part par le moteur de sauvegarde et ne doit pas être listée.

### Les clés de signature sont exclues par défaut {#signing-keys-are-excluded-by-default}

La table `SigningKeys` figure dans la liste de tables par défaut, mais elle est **filtrée des sauvegardes par défaut** (`BackupOptions.IncludeSigningKeys`, par défaut `false` ; la CLI ne l'active jamais). Pour les hôtes qui utilisent la source de clés locale (stockée en table), cette table contient la **clé privée** de signature des JWT, et l'écrire dans un fichier de sauvegarde en clair permettrait à quiconque lit la sauvegarde de forger des jetons. Cela vaut pour **tous** les hôtes : la signature des JWT n'est pas déléguée à Vault Transit, il n'existe donc aucune configuration dans laquelle la table `SigningKeys` ne contient pas de clé privée.

> ⚠️ N'activez `BackupOptions.IncludeSigningKeys` que si la cible de sauvegarde est elle-même chiffrée au repos et soumise à un contrôle d'accès. Il en va de même pour le reste de la sauvegarde : avec le fournisseur de secrets par défaut en **texte clair**, les sauvegardes contiennent aussi en clair les secrets des clients OIDC amont et les graines TOTP / MFA. Voir [Configuration → Fournisseur de secrets](configuration#secret-provider).

### `--tables` désigne des tables de l'ensemble sauvegardé {#--tables-names-tables-from-the-backup-set}

Seules les tables de l'ensemble déclaré (`BackupDefaults.Tables`, ou `KnownTables` ci-dessous) peuvent être désignées. Une table qui n'en fait pas partie est refusée d'emblée au lieu de
produire une archive que la restauration rejetterait. La liste autorisée de la restauration est ce même ensemble : une archive désignant
autre chose pourrait être écrite, hachée et signée, puis ne jamais être restaurée. Les tables transitoires (entrées de jetons
révoqués, compteurs de limitation de débit) sont délibérément exclues : elles expirent d'elles-mêmes, et restaurer des lignes périmées
ne sert à rien.

## Restauration {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Options {#options-1}

| Option | Description |
|---|---|
| `--connection-string <conn>` | Chaîne de connexion Azure Table Storage (ou définissez la variable d'environnement `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Répertoire de sauvegarde à partir duquel restaurer |
| `--mode <mode>` | Mode de restauration : `upsert` (par défaut), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Liste des tables à restaurer, séparées par des virgules (par défaut : tous les fichiers `.jsonl`/`.jsonl.gz` de la sauvegarde) |
| `--prefix <prefix>` | Préfixe des noms de table (pour un stockage multi-locataire) |
| `--clean-env <env>` | Avec `--mode clean`, n'effacer que les lignes de cet environnement (préfixe de PartitionKey `<env>|`) |
| `--allow-clean-from-incremental` | Autoriser `--mode clean` à partir d'une sauvegarde incrémentale |
| `--allow-clean-all-envs` | Autoriser `--mode clean` sans `--clean-env`, ce qui vide toute la table |
| `--encryption-key <base64>` | La clé de chiffrement de clés de 32 octets avec laquelle la sauvegarde a été écrite. Obligatoire pour une archive chiffrée. Lit aussi `BACKUP_ENCRYPTION_KEY`. |
| `--manifest-key <base64>` | La clé HMAC avec laquelle la sauvegarde a été signée. **Obligatoire**, sauf avec `--allow-unauthenticated-manifest`. Lit aussi `BACKUP_MANIFEST_KEY`. |
| `--allow-unauthenticated-manifest` | Restaurer sans `--manifest-key`, en acceptant des hachages qui détectent la corruption mais pas la falsification |
| `--allow-unverified` | Restaurer une sauvegarde dont le manifeste ne contient aucun hachage de fichier |
| `--dry-run` | Afficher ce qui serait restauré, sans rien écrire |

### Modes de restauration {#restore-modes}

| Mode | Comportement |
|---|---|
| `upsert` | Insère ou remplace chaque entité. Les données existantes sont écrasées. |
| `merge` | Insère ou fusionne. Les propriétés existantes absentes de la sauvegarde sont conservées. |
| `clean` | Supprime toutes les données existantes de chaque table avant de restaurer. |

Les fichiers de sauvegarde compressés avec gzip (`.jsonl.gz`) sont détectés et décompressés automatiquement ; aucune option supplémentaire n'est nécessaire.

### Rejeu des marqueurs de suppression {#tombstone-replay}

Après les fichiers de données, la restauration applique le fichier `_tombstones` de la sauvegarde : chaque clé enregistrée est supprimée des tables restaurées (`RestoreOptions.ApplyTombstones`, par défaut `true`). Les suppressions d'une sauvegarde incrémentale font tout autant partie de son état que ses upserts ; les ignorer ferait réapparaître des lignes supprimées, y compris celles effacées au titre du RGPD, lors de la restauration d'une séquence composée d'une sauvegarde complète et de sauvegardes incrémentales. Les sauvegardes complètes ne comportent pas de fichier de marqueurs de suppression. Lorsque vous restaurez une sauvegarde complète suivie de sauvegardes incrémentales, appliquez celles-ci de la plus ancienne à la plus récente, afin qu'une recréation ultérieure soit appliquée après une suppression antérieure. Le hachage du fichier de marqueurs de suppression est vérifié par rapport au manifeste, comme pour les fichiers de données.

### Aller-retour exact des types {#exact-type-round-trip}

Les lignes écrites avec le marqueur de format `"@v"` portent des annotations de type EDM explicites : la restauration reconstitue donc exactement les types de colonne d'origine (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`) ; une chaîne sans annotation est restaurée en tant que chaîne. Les anciens fichiers de sauvegarde dépourvus du marqueur se rabattent sur une déduction fondée sur la forme des valeurs, conservée uniquement pour que les anciennes sauvegardes restent restaurables (cette déduction peut attribuer un mauvais type aux colonnes de chaînes ressemblant à un GUID ou à une date).

### Codes de sortie {#exit-codes}

| Code | Signification |
|---|---|
| `0` | Succès |
| `1` | Erreur (arguments manquants, entrée invalide) |
| `2` | Succès partiel (certaines entités ont rencontré des erreurs) |

### Un hôte avec ses propres tables : `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` et `RestoreOptions.KnownTables` (tous deux `string[]?` ; null signifie `BackupDefaults.Tables`) déclarent l'ensemble des tables qu'une archive de votre déploiement peut légitimement désigner. Un hôte qui stocke ses propres données à côté de celles d'Authagonal et sauvegarde les deux dans une même archive doit le définir ; sinon, chaque sauvegarde désignant ces tables est refusée d'emblée (`BackupService.cs:48`) et chaque restauration refuse l'archive (`RestoreService.cs:17,167`).

- L'hôte déclare l'ensemble à l'avance. Il n'est jamais déduit de l'archive, et c'est tout l'intérêt : ce n'est pas à une archive de choisir quelles tables une restauration écrit.
- Passez le **même** ensemble aux deux options. Une sauvegarde effectuée avec un ensemble plus large ne se restaure qu'au moyen d'une restauration qui déclare le même ensemble.

## Utiliser la bibliothèque {#using-the-library}

Le paquet NuGet `Authagonal.Backup` expose les mêmes opérations par programmation, pour des services d'arrière-plan ou une orchestration personnalisée :

| Type | Rôle |
|---|---|
| `BackupService` | Exécute une sauvegarde complète ou incrémentale sur un `TableServiceClient`, en écrivant vers un `IBackupTarget` |
| `RestoreService` | Vérifie les hachages et réécrit une sauvegarde dans Table Storage |
| `MergeService` | Combine en flux une sauvegarde complète et ses sauvegardes incrémentales (avec leurs marqueurs de suppression) en une vue de l'état courant |
| `RollupService` | Consolide des sauvegardes incrémentales en une nouvelle sauvegarde complète, en supprimant éventuellement les sauvegardes d'origine |
| `BackupOptions` / `RestoreOptions` | Configuration propre à chaque exécution |
| `BackupDefaults` | Liste de tables par défaut et préréglages du journal des modifications |
| `IBackupSource` / `IBackupTarget` | Abstractions de stockage ; `FileSystemBackupSource` / `FileSystemBackupTarget` sont les implémentations intégrées. Implémentez `IBackupTarget` pour écrire vers un stockage blob ou ailleurs. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Sauvegardes incrémentales pilotées par le journal des modifications {#change-log-driven-incrementals}

Azure Table Storage n'indexe que `PartitionKey` et `RowKey` : une sauvegarde incrémentale filtrée sur `Timestamp` reste donc un balayage complet de chaque table. Pour l'éviter, les stores d'Authagonal enregistrent chaque modification dans un journal des modifications via le point d'extension `IChangeWriter` (`Authagonal.Core`), implémenté pour Azure par `TableChangeWriter` (`Authagonal.AzureProvider`). Il s'agit d'une seule table physique, toujours nommée `Tombstones` : PK = le nom logique de la table, RK = `"{pk}|{rk}"`, une colonne `Op` valant `"U"` (upsert) ou `"D"` (suppression), et des colonnes `OrigPK`/`OrigRK` qui font foi (un `|` à l'intérieur de la PartitionKey d'origine rend ambigu le découpage de la RowKey composite : le lecteur de sauvegarde se fie donc aux colonnes et ne recourt au découpage que pour les anciennes lignes). Chaque clé correspond à une seule ligne (upsert avec remplacement), si bien que la dernière opération d'une fenêtre de sauvegarde l'emporte.

Lorsque la voie du journal des modifications est activée, une sauvegarde incrémentale énumère les entrées `Op = "U"` du journal des modifications d'une table depuis le point de reprise et lit ponctuellement chaque ligne en vigueur au lieu de balayer la table. La fonctionnalité est **optionnelle et désactivée par défaut** : `BackupOptions.ChangeLoggedTables` null ou vide signifie que toutes les tables restent sur la voie du balayage ; le mécanisme est donc livré inerte jusqu'à une activation délibérée (un déploiement ne peut pas manquer sans avertissement des lignes modifiées par du code antérieur à la capture). Deux préréglages :

| Préréglage | Contenu |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Les tables dont les écritures sont intégralement capturées par le journal des modifications : `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Le même ensemble plus `Users`. Les écritures d'état de connexion de Users ne sont délibérément pas capturées (chemin critique, faible valeur) : ce préréglage n'est donc **sûr que si vous exécutez aussi le balayage complet de secours décrit ci-dessous** |

La propriété `ChangeLogTables` du manifeste liste les tables qu'une exécution a lues via le journal des modifications ; null ou vide signifie que l'exécution a eu une couverture complète par balayage (une sauvegarde complète, une sauvegarde incrémentale par simple balayage ou un balayage de secours).

### Balayage complet de secours {#full-scan-backstop}

Comme la capture par le journal des modifications peut manquer des écritures (champs d'état de connexion, auteurs d'écritures qui ne passent pas par les stores, pods exécutant du code antérieur à la capture pendant un déploiement), associez les sauvegardes incrémentales par journal des modifications à un nouveau balayage complet périodique. Définissez `BackupOptions.WatermarkOverride` sur l'horodatage du dernier balayage à couverture complète et laissez `ChangeLoggedTables` non défini pour cette exécution : la sauvegarde incrémentale filtre alors sur `Timestamp` sur toute la fenêtre écoulée depuis ce balayage, et récupère tout ce que le journal des modifications n'a jamais capturé. Un balayage de secours quotidien, en complément de sauvegardes incrémentales horaires par journal des modifications, constitue une cadence raisonnable. Les suppressions sont la seule catégorie de modification qui ne se rattrape pas d'elle-même (un balayage des lignes en vigueur ne peut pas voir une ligne qui a disparu) : c'est pourquoi les stores écrivent le marqueur de suppression **avant** de supprimer la ligne de données.

Tous les filtres incrémentaux, balayage de secours compris, retranchent `BackupDefaults.WatermarkSkewMargin` (5 minutes) du point de reprise ; les appelants qui purgent le journal des modifications après une sauvegarde doivent borner la purge avec la même marge, faute de quoi ils suppriment des lignes dont la prochaine exécution a encore besoin.

### Consolidations {#rollups}

`RollupService.RollupAsync` fusionne une sauvegarde complète et ses sauvegardes incrémentales en une nouvelle sauvegarde complète ; `RollupAndCleanAsync` supprime en plus les sauvegardes d'origine ensuite. Le paramètre optionnel `newBackupId` nomme le résultat (null produit un identifiant fondé sur l'horodatage) ; un instantané conservé à part (par exemple une consolidation hebdomadaire) doit passer son identifiant ici, car la rétention fondée sur les identifiants liste les identifiants physiques des sauvegardes, et non les manifestes.

Lors d'une fusion, les marqueurs de suppression s'appliquent selon l'ordre des horodatages : une suppression n'efface une ligne capturée que si le `Timestamp` de la ligne n'est pas postérieur au `DeletedAt` du marqueur. Une clé supprimée tôt dans la fenêtre puis recréée plus tard a à la fois un marqueur de suppression et une capture en vigueur, et la ligne recréée survit à la consolidation. Les anciens marqueurs de suppression dépourvus de `DeletedAt` suppriment sans condition.

## Docker {#docker}

L'outil de sauvegarde est livré avec un Dockerfile (`tools/Authagonal.Backup/Dockerfile`) pour l'exécuter en CI ou sans installer le SDK .NET :

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

L'outil de restauration n'a pas d'image ; exécutez-le avec le SDK .NET (`dotnet run --project tools/Authagonal.Restore`).

## Planifier les sauvegardes {#scheduling-backups}

En production, exécutez l'outil de sauvegarde selon une planification (par exemple une sauvegarde complète quotidienne et une sauvegarde incrémentale horaire) :

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Les hôtes qui intègrent la bibliothèque exécutent généralement des sauvegardes incrémentales horaires avec la voie du journal des modifications activée, un balayage complet de secours quotidien et des consolidations périodiques pour borner la chaîne des sauvegardes incrémentales.
