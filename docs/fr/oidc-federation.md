---
layout: default
title: Fédération OIDC
locale: fr
---

# Fédération OIDC

Authagonal peut déléguer l'authentification à des fournisseurs d'identité OIDC externes (Google, Apple, Azure AD, etc.). Cela permet des parcours du type « Se connecter avec Google », tandis qu'Authagonal reste le serveur d'authentification central.

## Fonctionnement {#how-it-works}

Il existe deux points d'entrée dans la fédération :

**Par domaine (connexion interactive) :**

1. L'utilisateur saisit son e-mail sur la page de connexion
2. La SPA appelle `/api/auth/sso-check` ; si le domaine de l'e-mail est lié à un fournisseur OIDC, le SSO est obligatoire
3. L'utilisateur clique sur « Continuer avec SSO » et est redirigé vers l'IdP externe (lorsque l'e-mail est le `login_hint` d'une requête d'autorisation et que son domaine est acheminé vers une connexion, l'utilisateur est envoyé directement à l'IdP, avec le `login_hint` transmis)
4. Après l'authentification, l'IdP le redirige vers `/oidc/callback`
5. Authagonal valide l'id_token, lie l'utilisateur (ou en crée un si la connexion autorise le provisionnement JIT) et définit un cookie de session

**Indiqué par la partie de confiance (`idp_hint`) :**

La partie de confiance en aval peut acheminer directement vers un IdP amont précis, sans passer par l'étape de l'e-mail et du domaine SSO. Ajoutez `idp_hint={connectionId}` à `/connect/authorize` :

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Lorsque la requête n'est pas authentifiée, Authagonal redirige vers `/oidc/{connectionId}/login` en conservant l'URL `/authorize` d'origine dans `returnUrl`. Une fois la fédération terminée, l'utilisateur revient sur `/authorize` avec un cookie de session et le flux se poursuit normalement. Si la connexion définit `InteractionPath`, l'utilisateur est d'abord envoyé vers cette page de l'application de connexion (voir [Collecter une information avant la fédération](self-service-sso#collect-something-before-federating)). Une connexion avec `ShowOnLogin: false` n'est jamais proposée comme bouton de connexion et n'est accessible que de cette manière.

## Mise en place {#setup}

### 1. Créer un fournisseur OIDC {#1-create-an-oidc-provider}

**Option A, configuration (recommandée pour les installations statiques) :**

Ajoutez à `appsettings.json` :

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Les fournisseurs sont initialisés au démarrage. `ConnectionId`, `MetadataLocation`, `ClientId` et `ClientSecret` sont obligatoires (le démarrage échoue sans eux). `RedirectUrl` est accepté par souci de compatibilité, mais ignoré : l'URI de redirection est dérivée pour chaque requête sous la forme `{Issuer}/oidc/callback`, puisqu'elle doit se trouver sur l'origine où se trouve le navigateur, et c'est cette URI qu'il faut enregistrer auprès de l'IdP (une valeur initialisée différente est journalisée comme ignorée). Le `ClientSecret` est protégé via `ISecretProvider` (Key Vault lorsqu'il est configuré, en clair sinon). Les correspondances de domaines SSO sont enregistrées automatiquement à partir de `AllowedDomains`, sauf pour une connexion propre à une organisation, dont les domaines ne sont mis en correspondance qu'au sein de son organisation.

L'initialisation peut aussi définir chacun des indicateurs de comportement du tableau ci-dessous. **Une entrée initialisée remplace la connexion stockée à chaque démarrage** : un indicateur que vous omettez revient à sa valeur par défaut ; indiquez donc dans la configuration chaque indicateur que vous souhaitez conserver (`ConnectionName`, `IconUrl` et `OrganizationId` sont les seules valeurs qui survivent à une omission, et `CreatedAt` est conservé).

| Champ | Valeur par défaut | Effet |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Crée un utilisateur fédéré inconnu à sa première connexion. Désactivé, un utilisateur inconnu est refusé avec `access_denied` |
| `AllowUninvitedJit` | `false` | Lorsque `ProvisioningAttributeParams` est déclaré, provisionne aussi un utilisateur qui arrive sans ce contexte. Voir [SSO en libre-service](self-service-sso) |
| `ProvisioningAttributeParams` | aucun | Clés de requête de la demande d'autorisation enregistrées comme attributs de provisionnement sur un utilisateur provisionné en JIT (le pendant entrant de `PassthroughParams`) |
| `PassthroughParams` | aucun | Clés de requête transmises à l'URL d'autorisation amont ; voir [Paramètres de requête transmis](#passthrough-query-parameters) |
| `SessionExpClaim` | aucun | Voir [Plafond de durée de session](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` masque le bouton « Continuer avec » ; la connexion n'est alors accessible que par `idp_hint` |
| `ChallengeMfaAfterLogin` | `true` | `false` fait confiance à la MFA propre au fournisseur amont et ignore le défi local |
| `IsExternalConnection` | `false` | Désigne un IdP tiers appartenant au client. Neutralise `UseUpstreamSubjectAsUserId` et `AutoLinkExistingByEmail`, même s'ils sont définis |
| `UseUpstreamSubjectAsUserId` | `false` | L'identifiant local d'un utilisateur JIT est le `sub` amont au lieu d'un nouveau GUID. Connexions internes uniquement |
| `AutoLinkExistingByEmail` | `false` | Lie un compte local existant par e-mail même lorsque `AllowedDomains` ne couvre pas le domaine. Connexions internes uniquement |
| `RevalidateOnRefresh` | `false` | Voir [Sessions fédérées](federated-sessions) |
| `InteractionPath` | aucun | Chemin de l'application de connexion affiché avant la fédération d'une requête `idp_hint` (doit commencer par `/`) |
| `OrganizationId` | aucun | Limite la connexion à une organisation ; voir [SSO en libre-service](self-service-sso#organisation-scoped-connections) |

> **Un IdP sur votre propre réseau privé.** `MetadataLocation` doit être en https et, par défaut, doit pointer vers une adresse routable publiquement : Authagonal refuse les cibles internes pour chaque URL qu'il récupère, au niveau de l'URL puis de nouveau au niveau du socket. Pour fédérer avec un IdP sur site, déclarez-le dans [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Cela couvre l'ensemble de l'échange, y compris les `token_endpoint`, `userinfo_endpoint` et `jwks_uri` désignés par le document de découverte. Le https reste obligatoire : ce document fournit les clés au regard desquelles chaque `id_token` amont est validé, et un réseau privé n'est pas un canal sécurisé.

**Option B, API d'administration (pour la gestion à l'exécution) :**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

Le corps de création accepte `connectionName`, `metadataLocation`, `clientId` et `clientSecret` (tous obligatoires), ainsi que `iconUrl`, `redirectUrl` (ignoré, facultatif), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (`false` par défaut), `challengeMfaAfterLogin` (`true` par défaut) et `interactionPath`. L'identifiant de connexion est généré par le serveur et renvoyé dans le corps de la réponse `201` (le secret client n'est jamais renvoyé). `metadataLocation` doit être en https et est contrôlé par la protection des requêtes sortantes au moment de la création. Les autres indicateurs du tableau ci-dessus (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` et les autres) ne peuvent pas être définis par la route de création : initialisez-les depuis la configuration ou écrivez-les via `IOidcProviderStore` depuis le code d'hébergement. Il n'existe pas de route de mise à jour pour une connexion OIDC ; pour en modifier une, supprimez-la puis recréez-la (ou modifiez la configuration initialisée). `GET /api/v1/oidc/connections/{connectionId}` et `DELETE` complètent l'ensemble.

### 2. Acheminement par domaine SSO {#2-sso-domain-routing}

Lorsque `AllowedDomains` est indiqué (dans la configuration ou via l'API de création), les correspondances de domaines SSO sont enregistrées automatiquement. Sans acheminement par domaine, les utilisateurs peuvent tout de même être dirigés vers la connexion OIDC via `/oidc/{connectionId}/login`.

## Points de terminaison {#endpoints}

| Point de terminaison | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Lance la connexion OIDC. Génère PKCE + state + nonce, dérive le scope amont et les paramètres transmis à partir de `returnUrl`, puis redirige vers le point de terminaison d'autorisation de l'IdP (`loginHint`, lorsqu'il est présent, est envoyé en amont sous la forme `login_hint`). `404` pour une connexion inconnue. |
| `GET /oidc/callback` | Traite le rappel de l'IdP. Échange le code contre des jetons, valide l'id_token, enregistre chaque revendication hors protocole dans le cookie sous la forme `federated:*`, puis crée ou connecte l'utilisateur. |

## Transmission des scopes et des revendications {#scope-and-claim-flow-through}

L'ensemble de scopes demandé par la partie de confiance en aval sur `/connect/authorize` est transmis à l'IdP amont, **filtré sur l'ensemble OIDC standard** : `openid`, `profile`, `email`, `address`, `phone`, avec `openid` toujours inclus. Tout autre scope demandé par la partie de confiance (scopes d'API personnalisés, `offline_access`, …) est abandonné avant l'appel amont (la seule exception est une connexion avec `RevalidateOnRefresh`, qui rajoute `offline_access` afin d'obtenir un jeton d'actualisation amont) : un IdP strict comme Google renvoie `invalid_scope` pour les valeurs inconnues, et le fournisseur amont n'a besoin que d'identifier l'utilisateur ; les scopes propres à la partie de confiance sont honorés sur les jetons émis par Authagonal, pas sur ceux de l'amont. Toutes les revendications que l'IdP amont place sur l'id_token en fonction des scopes reviennent à Authagonal, sont stockées sur le ticket du cookie sous forme de revendications `federated:<name>` et sont transmises à `OidcSubject.FederationClaims` lors du passage suivant par `/connect/authorize`. De là, `ProtocolTokenService` les réémet sur les jetons émis par Authagonal, sous réserve de la même liste blanche `Scope.UserClaims` que celle qui régit `CustomAttributes`. En cas de collision de clé, la valeur du stockage d'utilisateurs propre à Authagonal l'emporte : ces revendications arrivent telles quelles de l'IdP amont ; les laisser écraser les valeurs stockées permettrait à un IdP contrôlé par un client de réaffirmer n'importe quelle revendication libérée par scope au sujet de son propre utilisateur et de l'emporter sur l'enregistrement de ce serveur. Une revendication amont sans équivalent stocké est quand même transmise.

Résultat : aucune liste d'autorisation de revendications à conserver par connexion. Chaque revendication hors protocole que l'amont place sur l'id_token est enregistrée ; celles qui parviennent aux jetons en aval sont déterminées par les `UserClaims` du scope en aval : déclarez la revendication à cet endroit et la valeur est transmise.

`FederationClaims` survit aux rotations d'actualisation, séparément de `CustomAttributes`, de sorte que le contexte de fédération propre à la session (par exemple un jeton de lien de partage enregistré lors de l'autorisation initiale) reste intact, tandis que les attributs propres à l'utilisateur sont toujours relus à jour depuis le stockage d'utilisateurs.

## Paramètres de requête transmis {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` est une liste blanche, propre à chaque connexion, de clés de requête transmises de la requête `/authorize` d'origine à l'URL d'autorisation de l'IdP amont. L'ensemble standard (`scope`, `state`, `nonce`, PKCE) est toujours transmis ; ce mécanisme concerne des valeurs supplémentaires, définies par la partie de confiance, comme un identifiant à usage unique dont l'amont a besoin pour l'authentification (par exemple `link_token` pour les IdP de liens de partage).

Lorsqu'une clé figure dans la liste blanche, Authagonal récupère sa valeur dans la requête `/authorize` d'origine (transportée via `returnUrl`) et l'ajoute à l'URL amont. Tout ce qui ne figure pas dans la liste blanche est abandonné sans avertissement.

## Plafond de durée de session {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` est le nom facultatif d'une revendication de l'id_token (en secondes Unix) dont la valeur plafonne la durée de la session locale. Lorsqu'elle est présente, la valeur amont est transmise sous la forme `session_max_exp` sur le ticket du cookie et dans le code d'autorisation émis ; les jetons d'accès, d'identité et d'actualisation sont bornés de sorte qu'aucun jeton, y compris ceux émis lors des rotations, ne survive à la session amont. Utile lorsque l'IdP amont impose des limites de session plus courtes que celles qu'Authagonal appliquerait par défaut.

## Fonctionnalités de sécurité {#security-features}

- **PKCE** : code_challenge en S256 sur chaque requête d'autorisation
- **Validation du nonce** : le nonce est stocké avec le state, et doit être présent dans l'id_token et correspondre
- **Validation du state** : à usage unique (consommé de façon atomique via `IOidcStateStore`, conservé avec une date d'expiration) **et lié au navigateur** : un cookie `SameSite=Lax` limité à `/oidc` est défini à la connexion et doit correspondre au `state` du rappel, de sorte qu'un attaquant ne peut pas terminer un flux de fédération qu'il a lui-même lancé en transmettant l'URL de rappel à une victime (CSRF de connexion)
- **Validation de la signature de l'id_token** : clés récupérées depuis le point de terminaison JWKS de l'IdP ; émetteur, audience et durée de validité vérifiés
- **Repli sur userinfo** : si l'id_token ne contient pas d'e-mail, le point de terminaison userinfo est interrogé. Le `sub` de userinfo doit correspondre au `sub` de l'id_token (OIDC Core 5.3.2), sinon la réponse est ignorée
- **Liaison d'identité stable** : un utilisateur qui revient est identifié par le fournisseur + le `sub`, jamais par l'e-mail seul. Rattacher une identité fédérée à un compte local **déjà existant** par e-mail exige que les `AllowedDomains` de la connexion couvrent le domaine de cet e-mail (l'administrateur garantit ainsi explicitement que l'IdP en est propriétaire) ou que `AutoLinkExistingByEmail` soit défini sur une connexion interne, et c'est refusé lorsque le domaine est acheminé vers une autre connexion. Un compte déjà lié à l'identité fédérée d'une autre connexion n'est repris que si cette connexion fait autorité pour le domaine, auquel cas l'ancienne liaison est supprimée. Un `email_verified` affirmé par l'amont ne suffit *pas* à s'approprier un compte existant
- **Contrôle des domaines** : lorsque `AllowedDomains` est défini, la connexion ne peut affirmer que des identités appartenant à ces domaines (`access_denied` sinon)
- **Le JIT est à activer explicitement** : à moins que la connexion ne définisse `JitProvisioningEnabled`, un utilisateur inconnu est refusé avec `access_denied`. Lorsque le JIT s'applique, un amont qui n'affirme pas `email_verified` ne peut pas créer de compte, pas plus qu'une connexion dont le domaine de messagerie est acheminé vers une autre connexion
- **Protection contre les redirections ouvertes** : `returnUrl` doit être un chemin relatif sur le même site ; les formes relatives au protocole (`//`) et avec barre oblique inverse sont refusées
- **La MFA locale s'applique toujours par défaut** : la fédération ne prouve que le premier facteur. Un utilisateur enrôlé en MFA (ou dont la politique du client exige la MFA) passe par les pages locales de défi ou de configuration MFA après le rappel au lieu d'être connecté directement ; ce n'est qu'à ce moment que la session porte le marqueur MFA. Une connexion avec `ChallengeMfaAfterLogin: false` ignore cette étape et connecte l'utilisateur comme authentifié par MFA sur la seule base de la fédération
- **Confiance limitée dans les métadonnées** : le document de découverte doit être en https et son URL liée à l'émetteur qu'il désigne, et les id_tokens amont ne sont acceptés qu'avec des algorithmes de signature asymétriques (RS/PS/ES 256, 384, 512)
- **Rattachement à l'organisation** : un utilisateur qui se connecte via une connexion propre à une organisation devient membre de cette organisation, et la session porte son `org_id`

## Particularités d'Azure AD {#azure-ad-specifics}

Azure AD renvoie parfois les e-mails sous forme de tableau JSON dans la revendication `emails` (en particulier pour B2C). Authagonal gère ce cas en vérifiant à la fois la revendication `email` et le tableau `emails` (tableau JSON ou chaîne unique).

## Fournisseurs pris en charge {#supported-providers}

Tout fournisseur conforme à OIDC qui prend en charge :
- le flux code d'autorisation
- PKCE (S256)
- le document de découverte (`.well-known/openid-configuration`)

Testé avec :
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Guides associés {#related-guides}

- [SSO en libre-service](self-service-sso) : modes de provisionnement JIT (sur invitation uniquement ou en libre-service), niveau de confiance des connexions et pages intermédiaires avant la fédération.
- [Sessions fédérées](federated-sessions) : propager la révocation amont à la session locale avec `RevalidateOnRefresh`.
- [Promotion d'un utilisateur](user-upgrade) : permettre à un compte fédéré ou invité de réclamer un mot de passe interne.
