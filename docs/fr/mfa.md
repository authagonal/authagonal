---
layout: default
title: Authentification multifacteur
locale: fr
---

# Authentification multifacteur (MFA)

Authagonal prend en charge l'authentification multifacteur. Trois méthodes sont disponibles : TOTP (applications d'authentification), WebAuthn/passkeys (clés matérielles et biométrie) et codes de récupération à usage unique. Les passkeys peuvent aussi servir à la [connexion sans mot de passe](#passwordless-passkey-login).

Les connexions fédérées (SAML/OIDC) sont également couvertes : une assertion SAML ou OIDC prouve le premier facteur, pas le second. Un utilisateur fédéré qui a enrôlé une MFA passe par le même défi MFA local qu'une connexion par mot de passe, et une politique `Required` impose l'enrôlement avant l'émission de toute session. La fédération ne suffit à elle seule que lorsque la MFA n'est ni enrôlée ni exigée. Une connexion peut désactiver le défi local avec `ChallengeMfaAfterLogin: false` (voir ci-dessous).

## Méthodes prises en charge {#supported-methods}

| Méthode | Description |
|---|---|
| **TOTP** | Mots de passe à usage unique basés sur le temps (RFC 6238) : 6 chiffres, pas de 30 secondes, SHA-1, vérifiés avec une fenêtre de tolérance d'un pas pour le décalage d'horloge. Fonctionne avec n'importe quelle application d'authentification (Google Authenticator, Authy, 1Password, etc.). Un code déjà accepté ne peut pas être rejoué pendant sa fenêtre de validité. |
| **WebAuthn / Passkeys** | Clés de sécurité matérielles FIDO2, biométrie de la plateforme (Touch ID, Windows Hello) et passkeys synchronisées. Les utilisateurs peuvent enregistrer plusieurs passkeys, et les passkeys permettent de se connecter sans mot de passe. |
| **Codes de récupération** | 10 codes de secours à usage unique (10 caractères tirés d'un alphabet de 32 caractères, affichés sous la forme `XXXXX-XXXXX`) pour récupérer le compte lorsque les autres méthodes ne sont pas disponibles. Stockés hachés et chiffrés au repos. |

## Politique MFA {#mfa-policy}

L'application de la MFA se configure **par client** au moyen de la propriété `MfaPolicy` dans `appsettings.json` :

| Valeur | Comportement |
|---|---|
| `Disabled` (par défaut) | N'impose pas l'enrôlement ; l'interface de configuration en libre-service masque la MFA lorsque tous les clients sont en `Disabled` |
| `Enabled` | Propose l'enrôlement MFA sans l'imposer |
| `Required` | Impose l'enrôlement aux utilisateurs qui n'ont pas de MFA |

Un utilisateur qui a enrôlé une MFA est **toujours soumis au défi lors de la connexion, quelle que soit la politique du client**. La MFA est une propriété de l'utilisateur et de sa session, et non du client qui fait la requête ; une requête passant par un client `Disabled` ne peut donc pas servir à contourner le second facteur d'un utilisateur enrôlé.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

La valeur par défaut est `Disabled` : les clients existants ne sont donc pas affectés tant que vous ne l'activez pas.

### Redéfinition par utilisateur {#per-user-override}

Implémentez `IAuthHook.ResolveMfaPolicyAsync` pour redéfinir la politique du client pour certains utilisateurs :

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

La politique résolue régit l'enrôlement (s'il est proposé ou imposé). Elle n'exempte pas du défi un utilisateur déjà enrôlé ; les utilisateurs enrôlés sont toujours soumis au défi.

Voir [Extensibilité](extensibility) pour la documentation complète des hooks.

## Flux de connexion {#login-flow}

Le flux de connexion avec MFA se déroule ainsi :

1. L'utilisateur envoie son e-mail et son mot de passe à `POST /api/auth/login`
2. Le serveur vérifie le mot de passe, puis résout la politique MFA effective
3. Selon la politique et l'état d'enrôlement de l'utilisateur :

| Politique | L'utilisateur a-t-il une MFA ? | Résultat |
|---|---|---|
| Toute | Oui | Renvoie `mfaRequired` : l'utilisateur doit vérifier |
| `Disabled` / `Enabled` | Non | Cookie défini, connexion terminée |
| `Required` | Non | Renvoie `mfaSetupRequired` : l'utilisateur doit s'enrôler |

### Défi MFA {#mfa-challenge}

Lorsque `mfaRequired` est renvoyé, la réponse de connexion inclut un `challengeId`, les `methods` dont dispose l'utilisateur et (lorsque l'utilisateur a des passkeys) les options d'assertion `webAuthn`. Le client redirige vers une page de défi MFA où l'utilisateur effectue la vérification avec l'une de ses méthodes enrôlées via `POST /api/auth/mfa/verify` :

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` vaut `totp`, `recovery` ou `webauthn` (WebAuthn envoie une `assertion` au lieu d'un `code`).

Les défis expirent au bout de 5 minutes (configurable via `Auth:MfaChallengeExpiryMinutes`) et sont consommés lorsque la vérification réussit.

#### Nombre de tentatives {#retry-budget}

Un code erroné ne fait pas perdre le défi. Le point de terminaison de vérification valide d'abord le code et ne consomme le défi qu'en cas de réussite ; un chiffre TOTP mal saisi peut donc simplement être ressaisi avec le même `challengeId`. Les tentatives échouées renvoient `invalid_code` (ou `assertion_failed` pour WebAuthn) avec un 401 et incrémentent un compteur borné sur le défi ; la cinquième tentative erronée consomme le défi et renvoie `too_many_attempts`, ce qui oblige à se reconnecter. Cela vaut pour les trois méthodes.

Le budget par défi est un raccourci, pas la limite de sécurité ; deux barrières supplémentaires s'appliquent donc à `POST /api/auth/mfa/verify` :

- **Limitation de débit par utilisateur.** Au-delà de 10 tentatives de vérification par minute pour un même utilisateur, la réponse est `too_many_attempts` avec un 429, quel que soit le `challengeId` utilisé.
- **Verrouillage de compte partagé.** Chaque code erroné est aussi décompté du même compteur de tentatives échouées que l'étape du mot de passe (`Auth:MaxFailedAttempts`, `Auth:LockoutDurationMinutes`). Lorsque ce compteur se déclenche, le défi est consommé et la réponse est `locked_out` (423). Tant que le compte est verrouillé, la vérification est refusée avec `locked_out` avant même que le code soit contrôlé.

Seuls des identifiants confirmés peuvent satisfaire une vérification ; un enrôlement commencé mais jamais terminé ne compte pas comme facteur.

Un défi absent, expiré ou déjà consommé renvoie `invalid_challenge`.

### Connexions fédérées {#federated-logins}

Après une assertion SAML ou OIDC réussie, le serveur résout la même politique MFA effective. Un utilisateur enrôlé en MFA est redirigé vers la page de défi MFA hébergée (avec un `challengeId`) au lieu de recevoir une session ; un utilisateur sans MFA soumis à une politique `Required` est redirigé vers la page de configuration MFA (avec un `setupToken`). La session n'est marquée comme authentifiée par MFA qu'une fois la vérification terminée.

Ce défi est propre à chaque connexion : une connexion SAML ou OIDC dont `ChallengeMfaAfterLogin` vaut `false` ignore le défi local pour les utilisateurs qui arrivent par elle. La valeur par défaut est `true`.

### Enrôlement imposé {#forced-enrollment}

Lorsque `mfaSetupRequired` est renvoyé, la réponse inclut un `setupToken`. Ce jeton authentifie l'utilisateur auprès des points de terminaison de configuration MFA (via l'en-tête `X-MFA-Setup-Token`), afin qu'il puisse enrôler une méthode avant d'obtenir une session par cookie. Les jetons de configuration expirent au bout de 15 minutes (configurable via `Auth:MfaSetupTokenExpiryMinutes`).

## Enrôlement MFA {#enrolling-mfa}

Les utilisateurs enrôlent une MFA via les points de terminaison de configuration en libre-service. Ceux-ci exigent soit une session par cookie authentifiée, soit un jeton de configuration.

### Configuration TOTP {#totp-setup}

1. Appelez `POST /api/auth/mfa/totp/setup`, qui renvoie un code QR (`data:image/png;base64,...`), une `manualKey` (en Base32, pour la saisie manuelle) et un jeton de configuration
2. L'utilisateur scanne le code QR avec son application d'authentification
3. L'utilisateur saisit le code à 6 chiffres pour confirmer : `POST /api/auth/mfa/totp/confirm`

L'étape de confirmation est limitée comme la vérification : au-delà de 10 tentatives par minute pour un même utilisateur, la réponse est `too_many_attempts` (429), et avec un jeton de configuration, le cinquième code erroné consomme le défi de configuration. Un enrôlement non confirmé expire au bout de 30 minutes (`setup_expired`).

### Configuration WebAuthn / passkey {#webauthn--passkey-setup}

1. Appelez `POST /api/auth/mfa/webauthn/setup`, qui renvoie un `setupToken` et des `PublicKeyCredentialCreationOptions`
2. Le client appelle `navigator.credentials.create()` avec ces options
3. Envoyez la réponse d'attestation à `POST /api/auth/mfa/webauthn/confirm`

L'enrôlement d'une passkey exige au préalable un identifiant TOTP confirmé (`totp_required_first`). Les passkeys sont une commodité propre à chaque appareil, ajoutée par-dessus un facteur de base portable ; chaque compte conserve ainsi un facteur indépendant de l'appareil, et une politique `Required` ne peut pas être satisfaite par une passkey seule.

Les utilisateurs peuvent enregistrer plusieurs passkeys (une par appareil). Un identifiant d'authentificateur déjà enregistré (sur n'importe quel compte, y compris celui de l'utilisateur qui s'enrôle) est refusé avec `credential_already_registered` (409). Réenrôler un authentificateur déjà enrôlé créerait une seconde ligne d'identifiant partageant le même identifiant d'authentificateur : son compteur de signatures repartirait de zéro, ce qui affaiblirait la détection de clonage, et la suppression de l'une ou l'autre ligne retirerait l'entrée de recherche dont dépendent les deux. L'entrée de recherche est réservée par une écriture d'insertion conditionnée à l'absence, de sorte que deux enregistrements du même identifiant d'authentificateur ne peuvent pas réussir tous les deux. Les utilisateurs dont le domaine de messagerie est acheminé vers un IdP externe via le SSO imposé ne peuvent pas enrôler de passkey locale (`sso_managed`), car celle-ci contournerait l'IdP et son déprovisionnement.

### Hôte de la partie de confiance {#relying-party-host}

L'identifiant et l'origine de la partie de confiance FIDO2 sont résolus pour chaque requête à partir de l'hôte ; chaque nom d'hôte de locataire est donc sa propre partie de confiance. Définissez `Auth:WebAuthnAllowedHosts` avec les noms d'hôte que vous servez, afin qu'un hôte absent de cette liste ne puisse pas agir comme partie de confiance. Une liste vide (la valeur par défaut) conserve le comportement précédent plutôt que de bloquer les utilisateurs de passkeys existants lors d'une mise à niveau, et elle est journalisée comme une lacune à la première utilisation. Ce n'est pas un état sûr à long terme. Définir aussi `AllowedHosts` dans `appsettings.json`, afin que le filtrage d'hôtes d'ASP.NET Core rejette les en-têtes `Host` non reconnus avant l'exécution de tout gestionnaire, constitue la couche extérieure la moins coûteuse.

Indépendamment de cette liste, chaque identifiant enregistre la partie de confiance sous laquelle il a été enrôlé et est refusé partout ailleurs. C'est la partie sur laquelle la requête n'a aucune prise : sinon, les deux cérémonies construisent leurs attentes à partir du même en-tête `Host` que celui qu'elles vérifient ; l'origine et le `rpIdHash` seraient alors comparés à une valeur fournie par l'appelant, et un hôte intermédiaire qui transmet son propre `Host` verrait la liaison à l'origine, la propriété qui rend une passkey résistante à l'hameçonnage, le valider au lieu de l'en empêcher. Les identifiants enrôlés avant l'enregistrement du RP ID n'en portent aucun et continuent de fonctionner ; ils acquièrent la liaison lorsqu'ils sont réenrôlés.

### Codes de récupération {#recovery-codes}

Appelez `POST /api/auth/mfa/recovery/generate` pour générer 10 codes à usage unique. Au moins une méthode principale confirmée (TOTP ou WebAuthn) doit être enrôlée au préalable (`primary_method_required`), et l'appel exige une véritable session authentifiée : un jeton de configuration reçoit `session_required` (403).

Chaque code comporte 10 caractères tirés d'un alphabet de 32 caractères, affichés en deux groupes de cinq (`XXXXX-XXXXX`).

Régénérer les codes remplace tous les codes de récupération existants. Chaque code n'est utilisable qu'une seule fois ; un code utilisé est marqué comme consommé et n'est plus accepté.

Les codes ne sont jamais stockés en clair : chaque code est haché, et le hachage est en outre chiffré au repos avec le fournisseur de secrets du locataire ; un vidage du stockage ne livre donc que du texte chiffré, et non un hachage exposé à une attaque par force brute hors ligne.

## Connexion sans mot de passe par passkey {#passwordless-passkey-login}

Les passkeys ne sont pas qu'un second facteur : un utilisateur qui a enrôlé une passkey peut se connecter sans mot de passe.

1. `POST /api/auth/mfa/passwordless/begin` renvoie un `challengeId` et des `options` d'assertion pour les identifiants détectables, de sorte que l'authentificateur propose toute passkey résidente pour le site
2. Le client appelle `navigator.credentials.get()` avec ces options
3. `POST /api/auth/mfa/passwordless/complete` avec `{ challengeId, assertion }` : le serveur identifie l'utilisateur à partir de la passkey elle-même et le connecte

La page de connexion hébergée intègre ce mécanisme au champ e-mail via la médiation conditionnelle (saisie automatique des passkeys) : lorsque le navigateur la prend en charge, une passkey disponible est proposée comme suggestion de saisie automatique, sans interface supplémentaire.

Une passkey est une authentification forte résistante à l'hameçonnage ; la session obtenue porte donc le marqueur MFA et n'est pas soumise à un nouveau défi. Si le domaine de messagerie de l'utilisateur est acheminé vers un IdP externe via le SSO imposé, la connexion sans mot de passe est refusée par une réponse 409 `sso_required` qui inclut l'URL de redirection SSO, afin qu'une passkey locale ne puisse pas contourner l'IdP.

## Gestion de la MFA {#managing-mfa}

### Libre-service utilisateur {#user-self-service}

- `GET /api/auth/mfa/status` : affiche les méthodes enrôlées (indique aussi si la MFA est proposée par au moins un client)
- `DELETE /api/auth/mfa/credentials/{id}` : supprime un identifiant précis

La suppression d'un identifiant exige une véritable session authentifiée ; un jeton de configuration n'autorise que l'ajout d'un premier facteur et reçoit ici `session_required`, de sorte qu'un jeton de configuration divulgué ne peut pas affaiblir la MFA d'un utilisateur.

Si la dernière méthode principale est supprimée, la MFA est désactivée pour l'utilisateur.

### API d'administration {#admin-api}

Les administrateurs peuvent gérer la MFA de n'importe quel utilisateur via l'[API d'administration](admin-api) :

- `GET /api/v1/profile/{userId}/mfa` : affiche l'état MFA d'un utilisateur
- `DELETE /api/v1/profile/{userId}/mfa` : réinitialise toute la MFA (pour les utilisateurs bloqués)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` : supprime un identifiant précis

