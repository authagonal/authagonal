---
layout: default
title: Démarrage rapide
locale: fr
---

# Démarrage rapide

Faites tourner Authagonal en local en 5 minutes.

## 1. Démarrer le serveur {#1-start-the-server}

```bash
docker compose up
```

Cette commande lance Authagonal sur `http://localhost:8080`, avec Azurite pour le stockage.

> Le fichier compose définit `Auth__AllowInsecureHttp=true`, car les §3.1/§3.2 de la RFC 6749 exigent TLS sur les endpoints d'autorisation et de jeton, et Authagonal refuse sinon les requêtes en clair vers `/connect/*`. Ce réglage est réservé à un poste de développement. Tout ce qui est accessible à d'autres personnes doit se trouver derrière un proxy qui termine TLS et transmet `X-Forwarded-Proto: https`, sans ce réglage : voir [Installation](installation).

## 2. Vérifier qu'il fonctionne {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Enregistrer un client {#3-register-a-client}

Ajoutez un client à votre `appsettings.json` (ou passez-le par des variables d'environnement) :

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Les clients sont initialisés au démarrage, ce qui peut s'exécuter sans risque à chaque déploiement.

## 4. Lancer une connexion {#4-initiate-a-login}

Redirigez vos utilisateurs vers :

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

L'utilisateur voit la page de connexion, s'authentifie, puis est redirigé vers votre application avec un code d'autorisation.

> **Premier utilisateur :** inscrivez-en un sur `http://localhost:8080/login/register`, ou créez-le via l'[API d'administration](admin-api). L'inscription en libre-service envoie un e-mail de vérification ; sans expéditeur d'e-mail configuré (la valeur par défaut en local), ce message est ignoré. Pour vos tests en local, définissez donc `Auth__AutoConfirmEmailDomains__0=example.dev` (n'importe quel domaine avec lequel vous vous inscrivez) pour sauter la vérification, ou configurez `Email:ResendApiKey` + `Email:SenderEmail`. Voir [Configuration → E-mail](configuration#email).

## 5. Échanger le code {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Réponse :

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` correspond au `AccessTokenLifetimeSeconds` du client (1800 pour un client initialisé par configuration, sauf si vous le définissez). Aucun `refresh_token` n'apparaît ici : un client n'en reçoit un que s'il active `AllowOfflineAccess` et que la requête demande le scope `offline_access`.

## Démo fonctionnelle {#working-demo}

Le répertoire `demos/sample-app/` contient une SPA React complète avec son API, qui implémente l'intégralité du flux OIDC ci-dessus. Les instructions se trouvent dans le [README des démos](https://github.com/authagonal/authagonal/tree/master/demos).

## Étapes suivantes {#next-steps}

- [Configuration](configuration) : référence complète de tous les paramètres
- [Extensibilité](extensibility) : héberger Authagonal comme bibliothèque, ajouter des hooks personnalisés
- [Personnalisation de l'interface](branding) : adapter l'interface de connexion
- [SAML](saml) : ajouter des fournisseurs SSO SAML
- [Provisionnement](provisioning) : provisionner les utilisateurs dans les applications en aval
