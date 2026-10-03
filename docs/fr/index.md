---
layout: default
title: Accueil
locale: fr
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Serveur d'authentification OAuth 2.0 / OpenID Connect / SAML 2.0 pour .NET, adossé à un stockage interchangeable : votre propre PostgreSQL ou SQLite, Azure Table Storage, ou AWS (DynamoDB / S3 / Secrets Manager).

Un déploiement unique et autonome. Le serveur et l'interface de connexion sont livrés dans une seule image Docker, et la SPA est servie depuis la même origine que l'API : l'authentification par cookie, les redirections et la CSP fonctionnent donc sans les complications du cross-origin.

> **Vous préférez un service managé ?** [Authagonal Cloud](https://authagonal.io) fait tourner tout cela pour vous, en multi-locataire, avec toutes les fonctionnalités sur toutes les offres et sans frais de SSO par connexion. → [authagonal.io](https://authagonal.io)

## Fonctionnalités clés {#key-features}

- **Fournisseur OIDC** : octrois authorization_code + PKCE, client_credentials, refresh_token et device_code, avec rotation à usage unique
- **Fournisseur de services SAML 2.0** : implémentation maison avec prise en charge complète d'Azure AD (réponse signée, assertion signée, ou les deux), une paire de clés SP par connexion pour les AuthnRequests signées et le déchiffrement de `EncryptedAssertion`, et le Single Logout (initié par le SP comme par l'IdP)
- **Fédération OIDC dynamique** : connexion à Google, Apple, Azure AD ou tout IdP conforme à OIDC
- **Authentification multifacteur** : TOTP, WebAuthn/passkeys, codes de récupération ; politique par client (`Disabled` / `Enabled` / `Required`) avec surcharge par utilisateur via `IAuthHook`, appliquée aussi aux connexions fédérées
- **Provisionnement SCIM 2.0** : provisionnement entrant d'utilisateurs et de groupes depuis Entra ID, Okta, OneLogin ; listes paginées par curseur et filtres `eq` adossés à un index aveugle
- **Écran de consentement OAuth** : consentement par client, avec nouvelle demande en fonction des scopes et gestion des octrois
- **Octroi d'autorisation d'appareil** : flux RFC 8628 pour les appareils à saisie limitée (téléviseurs connectés, CLI, IoT)
- **Introspection de jetons** : RFC 7662, pour que les serveurs de ressources vérifient la validité d'un jeton
- **Signature des jetons** : ES256 uniquement. Les jetons d'accès portent le `typ: at+jwt` de la RFC 9068 afin qu'un serveur de ressources
  puisse les distinguer des id_tokens et des jetons de déconnexion, mais **la conformité à la RFC 9068 n'est pas revendiquée** : le §2.1
  impose RS256 parmi les algorithmes pris en charge, et ce serveur ne l'émet ni ne l'accepte. Un
  algorithme unique est un choix délibéré : chaque algorithme supplémentaire accepté est un moyen de plus d'amener un
  vérificateur à utiliser le mauvais.
