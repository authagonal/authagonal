---
layout: default
title: Organisations
locale: fr
nav_order: 14
---

# Organisations

Une organisation est un client à l'intérieur de votre locataire. Un même déploiement peut en servir un grand nombre : chacune a sa propre identité, ses propres membres et son propre `org_id` dans les jetons que reçoivent vos applications.

## Vue d'ensemble {#overview}

Avant les organisations, un enregistrement d'utilisateur portait une chaîne `OrganizationId` (écrite par le provisionnement TCC ou par la liaison d'un jeton SCIM), et cette chaîne était émise sous forme de revendication `org_id`. Il n'existait aucun endroit où dire ce qu'*était* l'organisation, qui en faisait partie, ni même s'il était possible de s'authentifier en son nom.

Une `Organization` lui donne un enregistrement : un identifiant opaque immuable, un slug immuable unique dans le locataire, un nom d'affichage, un indicateur d'activation, un ensemble de métadonnées et une surcharge de personnalisation. Une `OrganizationMembership` enregistre qui en fait partie, et c'est elle qui autorise réellement l'émission d'un jeton pour cette organisation.

**À quoi cela sert.** Un éditeur de logiciels dont le produit est déployé client par client (une instance d'application, une base de données, déterminée par le nom d'hôte) enregistre un locataire et une organisation par client. Son application lit `org_id` dans le jeton d'accès et refuse tout ce qui ne correspond pas à l'instance qu'elle sert. La décision de routage que l'application prenait elle-même est désormais prise par le serveur d'autorisation, et prouvée par une revendication signée.

**Le locataire reste la frontière d'isolation.** Une clé de signature, un émetteur, un store d'utilisateurs. Une organisation partitionne l'identité *à l'intérieur* de cette frontière ; elle n'en crée pas une seconde. Deux organisations d'un même locataire partagent un annuaire d'utilisateurs, et un utilisateur peut appartenir à plusieurs d'entre elles.

**Pas encore pris en charge :**

