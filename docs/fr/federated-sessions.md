---
layout: default
title: Sessions fédérées
locale: fr
---

# Synchroniser les sessions fédérées avec l'amont

Lorsqu'un utilisateur se connecte via un [IdP externe](oidc-federation), Authagonal émet sa *propre* session et
ses *propres* jetons. Par défaut, cette session locale vit ensuite sa propre vie : si le client désactive
l'utilisateur dans son annuaire, ou si le lien de partage de l'invité est révoqué en amont, la session Authagonal
locale continue de fonctionner jusqu'à l'expiration de son cookie.

Pour que les départs et les révocations soient pris en compte rapidement, activez **`RevalidateOnRefresh`**. Dès lors,
à chaque actualisation locale de jeton, Authagonal échange le jeton d'actualisation amont auprès de l'IdP et, si
l'amont indique que l'identifiant n'existe plus, l'actualisation locale est refusée et la session cesse d'obtenir de
nouveaux jetons en l'espace d'une durée de vie de jeton d'accès.

`RevalidateOnRefresh` est un paramètre propre aux **connexions OIDC**. SAML n'a pas de jeton d'actualisation à
échanger : une connexion SAML ne peut donc pas revalider ; limitez plutôt ces sessions au moyen de l'expiration de
session portée par l'assertion elle-même (voir [SAML](saml)).

## L'activer {#enable-it}

Par connexion (définie dans la configuration comme ci-dessous, ou fixée lors de la création de la connexion via
l'[API d'administration](admin-api)), et l'amont doit effectivement émettre un jeton d'actualisation :

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

C'est toute la configuration nécessaire. Lorsque l'option est activée, Authagonal ajoute `offline_access` au scope
qu'il demande à l'amont (si la requête en aval ne le portait pas déjà), conserve le jeton d'actualisation renvoyé par
l'amont et l'échange de serveur à serveur à chaque actualisation locale. Le jeton d'actualisation amont n'est
**jamais** transmis à un client. Il est conservé chiffré dans un stockage durable par session, enregistré à la
connexion avec une expiration fixe de sept jours (le plafond absolu de session), et sert uniquement à la
revalidation.

L'amont doit coopérer : si son inscription d'application ne reçoit jamais `offline_access` (par exemple parce que le
consentement n'a pas été accordé), aucun jeton d'actualisation n'est renvoyé et il n'y a rien à échanger. Dans cet
état, Authagonal journalise un avertissement
(`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`) à chaque actualisation,
et l'amont n'est **pas** revérifié.

## Ce qui se passe lors d'une actualisation {#what-happens-on-refresh}

1. La RP actualise un jeton Authagonal comme d'habitude (`grant_type=refresh_token` sur `/connect/token`).
2. Authagonal échange le jeton d'actualisation amont auprès du point de terminaison de jeton de l'IdP :
   - **Succès** → l'actualisation locale se poursuit ; si l'amont a fait tourner son jeton, le nouveau est stocké
     et partagé par tous les octrois de RP de la session.
   - **`invalid_grant`** → l'identifiant fédéré n'existe plus (utilisateur désactivé, session révoquée, jeton
     expiré). L'actualisation locale est **refusée** : la RP reçoit `invalid_grant` de `/connect/token`, et le
     jeton amont stocké est supprimé. Le refus ne fait échouer que cette requête ; il ne révoque pas l'octroi
     Authagonal, de sorte que le jeton d'actualisation de la RP n'est pas consommé et continue d'être refusé tant que
     l'amont reste révoqué.
   - **Toute autre erreur 4xx** (par exemple `invalid_client` dû à un secret renouvelé ou mal configuré, ou un 429),
     une erreur 5xx, un corps d'erreur illisible, une défaillance de transport, ou une connexion impossible à charger
     (supprimée, échec de la découverte ou du secret) → traité comme **transitoire** : la session est maintenue, afin
     qu'une erreur d'exploitation ne déconnecte pas en masse tous les utilisateurs fédérés. Corrigez la
     configuration ; rien n'est perdu. La session reste bornée par le plafond absolu de session.

Comme il existe **un seul** jeton amont par session de navigateur (indexé par utilisateur + connexion + session),
une seconde RP ouverte par l'utilisateur lit et fait tourner le *même* jeton : l'actualisation d'une application ne
peut donc pas laisser une autre application avec une copie périmée.

## Rien à implémenter {#nothing-to-implement}

Il n'y a aucune interface à écrire ici. Le stockage durable (`IUpstreamRefreshTokenStore`) est enregistré
automatiquement par les fournisseurs de stockage Azure, AWS et SQL, et l'échange est interne. Il vous suffit
d'activer `RevalidateOnRefresh` sur les connexions dont l'amont détient un identifiant révocable.

Le jeton stocké est supprimé lorsque la session prend fin, quelle que soit la voie qui y aboutit : déconnexion,
révocation d'une session depuis la page du compte, « se déconnecter partout » et balayage des expirations. Si un
hôte n'enregistre aucun stockage, la copie portée par le cookie de session sert de solution de repli.

Chaque session fédérée par OIDC enregistre aussi la connexion à laquelle elle appartient (la revendication
`upstream_connection_id`), que cette connexion revalide ou non. Il ne s'agit que de comptabilité : rien n'est
échangé pour une connexion dépourvue de l'option.

> **Portée :** cette fonctionnalité est active partout où le stockage est enregistré (fournisseurs Azure Table,
> DynamoDB et SQL). Activez-la sur les connexions vers un amont **de confiance**, en particulier un amont qui fait
> tourner ses jetons d'actualisation à usage unique (Entra, Auth0), et combinez-la avec `IsExternalConnection` pour
> les IdP tiers (voir [SSO en libre-service](self-service-sso)).

## En complément : un plafond de session strict {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` maintient la session en phase avec les révocations en amont. Si vous souhaitez plutôt (ou en
plus) que la session locale ne *survive* jamais à la session affirmée par l'amont, définissez `SessionExpClaim` sur
le nom d'une revendication de l'id_token portant une expiration (en secondes Unix). Authagonal borne à cette limite
la session locale et tous les jetons émis à partir d'elle (y compris les rotations d'actualisation, et y compris un
octroi device-code approuvé via cette session). Voir
[Fédération OIDC → Plafond de durée de session](oidc-federation).

Le flux device mérite d'être cité, car il faisait exception jusqu'à récemment : l'enregistrement device-code n'avait
aucun moyen de porter la limite de la session qui l'approuvait ; un appareil approuvé via une session fédérée
continuait donc d'émettre des jetons pendant toute la durée de vie absolue d'actualisation du client après la fin de
cette session, et `RevalidateOnRefresh` ne réinterrogeait jamais l'amont à son sujet non plus. Les deux suivent
désormais la session d'approbation. Un appareil approuvé via une session *non fédérée* n'a aucune limite à hériter,
ce qui correspond au résultat que donne le flux d'autorisation.