- **Back-Channel Logout** : notifications OIDC Back-Channel Logout 1.0 envoyées aux parties de confiance
- **Sessions côté serveur** *(optionnel)* : `AddAuthagonalServerSideSessions` conserve le ticket SSO dans le stockage, de sorte que le cookie d'authentification ne porte qu'un identifiant opaque, et active la liste en libre-service `GET /api/auth/sessions` ainsi que la révocation par appareil ([API d'authentification](auth-api#sessions-self-service))
- **Backend-for-Frontend** : `Authagonal.Bff` (.NET) et `@authagonal/bff` (Node), un BFF client confidentiel pour qu'une SPA ne détienne jamais de jeton ([BFF](bff))
- **RGPD en libre-service** *(Authagonal Cloud)* : export des données et suppression programmée du compte depuis la page de compte
  hébergée. L'application de connexion fournit l'interface correspondante, mais les endpoints qu'elle appelle
  (`GET /api/v1/account/export`, `POST /api/v1/account/erasure`) sont servis par l'hôte d'authentification Cloud et ne font
  **pas** partie de la surface de cette bibliothèque. Un déploiement auto-hébergé doit les implémenter, ou retirer les deux
  boutons de sa page de compte : `MapFallbackToFile` répond à une route non implémentée par un 200 et le HTML propre à la
  SPA, si bien qu'un export non implémenté doit être reconnu comme tel plutôt que téléchargé.
- **Provisionnement TCC** : provisionnement Try-Confirm-Cancel dans les applications en aval au moment de l'autorisation
- **Interface de connexion personnalisable** : configurable à l'exécution via un fichier JSON (logo, couleurs, propriétés CSS personnalisées), sans recompilation ; traduite en 11 langues
- **Hooks d'authentification** : extensibilité `IAuthHook` pour la journalisation d'audit, la validation personnalisée, les webhooks
- **Points d'extension pour le chiffrement des données personnelles** : `IFieldCipher` / `IIndexTokenizer` pour le chiffrement au repos champ par champ avec une recherche par index aveugle à clé (HMAC) ; codes de récupération chiffrés via `ISecretProvider`
- **Client HashiCorp Vault Transit** : signature/vérification, chiffrement/déchiffrement et HMAC à clé via le moteur Transit de Vault, pour construire un `IFieldCipher` ou un `IIndexTokenizer`. La signature distante des JWT n'est pas branchée : la clé de signature des jetons est toujours celle de `ISigningKeyStore`.
- **Bibliothèque composable** : `AddAuthagonal()` / `UseAuthagonal()` pour l'héberger dans votre propre projet avec vos propres surcharges de services
- **Prêt pour Native AOT** : élagage IL et sérialisation JSON générée à la compilation pour un démarrage rapide
- **Stockage interchangeable** : PostgreSQL ou SQLite auto-hébergés (aucun compte cloud requis), ou Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) pour des backends économiques et adaptés au serverless
- **Sauvegarde et restauration** : sauvegardes incrémentales (pilotées par un journal des modifications, avec un balayage complet de secours), vérification d'intégrité, suivi des suppressions par marqueurs de suppression (tombstones)
- **API d'administration** : CRUD des utilisateurs, gestion des fournisseurs SAML/OIDC, routage SSO par domaine, emprunt d'identité par jeton

## Intégrations courantes {#common-integrations}

Des guides orientés tâche pour les flux que les équipes construisent le plus souvent :

- **[Promouvoir un utilisateur](user-upgrade)** : transformer un compte invité / SSO / sur invitation en compte avec identifiants grâce à la réclamation de compte sans mot de passe, et exécuter votre promotion invité → membre standard à la confirmation.
- **[SSO en libre-service](self-service-sso)** : provisionnement JIT pour les connexions d'entreprise : intégration sur invitation seulement ou en libre-service, comment éviter que les IdP externes deviennent des pièges, et pages intermédiaires avant la fédération.
- **[Sessions fédérées](federated-sessions)** : révoquer la session locale quand l'IdP amont révoque la sienne (`RevalidateOnRefresh`).
- **[Backend-for-Frontend (BFF)](bff)** : garder les jetons hors du navigateur : un client OIDC confidentiel sur votre backend, avec un cookie de session httpOnly et un proxy d'API qui injecte le jeton, en .NET ou en Node.
- **[Authentification WebSocket](websocket-auth)** : authentifier les WebSockets du navigateur via le BFF sans exposer de jeton.
- **[Authentification agentique](agentic-auth)** : déléguer l'autorité d'un utilisateur à des agents d'IA : agents enregistrés, autorité fine selon la RFC 9396, jetons de délégation composites (`act` de la RFC 8693), consentement permanent, approbations juste-à-temps, tickets de capacité.
- **[Organisations](organizations)** : servir de nombreux clients depuis un seul locataire : enregistrements `Organization` et d'appartenance, paramètre d'autorisation `organization`, `org_id` / `org_slug` / `org_name` dans les jetons, rôles limités à une organisation, et refus des non-membres.

## Architecture {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Commencez par le guide d'[Installation](installation) ou passez directement au [Démarrage rapide](quickstart). Pour héberger Authagonal dans votre propre projet, consultez [Extensibilité](extensibility). Pour la gestion des données, consultez [Sauvegarde et restauration](backup-restore). Pour l'historique complet des changements, consultez le [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