- **Pas de sélecteur d'organisation.** Un utilisateur qui appartient à plusieurs organisations, sur une requête qui n'en désigne aucune, reçoit `account_selection_required`, une erreur que la partie de confiance peut traiter en réessayant avec un paramètre. Il n'existe aucun écran hébergé qui lui demande de choisir.
- **Pas d'administration déléguée des organisations.** Aucune permission ne permet à l'administrateur propre d'un client de gérer ses membres.
- **Pas d'isolation SCIM par organisation.** Un jeton SCIM lié à une organisation (`ScimToken.OrganizationId`) étiquette les utilisateurs qu'il crée et, lorsque l'identifiant désigne une organisation réelle, en fait des membres actifs (voir [Appartenance issue d'un jeton SCIM](#membership-from-a-scim-token)). Les contrôles de propriété restent fondés sur le client OAuth, et non sur l'organisation : la liaison décide de l'étiquetage, pas de l'accès.
- **Pas de flux d'invitation dans cette bibliothèque.** Il n'y a ni endpoint d'invitation ni e-mail d'invitation. Un hôte écrit lui-même une appartenance `invited` ; les façons dont elle devient `active` sont décrites dans [Invitations](#invitations).
- **Pas de groupes limités à une organisation.** La revendication `groups` et l'appartenance aux groupes SCIM restent à l'échelle du locataire ; seuls les rôles sont propres à une organisation.
- **Pas d'événements webhook d'organisation, ni d'audit par organisation.** `IAuthHook` n'a aucun événement de cycle de vie des organisations (création, attribution ou révocation d'une appartenance), les contenus des hooks existants ne portent aucun `organizationId`, et le journal d'audit n'a ni colonne ni index d'organisation.
- **Les scopes soumis à des rôles sont filtrés sur les rôles du locataire à l'autorisation.** `Scope.AllowedRoles` est appliqué sur `/connect/authorize` aux rôles attribués directement au compte, avant que l'organisation ne soit résolue : un scope dont les `AllowedRoles` ne sont satisfaits que par un rôle propre à une organisation est donc écarté à l'autorisation, ou refusé avec `access_denied` si aucun scope demandé ne subsiste. Au rafraîchissement, le même contrôle s'exécute sur les rôles du sujet résolu, qui incluent bien ceux de l'organisation. Tant que les deux ne concordent pas, conditionnez les scopes à des rôles du locataire.
- **Pas de personnalisation par organisation dans cette bibliothèque.** `Organization.BrandingJson` est conservé pour que l'hôte le fusionne avec la personnalisation du locataire ; rien dans cette bibliothèque ne le lit. L'application de connexion affiche le nom de l'organisation (« Connexion à {name} ») lorsque le contenu de démarrage fourni par l'hôte comporte une `organization` (`{ id, slug, name }`) ; la bibliothèque elle-même ne résout aucune organisation avant l'authentification, sauf par le paramètre `organization`, une restriction de client à une seule entrée, une connexion limitée à une organisation, ou le `ITenantContext.OrganizationId` d'un hôte.
- **Pas d'API REST d'administration des organisations.** La surface est constituée de `IOrganizationStore` et `IOrganizationMembershipStore` ; un hôte qui veut des endpoints les construit lui-même. Un hôte qui liste des organisations ou des membres doit utiliser `ListPageAsync` / `ListByOrganizationPageAsync` (ci-dessous).

## Créer une organisation {#creating-an-organization}

Les organisations sont stockées via `IOrganizationStore`. Une implémentation durable est nécessaire avant de pouvoir en créer. L'implémentation intégrée par défaut est vide et en lecture seule, et refuse les écritures avec un message qui nomme l'enregistrement manquant. C'est délibéré : les enregistrements d'organisation décident de l'émission des jetons, et un dictionnaire local au processus continuerait d'émettre des jetons sur chaque nœud qui n'aurait pas vu une révocation.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` doit correspondre à `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$` : de 1 à 64 caractères parmi les lettres minuscules, les chiffres et les tirets intérieurs, sans tiret au début ni à la fin. En minuscules, parce que le paramètre `organization` est mis en minuscules avant la recherche par slug : un slug en majuscules serait une valeur qu'aucune requête ne pourrait jamais résoudre.

`Slug` doit être unique dans le locataire, et **identifiants et slugs partagent un même espace de noms** : un store rejette un upsert dont le slug est déjà détenu par une autre organisation, et de même un upsert dont le slug est égal à l'identifiant d'une autre organisation, ou dont l'identifiant est égal au slug d'une autre. Deux enregistrements répondant à une même valeur feraient que le paramètre `organization` désigne une organisation alors que chaque identifiant stocké en désigne une autre.

`Id` doit correspondre à `^[A-Za-z0-9._~-]{1,200}$` : la même forme que le paramètre `organization`, de sorte que tout identifiant peut toujours être envoyé comme valeur de ce paramètre. Un identifiant hors de cette forme est un identifiant qu'aucune requête ne peut sélectionner, et un client restreint à un tel identifiant refuserait toutes les requêtes.

`Id` **devrait** aussi contenir au moins un caractère interdit dans un slug : une lettre majuscule, `.`, `_` ou `~`. Identifiants et slugs partagent un même espace de recherche, et une valeur entièrement en minuscules est résolue d'abord comme slug : un identifiant qui a lui-même la forme d'un slug pourrait plus tard être refusé à la création parce que quelqu'un aurait pris ce slug, alors qu'un identifiant portant un caractère hors slug ne peut jamais l'être. La forme recommandée pour les nouveaux identifiants est une valeur opaque préfixée par `org_` (`org_7f3a9c`) : `_` n'est pas autorisé dans un slug, le préfixe suffit donc à le garantir. Aucune convention n'est imposée, et les valeurs déjà présentes dans le champ sont arbitraires : elles proviennent de la réponse `/try` TCC d'une application en aval (`TccProvisioningOrchestrator`) ou de la liaison d'un jeton SCIM par un opérateur (`ScimToken.OrganizationId`, apposée sur les nouveaux utilisateurs par `ScimUserEndpoints`).

`Id` comme `Slug` sont immuables en pratique. Les parties de confiance les comparent à l'instance qu'elles servent et les codent en dur : changer l'un ou l'autre provoque une panne sans message d'erreur. `DisplayName` est librement modifiable, et c'est ce qu'affiche un écran.

## Accorder une appartenance {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` vaut `invited`, `active` ou `suspended`. **Seul `active` autorise l'émission de jetons.** Suspendre plutôt que supprimer conserve la trace de qui a invité qui.

### Invitations {#invitations}

Une ligne `invited` porte `InvitedByUserId`, `InvitedAt` et les éventuels rôles proposés à la personne invitée. La bibliothèque n'envoie jamais l'invitation ; elle fait passer la ligne à `active` (en conservant les rôles, l'auteur et la date de l'invitation, et en renseignant `JoinedAt`) à deux endroits :

- **Connexion via une connexion SAML ou OIDC limitée à une organisation.** Le fait que l'IdP propre de l'organisation se porte garant de la personne vaut acceptation de l'invitation (`FederatedOrganizationBinding`, 0.30.2). Sans cela, une personne invitée qui ne se connecte jamais que par SSO se verrait refuser un jeton par le contrôle d'appartenance.
- **Appartenance automatique** (ci-dessous), lorsque l'utilisateur remplit les conditions.

Une ligne `suspended` n'est jamais promue par l'une ou l'autre voie, et n'est jamais modifiée par une connexion.

### Domaines vérifiés et appartenance automatique {#verified-domains-and-automatic-membership}

`Organization.Domains` contient les domaines de messagerie que l'organisation a revendiqués, chacun sous la forme d'un `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` est stocké en minuscules, sans espaces autour et sans point final. La bibliothèque stocke la revendication et lit `VerifiedAt` ; prouver le contrôle du domaine (généralement par un enregistrement DNS TXT portant `VerificationToken`) incombe à l'hôte, qui renseigne `VerifiedAt` en cas de succès.

`Organization.AllowAutoMembership` (désactivé par défaut) permet à un utilisateur de rejoindre l'organisation sans invitation. Lorsque l'organisation est sélectionnée **explicitement** (un octroi de rafraîchissement transmis, le paramètre `organization`, une connexion limitée à une organisation, ou un client restreint exactement à cette organisation) et que l'utilisateur n'a pas d'appartenance active, il en devient membre si **toutes** ces conditions sont réunies :

- l'organisation est activée et `AllowAutoMembership` est activé,
- `AuthUser.EmailConfirmed` vaut true,
- la partie de l'adresse e-mail qui suit le dernier `@`, mise en minuscules, est **exactement** égale à un domaine dont `VerifiedAt` est renseigné. Un `acme.com` vérifié n'admet pas `user@eu.acme.com`.

Une ligne absente est créée `active` sans rôle ; une ligne `invited` est promue ; une ligne `suspended` n'est jamais touchée. Cela s'applique à l'autorisation et à chaque rafraîchissement : tant que l'indicateur est activé, supprimer la ligne d'un membre qui remplit les conditions ne l'exclut pas (il revient à son prochain jeton) ; suspendez-le plutôt. Une organisation héritée uniquement de `AuthUser.OrganizationId` ne donne jamais lieu à une appartenance automatique. Chaque appartenance automatique est journalisée au niveau Information.

### Appartenance issue d'un jeton SCIM {#membership-from-a-scim-token}

Un jeton SCIM émis avec `organizationId` appose cette valeur en tant que `org_id` sur chaque utilisateur qu'il crée. Lorsque l'identifiant désigne une organisation existante, la création écrit aussi une appartenance `active` sans rôle et journalise `scim.organization_member_added` dans l'audit. Un identifiant qui ne désigne aucune organisation reste une simple étiquette. À la création seulement : une synchronisation ultérieure ne réétiquette pas et n'ajoute pas de membres. Voir [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Lister et supprimer {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` et `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` renvoient une page (`Items` et un `NextCursor` opaque, null sur la dernière page). `limit` est ramené dans la plage 1..200, et un curseur mal formé lève une `ArgumentException`. Le curseur repose sur un jeu de clés : une ligne ajoutée ou supprimée entre deux lectures ne décale ni ne répète jamais une page. Les deux méthodes ont des implémentations par défaut fondées sur les listes non paginées, de sorte qu'un store personnalisé continue de compiler ; les stores Azure Table les surchargent avec une requête par plage côté serveur.

Supprimer un utilisateur via le `DELETE /api/v1/profile/{userId}` d'administration, le `DELETE /scim/v2/Users/{id}` SCIM ou le chemin de récupération SCIM supprime aussi toutes ses appartenances (`AccountArtefactPurge.PurgeAsync` avec un `IOrganizationMembershipStore` ; la surcharge à trois stores n'en purge aucune). Un hôte doté de son propre chemin de suppression doit lui aussi passer le store des appartenances, faute de quoi les organisations continuent de lister le membre supprimé.

## Rôles propres à une organisation {#organization-scoped-roles}

`OrganizationMembership.Roles` contient les rôles qu'un utilisateur détient **au sein de** cette organisation. Les noms proviennent du catalogue de rôles existant du locataire : un éditeur déclare « Auditor » une seule fois, et chaque client l'attribue à ses propres collaborateurs.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

Ils sont réunis dans la revendication `roles` avec les rôles attribués directement à l'utilisateur et ceux accordés par l'appartenance à des groupes SCIM, sous le même contrôle de scope `roles`. Un serveur de ressources n'a pas besoin de savoir si un rôle a été accordé à l'échelle du locataire ou par organisation, mais il **doit** lire `org_id` en même temps que `roles`, car le même nom de rôle signifie désormais « dans cette organisation ».

Quatre règles l'encadrent :

- **Seule une organisation sélectionnée explicitement apporte des rôles** : celle que désigne le paramètre `organization` ou une restriction de client à une seule entrée. Une organisation héritée de `AuthUser.OrganizationId` n'en apporte aucun, selon la même asymétrie que le contrôle d'appartenance.
- **Seule une appartenance `active` apporte des rôles.** Un membre invité qui n'a pas accepté, ou suspendu, n'accorde rien, exactement comme il n'autorise rien.
- **Les rôles ne franchissent jamais les organisations.** Ils sont lus dans la ligne d'appartenance indexée par l'organisation sélectionnée : un rôle détenu dans l'une ne peut pas atteindre un jeton émis pour une autre.
- **Les préfixes réservés sont retirés.** Un rôle commençant par `tenant:` ou `platform:` est écarté lors de la réunion et journalisé au niveau Warning. Une ligne d'appartenance est une donnée relevant du client : une appartenance capable d'accorder `tenant:admin` transformerait « peut gérer ma propre organisation » en « peut administrer le locataire ». Les rôles attribués directement et les correspondances groupe SCIM→rôle ne sont pas concernés : ils sont écrits par un opérateur via une interface d'administration authentifiée, c'est-à-dire l'autorité dont une ligne d'appartenance ne dispose pas.

Les rôles sont relus dans la ligne d'appartenance à chaque rotation de rafraîchissement : les modifier atteint donc une session en cours à son prochain rafraîchissement.

Les rôles à l'échelle du locataire sont **réunis avec** ceux de l'organisation, et non remplacés par eux : `tenant:admin` est une autorité sur le portail et survit à la sélection d'une organisation.

## Sélectionner une organisation dans une requête d'autorisation {#selecting-an-organization-on-an-authorization-request}

Envoyez `organization` avec le slug ou l'identifiant d'une organisation :

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

La valeur doit correspondre à `^[A-Za-z0-9._~-]{1,200}$` (l'ensemble des caractères non réservés de la RFC 3986) ; toute autre valeur donne `invalid_request`. Sa résolution dépend de sa casse :

- **Au moins une majuscule → résolue uniquement comme identifiant, à l'identique.** Les slugs sont uniquement en minuscules : une telle valeur ne peut donc pas en être un. La mettre en minuscules et interroger quand même l'index des slugs reviendrait à demander « le slug d'une organisation est-il la forme en minuscules de cet identifiant ? », et si c'était le cas, un appelant désignant un identifiant se verrait remettre un autre client.
- **Entièrement en minuscules → d'abord comme slug, puis comme identifiant.** Elle peut être l'un ou l'autre, et c'est généralement un slug qu'envoie une partie de confiance. C'est sans ambiguïté, car un store refuse qu'un identifiant et un slug partagent une valeur.

`org_slug` et `org_id` sont acceptés comme alias : les deux circulent chez d'autres fournisseurs, et ignorer en silence celui que ce serveur n'a pas retenu serait pire que d'accepter les deux. En envoyer deux qui désignent des organisations *différentes* est refusé avec `invalid_request` : la requête signifie deux choses, et quel que soit le choix du serveur, la partie de confiance aurait été informée de l'autre. Répéter l'un des trois est refusé pour la même raison que `redirect_uri`.

Le paramètre survit à l'aller-retour par l'interface de connexion, car l'URL d'autorisation entière est transmise en tant que `returnUrl`. Il fonctionne aussi avec les [Pushed Authorization Requests](par) sans travail supplémentaire : l'endpoint PAR stocke chaque champ qu'il reçoit, et `/connect/authorize` lit le contenu poussé au lieu de la chaîne de requête.

### Ordre de priorité {#precedence}

L'organisation est résolue dans cet ordre :

1. **Lors d'un rafraîchissement, l'organisation pour laquelle l'octroi a été émis.**
2. **L'organisation pour laquelle une [connexion SSO limitée à une organisation](self-service-sso#organisation-scoped-connections) a authentifié cette session.** C'est la seule source de cette liste qui ait été *prouvée* plutôt qu'affirmée par un appelant : l'utilisateur s'est connecté auprès d'un IdP qui appartient à exactement une organisation. Une requête qui en désigne une autre est refusée avec `access_denied` au lieu de recevoir discrètement un jeton pour l'autre.
3. **Le paramètre `organization`.**
4. **`OAuthClient.RestrictedToOrganizationIds`, lorsqu'il contient exactement une entrée.** Une application propre à un client désigne son organisation une seule fois, à l'enregistrement, et sa partie de confiance n'envoie jamais de paramètre. C'est la forme que veulent la plupart des produits à instance unique par client.
5. **`AuthUser.OrganizationId`** : l'organisation stockée sur le compte lui-même.

Les règles 1 à 4 sont des sélections *explicites* et doivent satisfaire le contrôle d'appartenance. La règle 5 ne l'est pas : l'enregistrement du compte constitue lui-même l'affirmation d'appartenance, et en exiger une seconde bloquerait tous les utilisateurs préexistants dès la création de l'organisation correspondante.

Avant que quiconque se soit authentifié (découverte du domaine d'origine, liste des fournisseurs de la page de connexion, `/sso-check`), il n'y a ni utilisateur ni octroi : les règles 3 et 4, puis `ITenantContext.OrganizationId`, sont donc résolues seules. Voir [Connexions limitées à une organisation](self-service-sso#organisation-scoped-connections).

## Restreindre un client à une organisation {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Chaque entrée doit correspondre à la forme d'identifiant d'organisation `^[A-Za-z0-9._~-]{1,200}$` ; l'API d'administration répond `400 invalid_request` pour une entrée vide ou mal formée, car une restriction qui liste un identifiant qu'aucun paramètre `organization` ne peut envoyer ne correspond à rien, et une restriction qui ne correspond à rien refuse toutes les requêtes. Une liste `null` est normalisée en liste vide.

Une liste vide (ce qu'ont tous les clients existants) signifie aucune restriction. Une requête dont l'organisation ne figure pas dans la liste est refusée avec `access_denied`. Une liste d'une seule entrée sélectionne aussi l'organisation, selon la règle 4 ci-dessus. Une liste de plusieurs entrées restreint sans sélectionner : la requête doit tout de même en désigner une, sinon elle est refusée avec `account_selection_required`.

## Les revendications {#the-claims}

Dans le jeton d'identité comme dans le jeton d'accès :

| Revendication | Valeur | Scope |
|---|---|---|
| `org_id` | `Organization.Id` | aucun ; toujours présent lorsque le sujet a une organisation |
| `org_slug` | `Organization.Slug` | aucun ; toujours présent lorsque l'organisation est un enregistrement réel |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` et `org_slug` ne sont délibérément pas soumis à un scope.** Ce sont des éléments du contexte d'autorisation, pas des données de profil : ils indiquent pour quel client le jeton peut agir, ce qui est la première chose que vérifie un serveur de ressources multi-clients, avant même d'avoir décidé si un nom l'intéresse, et souvent sur un jeton qui n'a demandé aucun profil. Sous un contrôle `profile`, un client purement API ne demandant que `openid` recevait un jeton sans organisation, ce qui se lit comme « n'appartient à personne » : le serveur de ressources soit refuse un appelant légitime, soit traite le jeton comme non limité et sert à partir de lui les données de tous les clients. Le second échec est silencieux, et c'est celui qui compte.

Les délivrer sans contrôle ne divulgue rien que le client n'ait déjà établi : il a choisi l'organisation, ou il est restreint à une seule. `org_name` conserve le contrôle `profile` car il relève de la présentation, et rien ne devrait s'appuyer sur lui pour autoriser.

Un compte sans organisation n'émet aucune des trois : un jeton qui ne portait auparavant aucune revendication d'organisation n'en porte toujours aucune.

Les trois sont réservés : ni la liste `UserClaims` d'un scope ni un attribut utilisateur personnalisé ne peuvent les produire ou les surcharger. C'est surtout important pour `org_slug`, la clé stable qu'une partie de confiance compare à l'instance client qu'elle sert. Un `org_slug` auto-déclaré dicterait le résultat de cette comparaison.

Un compte portant un identifiant d'organisation qui ne correspond à aucun enregistrement n'émet que `org_id`. L'absence de `org_slug` signifie « il n'y a pas de slug », jamais « valeur retenue ».

**Validez `org_id` dans votre application :**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, introspection et échange de jetons {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** renvoie `org_id`, `org_slug`, `org_name` et `roles` à partir du **jeton présenté**, et non de l'enregistrement de l'utilisateur. `org_id` et `org_slug` sont renvoyés chaque fois que le jeton les porte, sans contrôle de scope, pour la même raison qu'ils n'y sont pas soumis dans le jeton lui-même ; `org_name` nécessite `profile`. C'est la seule source qui puisse être exacte dès lors qu'un utilisateur peut appartenir à plusieurs organisations : le compte porte une valeur par défaut, tandis que le jeton désigne l'organisation pour laquelle l'octroi a effectivement été émis. Les champs de profil (`email`, `name`, `phone_number`) restent à jour : ce sont les informations actuelles du sujet, et c'est précisément le rôle de userinfo.

Ainsi, réétiqueter un compte ne change pas ce que userinfo dit d'un jeton déjà émis, et un utilisateur connecté à l'organisation B ne se voit jamais annoncer `org_id` A par le serveur même qui a placé B dans son jeton d'identité.

**`/connect/introspect`** inclut `org_id` et `org_slug` lorsque le jeton les porte. Un serveur de ressources qui valide lui-même le JWT les lit dans le jeton ; celui qui procède par introspection obtient désormais la même réponse.

**L'échange de jetons RFC 8693** reporte `org_id`, `org_slug` et `org_name` du jeton sujet sur le jeton échangé, et applique les `RestrictedToOrganizationIds` du client **qui effectue l'échange** : un client enregistré pour servir un seul client final ne peut pas échanger le jeton d'un autre client final, ni un jeton qui ne porte aucune organisation. Comme `org_id` n'est pas soumis à un scope, ce contrôle fonctionne aussi pour un jeton de serveur de ressources émis sans le scope `profile` : avec l'ancien contrôle, un tel jeton paraissait sans attribution, et un client restreint se voyait refuser son propre trafic. Un refus donne `invalid_target`, comme les autres refus de politique de cible sur ce chemin. Un échange est une projection d'une session existante, et une projection qui perdrait l'organisation pour laquelle elle agit serait sans attribution plutôt que plus restreinte. Le `ITokenExchangeSubjectTransformer` d'un hôte peut toujours rattacher délibérément l'échange à une autre organisation (c'est l'objet des échanges liés à un contexte), mais il doit le faire explicitement.

## Rafraîchissement {#refresh}

L'organisation pour laquelle un octroi a été émis est conservée à chaque rotation de rafraîchissement, et revérifiée à chaque fois. Trois changements prennent donc effet à la rotation suivante, sans attendre la fin de la durée de vie du rafraîchissement :

- la révocation ou la suspension d'une appartenance,
- la désactivation d'une organisation (`Enabled = false`),
- la restriction des `RestrictedToOrganizationIds` d'un client.

**Chacun refuse le rafraîchissement ; aucun ne révoque l'octroi.** Le jeton de rafraîchissement présenté n'est pas consommé et la famille reste intacte : la chaîne reste refusée tant que la condition persiste et reprend dès qu'elle cesse : rétablir une appartenance ou réactiver une organisation fait revenir la session sans nouvelle connexion. L'octroi expire toujours au terme de sa propre durée de vie absolue. C'est le même comportement que pour un utilisateur désactivé, dont les rafraîchissements sont refusés tant que `IsActive` vaut false.

Pour mettre réellement fin à une session, révoquez l'octroi : `POST /connect/revocation` avec le jeton de rafraîchissement, ou `GrantRevocation` côté hôte. Désactiver une organisation est un contrôle d'accès, pas une révocation.

Un octroi qui a simplement hérité de l'organisation du compte est en revanche redéterminé à chaque rotation : réétiqueter un compte prend donc toujours effet.

**Changer d'organisation est une nouvelle requête d'autorisation**, pas un rafraîchissement. Rappelez `/connect/authorize` avec une autre `organization` ; la session existante est réutilisée, il n'y a donc pas de seconde connexion, et un nouvel octroi commence. Ne comptez pas sur l'endpoint de rafraîchissement pour changer d'organisation : il n'a ni agent utilisateur ni consentement, et l'octroi enregistre les scopes approuvés pour l'organisation pour laquelle il a été émis.

## Désactiver le contrôle d'appartenance {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

Activé par défaut. Désactivez-le pour un déploiement qui utilise les organisations pour la personnalisation et le routage plutôt que pour l'accès : toute personne capable de nommer l'organisation reçoit alors un jeton pour celle-ci. Une organisation dont l'appartenance est purement indicative n'est pas une frontière ; faites ce choix en connaissance de cause.

## Refuser une émission depuis un hook de l'hôte {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` se déclenche immédiatement avant que les octrois `authorization_code`, `refresh_token` et `device_code` n'émettent quoi que ce soit, avec le sujet résolu :

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Lever une exception refuse l'émission avec `access_denied`, le message de l'exception servant de `error_description` ; lever plutôt une `ProtocolTokenException` permet de nommer votre propre erreur OAuth. Sur le chemin de rafraîchissement, le contrôle s'exécute **avant** la rotation : un refus laisse le jeton de rafraîchissement présenté non consommé et la famille intacte ; « pas maintenant » ne signifie pas « mettez fin à cette session ».

C'est un membre d'interface par défaut : un `IAuthHook` existant qui ne le surcharge pas n'est pas affecté. Les deux émissions agentiques (`client_credentials` et l'échange de jetons, chacun avec un profil d'agent) le déclenchent exactement comme auparavant.

## Refus {#refusals}

| Condition | Erreur |
|---|---|
| Deux sélecteurs désignant des organisations différentes | `invalid_request` |
| Un sélecteur répété | `invalid_request` (renvoyé directement, sans redirection vers `redirect_uri`) |
| L'organisation désignée n'existe pas | `access_denied` |
| L'organisation est désactivée | `access_denied` |
| Le client n'est pas autorisé pour cette organisation | `access_denied` |
| L'utilisateur n'est pas un membre actif (sélection explicite) | `access_denied` |
| Le client sert plusieurs organisations et la requête n'en désigne aucune | `account_selection_required` |

## Flux d'appareil {#device-flow}

L'octroi d'appareil n'a pas de requête d'autorisation pour transporter un paramètre : il se rabat donc sur la restriction du client, puis sur la valeur par défaut du compte. Un client d'appareil qui doit être rattaché à une seule organisation doit être enregistré avec un `RestrictedToOrganizationIds` à une seule entrée.

## Mettre à niveau un déploiement existant {#upgrading-an-existing-deployment}

Rien ne change tant qu'aucune organisation n'existe. Sans enregistrement :

- aucune requête ne peut sélectionner d'organisation,
- aucun contrôle d'appartenance n'entre en jeu,
- un compte portant un ancien `OrganizationId` continue d'émettre `org_id` à partir de l'enregistrement de l'utilisateur, exactement comme avant,
- un jeton émis pour un utilisateur sans organisation ne porte aucune des trois revendications.

Une fois les organisations créées, accordez les appartenances **avant** de faire pointer un client ou une partie de confiance vers l'une d'elles : une sélection explicite exige une appartenance active, et un client dont les utilisateurs ont des enregistrements mais aucune appartenance sera refusé.
