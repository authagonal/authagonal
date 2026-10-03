---
layout: default
title: API d'authentification
locale: fr
---

# API d'authentification

Ces endpoints font fonctionner la SPA de connexion. Ils utilisent l'authentification par cookie (`SameSite=Lax`, `HttpOnly`).

Si vous construisez une interface de connexion personnalisée, ce sont les endpoints sur lesquels vous devez vous appuyer.

## Endpoints {#endpoints}

### Connexion {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Succès (200) :** définit un cookie d'authentification et renvoie :

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` vaut `true` lorsque la `MfaPolicy` du client est `Enabled` mais que l'utilisateur ne s'est pas encore enrôlé (l'interface peut proposer la configuration) ; dans ce cas, un champ `clientId` est également inclus.

**MFA requise (200) :** si l'utilisateur est enrôlé dans la MFA, elle lui est **toujours** demandée, quelle que soit la `MfaPolicy` du client à l'origine de la requête (la MFA est une propriété de l'utilisateur et de la session, pas du client) :

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Le client doit rediriger vers une page de vérification MFA et appeler `POST /api/auth/mfa/verify`.

**Configuration de la MFA requise (200) :** si `MfaPolicy` vaut `Required` et que l'utilisateur n'est enrôlé dans aucune MFA :

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Le client doit rediriger vers une page de configuration de la MFA. Le jeton de configuration authentifie l'utilisateur auprès des endpoints de configuration de la MFA via l'en-tête `X-MFA-Setup-Token`.

**Réponses d'erreur :**

| `error` | Statut | Description |
|---|---|---|
| `invalid_credentials` | 401 | Adresse e-mail ou mot de passe incorrect. Délibérément identique pour les adresses inconnues (protection contre l'énumération). |
| `locked_out` | 423 | Trop de tentatives échouées. `retryAfter` (en secondes) est inclus. |
| `account_disabled` | 403 | Le compte est désactivé (signalé uniquement après un mot de passe correct) |
| `email_not_confirmed` | 403 | Adresse e-mail pas encore vérifiée (signalé uniquement après un mot de passe correct) |
| `sso_required` | 409 | Le domaine exige le SSO. `redirectUrl` pointe vers la connexion SSO. |
| `captcha_failed` | 400 | La vérification Turnstile a échoué (uniquement lorsque Turnstile est configuré ; les requêtes doivent alors comporter un champ `turnstileToken`) |
| `email_required` | 400 | Le champ e-mail est vide |
| `password_required` | 400 | Le champ mot de passe est vide |

### Inscription {#register}

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crée un compte utilisateur et envoie un e-mail de vérification. Renvoie `201 { "success": true, "userId": "..." }`. Champs facultatifs : `locale` (balise BCP-47 enregistrée sur l'utilisateur) et `customAttributes` (un dictionnaire de chaînes).

L'inscription est délibérément **neutre face à l'énumération** : si l'adresse e-mail est déjà enregistrée, la réponse est le même `201` neutre (avec un `userId` jetable), et le véritable titulaire reçoit à la place un e-mail l'invitant à se connecter ou à réinitialiser son mot de passe. L'inscription est aussi limitée en débit par adresse IP, avec `429 rate_limited` en cas de dépassement (fenêtre et plafond configurables via `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmer l'adresse e-mail {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirme l'adresse e-mail de l'utilisateur à l'aide du jeton contenu dans l'e-mail de vérification. `GET` correspond au lien cliquable de l'e-mail et redirige vers `/login?email_confirmed=1` (avec un paramètre `continue_client` lorsque l'inscription provenait d'un flux OAuth). `POST` est le chemin programmatique et renvoie du JSON (le jeton peut aussi être fourni dans un corps JSON sous la forme `{ "token": "..." }`) ; la réponse comporte un `appLink` facultatif (cible « continuer vers l'application »).

### Fournisseurs {#providers}

```
GET /api/auth/providers
```

Renvoie la liste des fournisseurs d'identité externes configurés (pour afficher les boutons SSO) :

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Les connexions pour lesquelles `AllowedDomains` est configuré sont **exclues** : on y accède en saisissant d'abord l'adresse e-mail, via `/api/auth/sso-check`, plutôt que par un bouton. `turnstileSiteKey` est renseigné lorsque Cloudflare Turnstile est configuré (l'interface de connexion doit alors envoyer un `turnstileToken` avec les requêtes de connexion, d'inscription et de mot de passe).

### Déconnexion {#logout}

```
POST /api/auth/logout
```

Met fin à la session de l'appelant de la même manière que `/connect/endsession` : des jetons de déconnexion back-channel sont envoyés aux parties de confiance qui ont enregistré une URI, les octrois émis pour cette session sont révoqués et le cookie d'authentification est effacé. Exige l'authentification par cookie et une requête de même origine. Renvoie `200` :

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` liste les URL de déconnexion front-channel que l'appelant doit charger (dans des iframes masquées) pour achever la déconnexion dans le navigateur ; la liste est vide lorsqu'aucun client n'en a enregistré. Voir [Déconnexion front-channel](front-channel-logout).

### Mot de passe oublié {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Renvoie toujours `200` (protection contre l'énumération). Si l'utilisateur existe, un e-mail de réinitialisation lui est envoyé.

### Réinitialiser le mot de passe {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Description |
|---|---|
| `weak_password` | Ne satisfait pas les exigences de robustesse |
| `invalid_token` | Le jeton est mal formé |
| `token_expired` | Le jeton a expiré (validité de 60 minutes par défaut, configurable via `Auth:PasswordResetExpiryMinutes`) |

### Session {#session}

```
GET /api/auth/session
```

Renvoie les informations de la session en cours si l'utilisateur est authentifié :

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Renvoie `401` en l'absence d'authentification.

### Applications {#apps}

```
GET /api/auth/apps
```

Renvoie les liens vers les applications du locataire pour le lanceur « retour à l'application » de la page de compte : les clients activés qui ont une URI d'accueil (`initiateLoginUri` est préférée à `clientUri`). Chaque entrée a la forme `{ clientId, clientName, homeUri, logoUri, isDefault }` ; exactement une application est marquée par défaut (le client signalé comme tel, ou le seul client doté d'une URI d'accueil). Exige l'authentification par cookie.

### Profil (libre-service) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

L'utilisateur authentifié lit et met à jour ses propres champs de profil non sensibles : `firstName`, `lastName`, `companyName`, `phone`, `locale`. Les champs null restent inchangés ; l'adresse e-mail, le mot de passe, les rôles, l'état d'activation et l'organisation ne sont **pas** modifiables ici. Les deux renvoient le profil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Sessions (libre-service) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

Liste les sessions SSO de l'utilisateur authentifié et y met fin. Ces opérations nécessitent des sessions côté serveur, qui sont optionnelles : appelez `AddAuthagonalServerSideSessions(configuration)` après `AddAuthagonal` (Azure Table Storage, qui lit `Storage:ConnectionString` ou `Storage:TableServiceUri`), ou enregistrez vos propres `ITicketStore` et `IUserSessionRegistry`. Sans registre, `GET` renvoie une liste vide, `revoke-others` renvoie `{ "revoked": 0 }` et `DELETE` renvoie `404 not_supported`. Les routes `DELETE` et `POST` exigent une requête de même origine.

`GET` renvoie les sessions, de l'activité la plus récente à la plus ancienne :

```json
{
  "sessions": [
    {
      "sessionId": "...",
      "current": true,
      "createdAt": "2026-10-01T02:11:40+00:00",
      "lastSeenAt": "2026-10-04T05:30:12+00:00",
      "expiresAt": "2026-10-08T02:11:40+00:00",
      "ip": "203.0.113.7",
      "userAgent": "Mozilla/5.0 ..."
    }
  ]
}
```

`DELETE` met fin à une session et renvoie `{ "revoked": 1 }`, ou `404 session_not_found`. `POST /revoke-others` met fin à toutes les sessions sauf celle de l'appelant et renvoie `{ "revoked": <count> }`. Les deux notifient aussi les parties de confiance de chaque session terminée (déconnexion back-channel et front-channel) et révoquent les octrois qui y sont liés : les jetons de rafraîchissement détenus sur cet appareil cessent donc de fonctionner. La page de compte de l'interface de connexion affiche cette liste lorsqu'un registre est enregistré.

### Vérification SSO {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

Vérifie si le domaine de l'adresse e-mail exige le SSO :

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Si le SSO n'est pas exigé :

```json
{
  "ssoRequired": false
}
```

### Politique de mot de passe {#password-policy}

```
GET /api/auth/password-policy
```

Renvoie les exigences de mot de passe du serveur (configurées via `PasswordPolicy` dans les paramètres) :

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

L'interface de connexion par défaut interroge cet endpoint sur la page de réinitialisation du mot de passe pour afficher dynamiquement les exigences.

## Exigences de mot de passe par défaut {#default-password-requirements}

Avec la configuration par défaut, les mots de passe doivent satisfaire toutes ces conditions :

- Au moins 8 caractères
- Au moins une lettre majuscule
- Au moins une lettre minuscule
- Au moins un chiffre
- Au moins un caractère non alphanumérique
- Au moins 2 caractères distincts

Ces exigences peuvent être personnalisées via la section de configuration `PasswordPolicy` ; voir [Configuration](configuration).

## Endpoints MFA {#mfa-endpoints}

### Vérification MFA {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Vérifie un défi MFA. En cas de succès, définit le cookie d'authentification et renvoie les informations de l'utilisateur.

**Méthodes :**

| `method` | Champs requis | Description |
|---|---|---|
| `totp` | `code` (6 chiffres) | Mot de passe à usage unique basé sur le temps, issu d'une application d'authentification |
| `webauthn` | `assertion` (chaîne JSON) | Réponse d'assertion WebAuthn issue de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Code de récupération à usage unique (consommé à l'utilisation) |

**Sémantique des nouvelles tentatives :** un code erroné ne **consomme pas** le défi : le code est validé d'abord et le défi n'est consommé qu'en cas de succès, de sorte que l'utilisateur peut réessayer avec le même `challengeId` après une faute de frappe (`401 invalid_code` / `assertion_failed`). Chaque défi tolère **5 tentatives échouées** ; le 5e échec le consomme et renvoie `401 too_many_attempts`, ce qui impose une nouvelle connexion (cela limite une attaque par force brute sur le TOTP à 5 essais par défi). Les défis expirent aussi (5 minutes par défaut, `Auth:MfaChallengeExpiryMinutes`) ; un `challengeId` expiré, inconnu ou déjà consommé renvoie `invalid_challenge`. Les codes TOTP sont en outre protégés contre le rejeu : un code issu d'un intervalle de temps déjà utilisé est rejeté.

### État de la MFA {#mfa-status}

```
GET /api/auth/mfa/status
```

Renvoie les méthodes MFA auxquelles l'utilisateur est enrôlé. Exige l'authentification par cookie ou l'en-tête `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` vaut `false` lorsque la `MfaPolicy` de chaque client est `Disabled`, c'est-à-dire que la MFA est désactivée pour le locataire : l'interface de configuration peut alors se masquer. Les entrées de codes de récupération portent en plus `isConsumed`.

### Configuration du TOTP {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuration de WebAuthn / des passkeys {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

L'enrôlement d'une passkey exige d'abord un **identifiant TOTP confirmé** (`400 totp_required_first`) : les passkeys sont une commodité propre à un appareil, superposée à un facteur de base portable, de sorte qu'un compte ne peut jamais se retrouver uniquement protégé par passkey et lié à un appareil. Les utilisateurs dont le domaine de messagerie est routé vers le SSO ne peuvent pas enrôler de passkey locale (`400 sso_managed`), car elle contournerait l'IdP du locataire. Un identifiant de credential déjà enregistré pour **n'importe quel** compte, y compris celui de l'utilisateur qui s'enrôle, est rejeté avec `409 credential_already_registered`, car un doublon réinitialiserait le compteur de signatures de ce credential et ferait partager une même entrée de recherche à deux lignes.

### Codes de récupération {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Génère 10 codes de récupération à usage unique. Exige qu'au moins une méthode principale (TOTP ou WebAuthn) soit enrôlée. Une régénération remplace tous les codes de récupération existants.

### Supprimer un identifiant MFA {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Supprime un identifiant MFA précis. Si la dernière méthode principale est supprimée, la MFA est désactivée pour l'utilisateur. Exige une véritable session par cookie : un jeton de configuration est rejeté avec `403 session_required` (les jetons de configuration n'existent que pour ajouter un premier facteur, jamais pour affaiblir la MFA).

### Connexion par passkey sans mot de passe {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Connexion par credential découvrable (passkey résidente) sans contexte utilisateur préalable : `begin` émet un défi d'assertion avec une liste `allowCredentials` vide, et `complete` résout l'utilisateur **à partir de** la passkey choisie, vérifie l'assertion et le connecte (la session porte le marqueur MFA, une passkey étant une authentification forte résistante à l'hameçonnage). Comme aucun utilisateur n'a été identifié avant la cérémonie, l'étape 6 du §7.2 de WebAuthn rend ici obligatoire le user handle de l'authentificateur : une assertion qui n'en comporte pas est refusée avec `401 user_handle_required`, et une assertion qui désigne un autre compte que le propriétaire du credential l'est avec `401 credential_not_found`. Si le domaine de messagerie de l'utilisateur résolu est routé vers le SSO, la connexion est refusée avec `409 sso_required` + `redirectUrl`, afin qu'une passkey locale ne puisse pas contourner un IdP imposé.

## Autorisation d'appareil (RFC 8628) {#device-authorization-rfc-8628}

### Demander un code d'appareil {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Renvoie un code d'appareil, un code utilisateur et une URI de vérification :

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` provient de la `DeviceCodeLifetimeSeconds` du client (300 par défaut). L'appareil affiche `verification_uri` et `user_code` à l'utilisateur, puis interroge l'endpoint de jeton avec `device_code`, sans descendre sous `interval` secondes entre deux appels, faute de quoi l'endpoint de jeton répond `slow_down` (RFC 8628 §3.5). Tant que l'utilisateur n'a pas approuvé, l'endpoint de jeton renvoie `authorization_pending`. L'utilisateur se rend sur l'URI de vérification, se connecte et saisit le code utilisateur pour approuver.

### Afficher la requête avant l'approbation {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Exige l'authentification par cookie. Décrit ce que le code accorderait, afin que l'écran d'approbation puisse montrer à l'utilisateur quelle application le demande avant qu'il n'approuve (un flux d'appareil lancé par un attaquant et approuvé sur une invite opaque est le schéma de consentement illicite contre lequel met en garde le §5.4 de la RFC 8628) :

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` correspond à ce qui serait effectivement accordé, après le contrôle par rôle de l'utilisateur sur les scopes soumis à des rôles, et non à la requête brute. Erreurs : `401 not_authenticated`, `400 user_code_required`, `400 invalid_user_code` (inconnu, consommé ou expiré), `400 expired`. Il partage le compartiment de limitation de débit de l'approbation (ci-dessous).

### Approuver un appareil {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Exige l'authentification par cookie et une requête de même origine. `scopes` est facultatif (séparés par des espaces) : il ne peut que restreindre ce à quoi l'utilisateur a droit, jamais l'élargir, et l'omettre accorde tout ce à quoi il a droit. Approuve le code d'appareil pour l'utilisateur courant et renvoie `200 { "approved": true }`. L'appareil peut ensuite échanger le code d'appareil contre des jetons auprès de l'endpoint de jeton avec le type d'octroi `urn:ietf:params:oauth:grant-type:device_code`.

Le code soumis est normalisé selon le §6.1 de la RFC 8628 avant la recherche : il est mis en majuscules et tout caractère hors de l'alphabet de 31 caractères des codes est supprimé. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` et un copier-coller qui a transformé le tiret en tiret cadratin désignent tous le même code. Le tiret n'existe que pour faciliter la lecture à voix haute.

| Statut | `error` | Signification |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | Comme pour `info` |
| 400 | `invalid_scope` | `scopes` a été fourni, mais aucun de ses éléments n'est un scope auquel l'utilisateur a droit |
| 403 | `access_denied` | L'utilisateur n'a droit à aucun des scopes demandés (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | La politique MFA effective du client est `Required` et l'utilisateur n'a pas de second facteur ; enrôlez-vous, puis approuvez à nouveau |

La saisie est limitée à dix tentatives par minute et par sujet (RFC 8628 §5.1), partagées entre `info`, `approve` et `deny` ; la onzième renvoie `429`. Avec le limiteur de débit en processus par défaut, ce compteur est propre à chaque nœud : un déploiement à plusieurs réplicas devrait donc aussi appliquer la limite en périphérie.

### Refuser un appareil {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Exige l'authentification par cookie et une requête de même origine. Enregistre le refus de l'utilisateur et renvoie `200 { "success": true }`. La prochaine interrogation de l'endpoint de jeton par l'appareil obtient `access_denied` (RFC 8628 §3.5) au lieu de `authorization_pending`, jusqu'à l'expiration du code. Mêmes erreurs et même compartiment de limitation de débit que `info`.

## Introspection de jetons (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Ou avec des identifiants encodés dans le formulaire :

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Renvoie les métadonnées du jeton :

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Les jetons inactifs ou invalides renvoient `{ "active": false }`. Prend en charge à la fois les jetons d'accès JWT et les jetons de rafraîchissement opaques.

## Endpoints de consentement {#consent-endpoints}

### Informations de consentement {#consent-info}

```
GET /consent/info?client_id=my-app
```

Exige l'authentification par cookie. Renvoie les détails du client et les scopes demandés pour la page de consentement. Les scopes ne proviennent pas de la chaîne de requête : il s'agit de l'offre que l'endpoint d'autorisation a enregistrée pour cet utilisateur et ce client (après filtrage selon les droits liés aux rôles), de sorte qu'un lien forgé ne peut pas placer le nom d'un client de confiance au-dessus d'une liste de permissions choisie par l'appelant.

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"],
  "scopeDetails": [
    { "name": "openid", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "profile", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "email", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null }
  ]
}
```

`scopeDetails` est parallèle à `scopes` (même ordre, une entrée par scope) : une application de connexion qui ne lit que `scopes` continue donc de fonctionner. Chaque entrée porte la présentation enregistrée pour ce scope :

| Champ | Signification |
|---|---|
| `name` | Le nom du scope, comme dans `scopes`. |
| `displayName` | Le nom d'affichage enregistré, ou `null` lorsque le scope n'est pas enregistré. |
| `description` | La description enregistrée, ou `null`. |
| `emphasize` | `true` lorsque le scope est enregistré comme lourd de conséquences, de sorte que l'écran peut attirer l'attention sur lui. Vaut `false` par défaut. |
| `required` | `true` lorsque le scope est enregistré comme impossible à refuser : l'écran l'affiche coché et verrouillé. Vaut `false` par défaut. |
| `group` | L'intitulé sous lequel ranger le scope, ou `null` pour l'afficher seul. |

Un scope non enregistré donne `null` pour `displayName`, `description` et `group`, et `false` pour les deux indicateurs ; l'application de connexion se rabat alors sur sa propre formulation. Voir [Scopes](scopes) pour enregistrer la formulation.

Erreurs :

| Statut | Corps | Quand |
|---|---|---|
| `401` | aucun | Aucun utilisateur connecté. |
| `404` | `{ "error": "client_not_found" }` | `client_id` inconnu. |
| `400` | `{ "error": "no_pending_consent_request" }` | Il n'existe aucune offre de consentement en cours pour cet utilisateur et ce client (aucune n'a été enregistrée, ou elle a expiré). |

### Soumettre le consentement {#submit-consent}

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Enregistre la décision de consentement de l'utilisateur (exige l'authentification par cookie) et renvoie `{ "redirect": "..." }`, vers lequel la SPA doit naviguer. En cas d'acceptation, les scopes accordés sont enregistrés (filtrés selon les `AllowedScopes` du client : un corps falsifié ne peut pas enregistrer des scopes que le client n'aurait pas pu demander) et la redirection renvoie vers le flux d'autorisation. Avec `"decision": "deny"`, la redirection pointe vers la `redirect_uri` du client avec une erreur `access_denied`.

### Lister les octrois {#list-grants}

```
GET /consent/grants
```

Renvoie toutes les applications que l'utilisateur a autorisées :

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Révoquer un octroi {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

Révoque le consentement accordé à une application précise. L'utilisateur sera invité à consentir de nouveau lors de sa prochaine connexion.

## Découverte et clés de signature (JWKS) {#discovery-and-signing-keys-jwks}

Les deux sont publics et anonymes. C'est ce qu'utilise un serveur de ressources pour valider les jetons émis par ce serveur.

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- Les deux chemins de métadonnées renvoient le même document de découverte ; son `jwks_uri` est `{issuer}/.well-known/openid-configuration/jwks`.
- Le JWKS liste toutes les clés de signature non expirées (`kty`, `use`, `kid`, `alg`, ainsi que `crv`/`x`/`y` pour les clés EC). La rotation publie la clé suivante plusieurs jours à l'avance : une copie en cache ne manque donc jamais la clé avec laquelle un jeton a été signé.
- Les réponses portent `Cache-Control: public, max-age=3600`.
- La signature se fait uniquement en ES256 ; la découverte annonce `id_token_signing_alg_values_supported: ["ES256"]`.
- L'émetteur provient de `ITenantContext` et les clés de `IKeyManager` : un hôte multi-locataire doté d'un gestionnaire de clés par locataire sert donc des clés par locataire.

## Comportement de l'endpoint d'autorisation {#authorization-endpoint-behaviour}

`GET /connect/authorize` est le point d'entrée du flux par code d'autorisation. Deux comportements comptent pour quiconque construit un client ou une interface de connexion qui s'appuie dessus.

### Émetteur dans la réponse (RFC 9207) {#issuer-in-the-response-rfc-9207}

Chaque redirection vers la `redirect_uri` du client porte un paramètre de requête `iss` contenant l'émetteur, en cas de succès (à côté de `code` et `state`) comme en cas d'erreur (à côté de `error`, `error_description` et `state`). Il en va de même pour la redirection d'erreur lorsqu'un utilisateur refuse son consentement sur `/consent`. Le document de découverte l'annonce avec `authorization_response_iss_parameter_supported: true`. Un client qui dialogue avec plusieurs serveurs d'autorisation devrait comparer `iss` à l'émetteur auprès duquel il a lancé le flux, ce qui déjoue l'attaque par confusion (mix-up) ; les clients qui ignorent ce paramètre ne sont pas affectés. Les erreurs survenant avant qu'une `redirect_uri` de confiance soit connue (`client_id` inconnu, URI de redirection non enregistrée) sont renvoyées sous forme de corps d'erreur JSON, et non de redirection : elles ne portent donc pas de `iss`.

### `prompt` et `max_age` {#prompt-and-max_age}

| Requête | Comportement |
|---|---|
| `prompt=login` | Une session existante est fermée et l'utilisateur est envoyé vers `/login` pour s'authentifier de nouveau. Le `prompt` est retiré de `returnUrl`, afin que la nouvelle connexion ne soit pas contrainte de se réauthentifier en boucle. Pour une [requête poussée](par), le prompt est transporté par le contenu stocké, et la boucle est rompue en exigeant que l'`auth_time` de la session soit égal ou postérieur au moment où la requête a été poussée |
| `prompt=select_account` | Traité comme `prompt=login` : le serveur conserve une session par navigateur, le choix du compte se fait donc sur l'écran de connexion |
| `prompt=create` | Un utilisateur non authentifié est envoyé vers `/login/register` au lieu du formulaire de connexion. Une session existante poursuit simplement |
| `prompt=consent` | L'écran de consentement est affiché même si un octroi enregistré satisferait la requête, une fois par requête (le marqueur de satisfaction est à usage unique) |
| `prompt=none` | Aucune interface n'est jamais affichée. Le serveur répond par une redirection portant `login_required` (pas de session), `interaction_required` (renforcement ou enrôlement MFA nécessaire) ou `consent_required` (consentement nécessaire) |
| `max_age=N` | Si l'`auth_time` de la session remonte à plus de `N` secondes, ou est absent, l'utilisateur est réauthentifié exactement comme pour `prompt=login`. `max_age=0` réauthentifie toujours |

`prompt=none` combiné à toute autre valeur est rejeté avec `invalid_request`, de même que toute valeur autre que `none`, `login`, `consent`, `select_account` et `create`. L'hôte intégrable `Authagonal.Protocol` traite `prompt=login`, `select_account`, `none` et `max_age` de la même manière, mais n'a pas d'interface de consentement : il répond donc à `prompt=consent` par `consent_required`.

## Construire une interface de connexion personnalisée {#building-a-custom-login-ui}

La SPA par défaut (`login-app/`) est une implémentation de cette API. Pour construire la vôtre :

1. Servez votre interface aux chemins `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. L'endpoint d'autorisation redirige les utilisateurs non authentifiés vers `/login?returnUrl={encoded-authorize-url}`
3. Après une connexion réussie (cookie défini), redirigez l'utilisateur vers `returnUrl`
4. Les liens de réinitialisation du mot de passe utilisent `{Issuer}/login/reset-password?p={token}` (la SPA de connexion est montée sous `/login`)

Votre interface doit être servie depuis la **même origine** que l'API, car :
- l'authentification par cookie utilise `SameSite=Lax` + `HttpOnly`
- l'endpoint d'autorisation redirige vers `/login` (chemin relatif)
- les liens de réinitialisation utilisent `{Issuer}/login/reset-password`
