---
layout: default
title: SSO en libre-service
locale: fr
---

# Intégration SSO en libre-service

Une fois que vous avez [fédéré une connexion](oidc-federation) vers l'IdP d'un client, la question suivante
est : **que se passe-t-il lorsque quelqu'un qui ne s'est jamais connecté se présente ?** Authagonal vous propose
trois modes pour cet utilisateur inconnu, du plus strict au plus ouvert (refuser tout utilisateur inconnu, exiger un contexte
d'invitation, ou provisionner automatiquement depuis un domaine autorisé), ainsi que les contrôles qui évitent qu'un IdP *externe* ne devienne
un piège. Les deux premiers sont traités ensemble dans le mode 1, le troisième dans le mode 2. Ce guide explique comment choisir et mettre en place le mode qui vous convient.

Tout cela se configure par connexion : les sections d'initialisation `OidcProviders` et `SamlProviders`, les objets stockés
`OidcProviderConfig` / `SamlProviderConfig` et l'API d'administration. Les réglages concernés :

| Réglage | Effet | Protocoles |
|---|---|---|
| `JitProvisioningEnabled` | Un utilisateur inconnu peut-il seulement être créé ? | OIDC, SAML |
| `ProvisioningAttributeParams` | Exige un *contexte d'invitation* sur la requête avant d'en créer un. | OIDC, SAML |
| `AllowUninvitedJit` | Autorise la création en libre-service **sans** invitation (étiquetée avec la connexion). | OIDC, SAML |
| `IsExternalConnection` | Désigne un IdP tiers afin que les indicateurs réservés aux connexions internes ne puissent pas s'appliquer. | OIDC uniquement |
| `InteractionPath` | Affiche une page de l'application de connexion (nom, conditions) *avant* la fédération. | OIDC uniquement |

L'endroit où chacun peut être défini compte, car l'API d'administration ne les expose pas tous :

- **Initialisation par la configuration (`OidcProviders`, `SamlProviders`) :** chacun des réglages ci-dessus qui existe pour le protocole. Les
  connexions initialisées sont réappliquées depuis la configuration à chaque démarrage ; pour une connexion initialisée,
  `JitProvisioningEnabled` et `AllowUninvitedJit` proviennent donc de l'initialisation, et non de la dernière valeur stockée.
- **API d'administration SAML** (`POST` / `PUT /api/v1/saml/connections`) : `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` et `AllowUninvitedJit`.
- **API d'administration OIDC** (`POST /api/v1/oidc/connections`) : `JitProvisioningEnabled` et `InteractionPath`
  (qui doit commencer par `/`). `ProvisioningAttributeParams`, `AllowUninvitedJit` et `IsExternalConnection`
  ne se définissent que par l'initialisation pour OIDC, et il n'existe pas de route de mise à jour pour une connexion OIDC. Voir
  [API d'administration](admin-api) et [Fédération OIDC](oidc-federation).

## Mode 1 : sur invitation uniquement (refuser les non-invités) {#posture-1-invite-only-reject-the-uninvited}

C'est le comportement par défaut. Avec `JitProvisioningEnabled: false`, un utilisateur SSO inconnu est refusé d'emblée
(`access_denied`, « contactez votre administrateur »), ce qui convient lorsque chaque utilisateur doit être créé au préalable
par un administrateur ou par SCIM.

Si vous voulez le JIT, mais *uniquement* en présence d'une invitation, activez le JIT **et** déclarez
`ProvisioningAttributeParams`. Ce réglage nomme les paramètres de requête `/authorize` autorisés qui portent le contexte
d'invitation (par exemple `acceptKind`, `acceptToken`). Un utilisateur inconnu n'est provisionné que si au moins l'un de ces
paramètres est effectivement arrivé avec une valeur ; une connexion SSO simple, sans invitation, est refusée avec `access_denied`
(« Cette connexion nécessite une invitation »), de sorte qu'une connexion isolée ne peut pas provisionner silencieusement un nouveau compte ou une nouvelle organisation.
Les paramètres sont lus dans la requête de l'URL `/authorize` vers laquelle l'utilisateur revient (le `RelayState` pour
SAML) ; OIDC se rabat aussi sur la requête du rappel elle-même.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

Il n'y a pas de `RedirectUrl` à définir : le `redirect_uri` du rappel est dérivé pour chaque requête sous la forme
`{issuer}/oidc/callback` ; enregistrez donc cette URI auprès de l'IdP amont. Un `RedirectUrl` initialisé est ignoré.

Les paramètres capturés sont enregistrés dans les `CustomAttributes` de l'utilisateur JIT et parviennent à votre
[gestionnaire `Try` de provisionnement](provisioning), qui constitue le véritable contrôle des *valeurs* (par exemple
« ce jeton d'invitation correspond-il à cet e-mail ? »). Authagonal capture les clés autorisées ; votre service de provisionnement décide
si elles sont valides. Si `Try` répond `approved: false`, l'utilisateur qui vient d'être créé est supprimé et le navigateur reçoit
`400 provisioning_rejected`.

## Mode 2 : libre-service (provisionnement automatique d'un utilisateur d'un domaine autorisé) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

Pour que « n'importe quel employé d'un client puisse simplement se connecter et obtenir un compte », définissez `AllowUninvitedJit: true`. Dès lors, un
utilisateur inconnu issu d'un **domaine autorisé** est provisionné même sans contexte d'invitation, et Authagonal l'étiquette
avec la connexion par laquelle il est arrivé, afin que votre service de provisionnement puisse le placer chez le bon locataire au lieu
d'en créer un nouveau. Le contrôle du domaine ne s'applique que lorsque `AllowedDomains` n'est pas vide : une connexion qui
ne liste aucun domaine accepte n'importe quel domaine affirmé par son IdP ; listez-les donc sur chaque connexion en libre-service.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

L'étiquette arrive sous la forme d'un attribut personnalisé `federated_connection`. Sa valeur est le `ConnectionName` de la connexion
(et non son `ConnectionId`), et elle n'est écrite que lorsque l'utilisateur a été créé sans contexte d'invitation ; un
utilisateur invité porte donc à la place les paramètres capturés. Votre gestionnaire `Try` distingue les deux cas :

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` s'active explicitement, connexion par connexion : une connexion qui déclare `ProvisioningAttributeParams` sans
le définir reste en mode sur invitation uniquement.

Deux contrôles supplémentaires s'exécutent avant la création de tout utilisateur inconnu, quel que soit le mode choisi :

- **Le domaine ne doit pas appartenir à une autre connexion.** Si l'index des domaines SSO achemine le domaine de l'e-mail de l'utilisateur
  vers une autre connexion, la connexion est refusée avec `access_denied` (« Ce domaine de messagerie est
  géré par un autre fournisseur d'identité »).
- **OIDC uniquement : l'amont doit avoir vérifié l'e-mail.** Lorsque l'amont n'indique pas `email_verified` à true (lu dans l'id_token, ou dans la
  réponse userinfo lorsque l'e-mail en provient), la connexion est
  refusée avec `access_denied`. Une assertion SAML ne comporte pas d'indicateur de ce type ; SAML s'appuie donc plutôt sur `AllowedDomains`.

`federated_connection` est un nom d'attribut réservé. Il n'est jamais émis sur un jeton, la revendication de ce nom dans l'id_token
d'un amont OIDC est abandonnée, et l'inscription anonyme en libre-service ne peut pas le définir ; seuls les rappels SSO
peuvent donc affirmer par quelle connexion un compte est arrivé.

## Éviter que les IdP externes ne deviennent des pièges {#keep-external-idps-from-becoming-foot-guns}

Quelques indicateurs de connexion OIDC sont sans danger sur une connexion que **vous** contrôlez, mais dangereux sur un IdP tiers
quelconque :

- **`UseUpstreamSubjectAsUserId`** : l'amont choisit l'identifiant de l'utilisateur local. Sur votre propre fournisseur de liens
  de partage, cela garde les identifiants alignés ; sur l'IdP d'un client, cela *lui* permet de choisir vos identifiants d'utilisateur.
- **`AutoLinkExistingByEmail`** : rattache une connexion fédérée à un compte local déjà existant par e-mail,
  en sautant le contrôle de propriété du domaine. Avec une boîte de réception vérifiée et une connexion interne, pas de problème ; sur un IdP externe, c'est un
  levier de prise de contrôle de comptes.

Désignez les connexions tierces comme **externes** et ces indicateurs sont neutralisés, même s'ils sont définis :

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` vaut `false` par défaut (connexion interne), de sorte que les connexions existantes ne sont pas affectées. Définissez-le sur
chaque connexion OIDC qui pointe vers l'IdP de quelqu'un d'autre ; une erreur de configuration ultérieure ne pourra alors pas donner à cet IdP
le contrôle des identités locales. Les connexions SAML n'ont aucun de ces indicateurs ; il n'y a donc rien à neutraliser
de ce côté. (Rattacher une identité fédérée à un compte déjà existant exige toujours, en outre, que les
`AllowedDomains` de la connexion garantissent le domaine de l'e-mail : voir
[Fédération OIDC : sécurité](oidc-federation).)

## Collecter une information avant la fédération {#collect-something-before-federating}

Il faut parfois afficher une page à l'utilisateur **avant** de le renvoyer vers l'IdP : le nom affiché d'un invité, une
case d'acceptation des conditions, un choix d'offre. `InteractionPath` (connexions OIDC uniquement) désigne une route de l'application de connexion à afficher
d'abord :

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

Lorsqu'une requête `idp_hint={ConnectionId}` non authentifiée atteint `/connect/authorize`, Authagonal redirige vers
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` au lieu de rediriger directement vers l'IdP
(`LoginAppUrl` vaut `/login` par défaut, et le chemin doit commencer par `/`). La même redirection a lieu lorsque
la connexion unique d'une [organisation](#organisation-scoped-connections), ou celle qui correspond au domaine, fait l'objet d'un défi automatique,
et lorsque `prompt=login` impose une nouvelle authentification via un `idp_hint`. Votre page collecte ce dont elle a besoin,
ajoute les valeurs à la requête du `returnUrl` (où `PassthroughParams` /
`ProvisioningAttributeParams` les lisent), puis poursuit elle-même vers `/oidc/{id}/login`. Une page qui
détermine qu'aucune interaction n'est nécessaire peut poursuivre immédiatement.

## Connexions propres à une organisation {#organisation-scoped-connections}

Tout ce qui précède décrit une connexion **au niveau du locataire** : une connexion partagée par tout le locataire, dont les
`AllowedDomains` revendiquent un domaine de messagerie pour chaque écran de connexion servi par le locataire. C'est la bonne
forme lorsque vous fédérez vers un client par locataire. C'est la mauvaise lorsqu'un même locataire sert de nombreuses
[organisations](organizations) clientes et que chacune apporte son propre IdP : deux clients ne peuvent pas revendiquer tous deux
`contoso.com`, et le bouton « Continuer avec Contoso Entra » d'un client n'a rien à faire sur l'écran de connexion
d'un autre.

Définissez `OrganizationId` sur une connexion et elle appartient alors à cette organisation (à la création et, pour SAML, à la
mise à jour, via l'API d'administration ; une organisation qui n'existe pas donne `400 unknown_organization`) :

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Trois choses changent, et rien d'autre.

**Elle n'est proposée que lorsque cette organisation est sélectionnée.** Une connexion propre à une organisation n'apparaît jamais sur
l'écran de connexion du locataire lui-même et n'est jamais atteinte par une requête qui n'a été résolue vers aucune organisation,
même si son `login_hint` correspond exactement à ses domaines.

**Ses domaines ne sont mis en correspondance qu'au sein de cette organisation.** Une connexion propre à une organisation n'est délibérément
*pas* écrite dans l'index des domaines SSO à l'échelle du locataire ; un domaine peut donc être revendiqué une fois au niveau du locataire et
une fois par organisation. Une seconde revendication au sein d'une même organisation reste refusée avec `domain_claimed`,
quel que soit le protocole, de sorte qu'une adresse ne peut pas être acheminée vers deux IdP d'une même organisation. Déplacer une connexion
dans une organisation supprime ses lignes d'index ; la déplacer de nouveau (en envoyant `"organizationId": ""` au point de terminaison de mise à jour
SAML) les réenregistre. Les connexions OIDC n'ont pas de route de mise à jour ; leur portée se définit donc à la création ou
dans l'initialisation `OidcProviders`.

**Toute personne qui se connecte par son intermédiaire en devient membre.** L'ACS SAML et le rappel OIDC inscrivent
l'organisation de la connexion comme `org_id`, à la place du propre `AuthUser.OrganizationId` du compte (qui
est un artefact du provisionnement en aval plutôt qu'une affirmation portant sur cette connexion), et créent une
appartenance active si l'utilisateur n'en a aucune. Une appartenance **invitée** est acceptée : elle passe à `active` et
conserve ses rôles, la personne qui a invité et la date d'invitation, puisque l'IdP propre à l'organisation vient de se porter garant de
cette personne. Toute autre appartenance existante est laissée telle quelle : une ligne `suspended` reste suspendue ; se reconnecter
ne peut donc pas rétablir un accès qu'un administrateur a révoqué.

### Pour quelle organisation est une requête, avant toute connexion {#which-organization-a-request-is-for-before-anyone-signs-in}

La découverte du domaine d'origine (home-realm discovery) doit répondre à cette question avant qu'il n'y ait un utilisateur ; elle procède donc à une résolution distincte (mais dans
le même ordre) de celle du [sélecteur postérieur à l'authentification](organizations#precedence) :

1. Le **paramètre `organization`** de la requête (un slug ou un identifiant).
2. **`OAuthClient.RestrictedToOrganizationIds`**, lorsqu'il contient exactement une entrée. Deux entrées ou plus ne constituent pas une
   sélection : le client en sert plusieurs et la requête n'en a désigné aucune.
3. **`ITenantContext.OrganizationId`** : un hôte qui en fixe une par requête, par exemple un domaine personnalisé
   propre à chaque organisation. `null` dans tout déploiement mono-locataire.

L'organisation doit exister et être activée, et la restriction du client doit l'autoriser. Dans tous les autres cas, la résolution
aboutit à *aucune organisation* et la requête poursuit sur le chemin à l'échelle du locataire, exactement comme auparavant. En
particulier, un paramètre désignant une organisation que la restriction du client exclut n'est pas refusé ici :
le refus existe déjà après l'authentification (`access_denied`), et le placer avant l'écran de connexion
changerait les requêtes qu'un appelant non authentifié peut distinguer les unes des autres.

### Ce qu'en fait `/connect/authorize` {#what-connectauthorize-does-with-it}

Une fois une organisation résolue, et avant toute règle à l'échelle du locataire :

- Un `idp_hint` désignant l'une de **ses** connexions mène directement à cette connexion. Cela inclut SAML, que
  le chemin d'indication à l'échelle du locataire (OIDC uniquement) ne peut pas atteindre. Une indication désignant autre chose est ignorée et le traitement se poursuit.
- **Exactement une connexion et aucun `login_hint` contradictoire** → directement vers elle. Une connexion qui ne liste aucun
  domaine revendique toute l'organisation ; une connexion qui liste des domaines fait tout de même l'objet d'un défi automatique, sauf si le
  domaine de l'adresse indiquée n'en fait pas partie.
- **Plusieurs connexions** → le domaine de l'e-mail indiqué départage.
- **Aucune correspondance** → le comportement à l'échelle du locataire pour `login_hint` et la carte de connexion, inchangé.

Une fédération qui a échoué et qui revient avec `error=` dans la requête renvoie cette erreur à la partie de confiance
au lieu de fédérer de nouveau, de sorte qu'un défi automatique ne peut pas tourner en boucle.

### Ce que voit l'application de connexion {#what-the-login-app-sees}

`/api/auth/providers` et `/api/auth/sso-check` acceptent tous deux un paramètre de requête `organization` (et se rabattent
sur `ITenantContext.OrganizationId`), résolu selon les mêmes règles. Avec une organisation :

- `providers` liste d'abord les connexions sous forme de bouton de **cette organisation**, puis celles du locataire. Une connexion
  n'est un bouton que lorsqu'elle ne liste aucun `AllowedDomains` (et, pour OIDC, que `ShowOnLogin` est activé) ; celles qui sont acheminées par domaine
  sont atteintes en commençant par l'e-mail, via `sso-check`. Les connexions propres à une organisation sont entièrement exclues de la liste
  lorsqu'aucune organisation n'est résolue, et les connexions des autres organisations ne sont jamais listées.
- `providers` comporte en plus **`autoChallenge`** lorsque l'organisation a exactement une connexion : un enregistrement de
  fournisseur complet (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) pour la connexion vers laquelle l'application
  doit aller directement, sans passer par la carte. Il porte l'enregistrement entier plutôt qu'un simple identifiant, car
  cette connexion peut être acheminée par domaine ou masquée, et donc absente de `providers`. Le champ est
  omis dans les autres cas, et il n'est **qu'indicatif** : `/connect/authorize` effectue lui-même le même défi automatique ;
  une application qui l'ignore atteint donc quand même le même IdP.
- `sso-check` met en correspondance les domaines des connexions de l'organisation **avant** l'index à l'échelle du locataire, et
  se rabat sur celui-ci lorsque l'organisation ne revendique rien pour cette adresse. Une connexion unique qui ne liste
  aucun domaine revendique toutes les adresses.

### À l'émission des jetons {#at-token-issuance}

Une session établie via une connexion propre à une organisation porte cette organisation comme source prioritaire,
juste après la valeur conservée lors d'une actualisation (avant le paramètre `organization` et avant la
restriction du client), car c'est la seule qui ait été *prouvée* : l'utilisateur s'est authentifié auprès d'un IdP appartenant
précisément à cette organisation. Une requête qui en désigne une autre est refusée avec `access_denied` au lieu
de recevoir discrètement des jetons pour l'autre. Le `RequireMembershipForTokens` de l'organisation s'applique toujours, et c'est
pourquoi le rappel crée l'appartenance.

## Voir aussi {#related}

- [Organisations](organizations) : les enregistrements, les appartenances, les revendications et les règles de sélection.
- [Fédération OIDC](oidc-federation) : la mise en place de la connexion et le modèle de sécurité.
- [Provisionnement TCC](provisioning) : le gestionnaire `Try` qu'appellent ces flux.
- [Garder les sessions fédérées synchronisées](federated-sessions) : révoquer les sessions locales lorsque l'amont le fait.
