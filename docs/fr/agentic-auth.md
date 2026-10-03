---
layout: default
title: Authentification agentique
locale: fr
---

# Authentification agentique

Authagonal fournit les briques nécessaires pour déléguer en toute sécurité l'autorité d'un utilisateur à des agents
d'IA (ou à toute charge de travail non humaine) : agents enregistrés, octrois d'autorité à granularité fine, jetons de
délégation composites, consentement permanent de l'utilisateur, approbations juste-à-temps, tickets de capacité et
une surface d'audit consciente de la délégation. La bibliothèque possède les primitives et l'invariant ; l'application
hôte les assemble en un produit (les implémentations de connecteurs, l'expérience d'approbation, l'envoi des
notifications et la politique métier restent du côté de l'hôte).

## L'invariant {#the-invariant}

Tout jeton délégué obéit à :

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Rien en aval ne peut l'élargir ; chaque saut de délégation supplémentaire effectue à nouveau l'intersection, si bien
que l'autorité ne fait jamais que se restreindre. L'intersection est implémentée une seule fois
(`AuthoritySet.Intersect`) et utilisée partout.

## Entités {#entities}

| Entité | Type | Remarques |
|---|---|---|
| Agent | `AgentProfile` sur un `OAuthClient` confidentiel | C'est l'enregistrement d'un profil qui fait d'un client un agent ; le supprimer ramène le client au simple OAuth. |
| Autorité | `AuthoritySet` / `AuthorityGrant` | Forme `authorization_details` de la RFC 9396 : `type` de connecteur, `actions`, `locations`, contraintes, politiques `auto`/`ask`/`deny` par action. |
| Plafond | `AgentProfile.Ceiling` | L'autorité la plus large que puisse porter une délégation passant par l'agent. Gérée par l'administrateur (`/api/v1/agents`). |
| Consentement (plancher) | `PersistedGrant` de type `agent_consent` | Par couple (utilisateur, agent), géré via `/consent/agents`. Stocké déjà intersecté avec le plafond, puis réintersecté à chaque émission. |
| Délégation | Échange de jetons RFC 8693 | Identité composite : `sub` = utilisateur, `act` = agent (imbriqué à chaque saut), `authorization_details` = l'intersection effective. De courte durée, jamais actualisable. |
| Approbation | `PersistedGrant` de type `approval` | Barrière juste-à-temps pour les actions à politique `ask` ; sémantique d'interrogation du flux device ; à usage unique, liée à la forme de la requête. |
| Ticket de capacité | `ICapabilityTicketService` | Référence opaque à usage unique liée à un jeton : le ws-ticket du BFF généralisé, atomique sur le stockage des octrois. |
| Audit | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync`, plus la barrière préalable à l'émission `OnTokenIssuingAsync`. |

## Enregistrer un agent {#registering-an-agent}

1. Créez un client confidentiel autorisant `urn:ietf:params:oauth:grant-type:token-exchange`
   (mode délégué) et/ou `client_credentials` (mode service).
2. `PUT /api/v1/agents/{clientId}` :

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` vaut `delegated`, `service` ou `both` (un `mode` omis lors d'une mise à jour conserve la valeur existante). `maxDelegationDepth` doit être compris entre 0 et 8 (0 par défaut), `maxTokenLifetimeSeconds` entre 30 et 86400 (300 par défaut), et `highRiskDefault` doit valoir `auto`, `ask` ou `deny` ; toute autre valeur donne un 400.

Les membres de contrainte sont typés selon leur forme JSON : chaîne ou tableau de chaînes → liste d'autorisation
(combinaison par intersection d'ensembles ; les entrées acceptent la correspondance exacte, le joker `*.host` et la
correspondance `@suffix`), nombre → plafond (combinaison par minimum), booléen → barrière (combinaison par ET). Les
membres non interprétables sont conservés tels quels et échouent en mode fermé lors de l'évaluation.
`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` donne un aperçu de plafond ∩ consentement pour
l'interface d'administration.

## Consentement de l'utilisateur (le plancher) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info` : le plafond présenté au regard du catalogue de connecteurs
  (enregistrez un `IConnectorCatalog` pour les noms d'affichage, les descriptions d'actions et les indicateurs de
  risque élevé ; ses types sont annoncés dans la découverte sous `authorization_details_types_supported`).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }` : accorde le plancher (omettez
  `authority` pour consentir au plafond complet). Un utilisateur peut durcir une politique (`auto` → `ask`)
  mais jamais l'assouplir ni l'élargir : le stockage effectue l'intersection préalable avec le plafond en vigueur.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}` : lister et révoquer. La révocation
  bloque l'émission suivante ; les délégations en cours ne sont pas actualisables et expirent au terme de leur durée
  de vie (courte). Sans consentement → l'échange échoue avec `invalid_grant` /
  `consent_required` ; le plafond seul n'accorde rien.

## Émettre une délégation {#minting-a-delegation}

L'agent s'authentifie en son nom propre et échange le jeton de l'utilisateur :

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

L'émission applique, dans l'ordre : le mode de l'agent, le consentement permanent, la profondeur de sous-délégation
(chaque acteur déjà présent dans la chaîne `act` doit disposer d'un budget `maxDelegationDepth` suffisant pour un saut
de plus), l'intersection, les refus de requêtes explicites (`invalid_target` : un agent ne doit pas croire qu'il
détient une autorité qu'il n'a pas), la barrière ask et les bornes de durée de vie (durée de vie du client ∩ reste du
jeton sujet ∩ `maxTokenLifetimeSeconds`). Le jeton porte `act` (RFC 8693 ; imbriqué à chaque saut) et
`authorization_details` (RFC 9396) ; la réponse renvoie les détails accordés ; l'introspection émet les deux.
Échanger à nouveau un jeton délégué le restreint automatiquement davantage, car la revendication propre au jeton
sujet entre dans l'intersection.