### Hooks d'audit {#audit-hooks}

Implémentez `IAuthHook.OnMfaVerifiedAsync` pour journaliser les événements MFA :

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Tout le cycle de vie de la MFA dispose de hooks : `OnMfaVerifyFailedAsync` (une tentative de vérification échouée), `OnMfaEnrolledAsync` (une méthode confirmée), `OnMfaCredentialRemovedAsync` (un identifiant supprimé, avec un indicateur précisant si cela a désactivé la MFA) et `OnRecoveryCodesRegeneratedAsync`.

## Interface de connexion personnalisée {#custom-login-ui}

Si vous créez une interface de connexion personnalisée, gérez ces réponses de `POST /api/auth/login` :

1. **Connexion normale** : `{ userId, email, name }` avec cookie défini. Redirigez vers `returnUrl`.
2. **MFA exigée** : `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Affichez le formulaire de défi MFA.
3. **Configuration MFA exigée** : `{ mfaSetupRequired: true, setupToken }`. Affichez le parcours d'enrôlement MFA.

Pour les erreurs de `POST /api/auth/mfa/verify` : `invalid_code` et `assertion_failed` permettent de réessayer avec le même `challengeId` (dans la limite du nombre de tentatives) ; `too_many_attempts` et `invalid_challenge` sont définitives : renvoyez alors l'utilisateur vers le formulaire de connexion.

Voir [API d'authentification](auth-api) pour la référence complète des points de terminaison.