Les clients **sans** profil d'agent conservent exactement le comportement d'échange actuel, à ceci près qu'un
paramètre de requête `authorization_details` restreint désormais (sans jamais l'élargir) le jeton échangé.

## Approbations (barrière ask) {#approvals-ask-gate}

Lorsque la part effective contient une action `ask`, l'échange est mis en attente :

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

L'hôte est notifié via `IAuthHook.OnApprovalRequestedAsync` (l'envoi par e-mail, notification push ou messagerie
relève de l'hôte). L'utilisateur tranche (`GET /approvals`, `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }`), tandis que l'agent renouvelle la requête identique en y ajoutant
`approval_id`, avec le vocabulaire du flux device de bout en bout (`slow_down`, `access_denied`,
`expired_token`). Les approbations sont à usage unique (consommation atomique), expirent après
`ApprovalLifetimeSeconds` (300 par défaut) et sont liées à la forme exacte de la requête *et à l'état actuel des
politiques* : une modification du plafond par l'administrateur entre la mise en attente et l'interrogation invalide
l'approbation au lieu d'émettre une autorité périmée. Une approbation consommée émet un jeton dont les actions `ask`
sont résolues en `auto` (la question a été posée et a obtenu une réponse).

Le mode service (`client_credentials`) n'implique aucun utilisateur : le plafond s'applique seul et
`ask` se dégrade en `deny`.

## Application côté ressource {#resource-side-enforcement}

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` dans n'importe quel serveur de
  ressources (les clés du contexte sont comparées aux noms des contraintes ; transmettez ce que vous pouvez en
  déduire, par exemple `recipient_domains` lors de l'envoi d'un e-mail). Un jeton dépourvu de la revendication est
  évalué sans restriction (compatibilité ascendante) ; une revendication altérée est évaluée comme un refus total.
  - `location` est la valeur `locations` de la RFC 9396 à laquelle vous agissez. Un octroi qui nomme des
    emplacements n'est honoré qu'à ceux-ci ; un emplacement accordé est une **racine**, de sorte que
    `https://api.example.com/orders` couvre `/orders/17` mais pas `/orders-admin`.
  - `strict: true` refuse lorsque l'appelant n'a fourni aucun contexte pour une contrainte, au lieu de l'ignorer.
    Utilisez-le partout où vous pouvez énumérer toutes les clés que vous prenez en charge :
    `AuthoritySet.UncheckedConstraints(type, context)` nomme celles que vous n'avez pas vérifiées.
- Point de contrôle du BFF : `BffUpstream.RequiredAuthority = ["email:send"]` fait vérifier par le proxy le jeton
  bearer sortant avant le relais, avec un 403 en cas d'échec, et aucun passage anonyme. L'emplacement qu'il présente
  est l'amont que la requête atteindra réellement (`AuthorityLocation` remplace la racine lorsque l'autorité est émise
  pour un identifiant public plutôt que pour l'adresse interne) ; `StrictAuthority` fait refuser par le proxy une
  contrainte qu'il ne peut pas évaluer au lieu de la laisser à l'amont.

## Tickets de capacité {#capability-tickets}

`ICapabilityTicketService` (par défaut `GrantStoreCapabilityTicketService`, enregistré avec `TryAdd`
par `AddAuthagonalCore`, donc également obtenu par `AddAuthagonal`) émet des références opaques à usage unique liées à
un jeton, échangées de façon atomique grâce à la suppression conditionnelle du stockage des octrois : durables
et protégées contre le rejeu d'un pod à l'autre, contrairement au lire-puis-supprimer d'un simple cache. Le ws-ticket
du BFF conserve son contrat existant de cache distribué (`WsTicketKey` / `TryRedeemWsTicketAsync`), car son
consommateur est généralement un hôte distinct qui ne partage que Redis ; les intermédiaires hébergés au même endroit
devraient préférer le service de tickets de capacité.

## private_key_jwt {#private_key_jwt}

Les agents sont des charges de travail ; les secrets partagés sont le maillon le plus faible de la chaîne. Définissez
`OAuthClient.JwksJson` (JWKS en ligne) ou `JwksUri` (récupéré, mis en cache environ 10 min) et authentifiez-vous
avec une assertion client RFC 7523 (`client_assertion_type=…:jwt-bearer`). Sont imposés :
la signature vérifiée contre le JWKS enregistré, `iss` = `sub` = `client_id`, une audience égale à l'émetteur ou au
point de terminaison de jeton, un `exp` borné (≤ 10 min) et un `jti` à usage unique (cache anti-rejeu reposant sur
`IRevokedTokenStore`). Une assertion présente ne bascule jamais vers la voie du secret.

## Compatibilité {#compatibility}

- Sans profil d'agent → aucun changement de comportement, quel que soit le flux. Toutes les nouvelles tables et
  colonnes sont nullables par défaut et provisionnées automatiquement sur les deux fournisseurs de stockage (table
  `AgentProfiles` ; `JwksJson`/`JwksUri` sur les clients ; consentements, approbations et tickets s'appuient sur la
  table des octrois existante).
- Les nouveaux membres de `IAuthHook` sont des méthodes d'interface par défaut ; les hooks existants compilent sans
  modification.
- `ITokenExchangeSubjectTransformer` s'exécute toujours à chaque échange et peut rejeter ou lier des revendications de
  contexte ; il ne peut jamais élargir la délégation (sa sortie est réintersectée) ni toucher à la chaîne `act`
  (revendication réservée).
