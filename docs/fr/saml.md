---
layout: default
title: SAML
locale: fr
---

# Fournisseur de services SAML 2.0

Authagonal inclut une implémentation maison d'un fournisseur de services (SP) SAML 2.0. Aucune bibliothèque SAML tierce : elle repose sur `System.Security.Cryptography.Xml.SignedXml` (inclus dans .NET).

## Périmètre {#scope}

- **SSO initié par le SP** (l'utilisateur commence sur Authagonal et est redirigé vers l'IdP)
- **Liaison HTTP-Redirect** pour l'AuthnRequest (signée en option, voir ci-dessous)
- **Liaison HTTP-POST** pour la Response (ACS)
- **Assertions chiffrées** (`EncryptedAssertion`) déchiffrées avec une paire de clés SP propre à chaque connexion
- **Déconnexion unique (Single Logout)** (initiée par le SP et par l'IdP, liaisons Redirect et POST)
- Azure AD / Entra ID est la cible principale, mais tout IdP conforme fonctionne (les noms d'attributs d'Okta, OneLogin, Ping, Google Workspace, ADFS et Shibboleth sont pris en charge)

### Non pris en charge {#not-supported}

- Liaison Artifact
- Chiffrement des assertions en AES-GCM (limitation de `EncryptedXml` dans .NET ; configurez AES-CBC côté IdP, voir ci-dessous)

**La connexion initiée par l'IdP fonctionne, et la vignette n'a pas besoin d'être reconfigurée**, mais ce n'est pas l'assertion non sollicitée qui connecte l'utilisateur. Une Response sans `InResponseTo` est écartée, et l'ACS redirige le navigateur vers `/saml/{connectionId}/login`, qui émet une nouvelle AuthnRequest liée à ce navigateur. L'utilisateur étant déjà authentifié auprès de l'IdP, celui-ci répond immédiatement et l'aller-retour est invisible ; le `RelayState` de l'IdP est transmis comme URL de retour, de sorte que l'utilisateur arrive tout de même sur le lien profond configuré pour la vignette.

L'assertion doit être écartée pour deux raisons : accepter une assertion non sollicitée permet à quiconque possède un compte auprès de cet IdP d'ouvrir une session dans n'importe quel agent utilisateur (chaque règle du §4.1.4.3 est satisfaite par une assertion que l'attaquant a obtenue légitimement pour son propre compte), et exiger le cookie de requête sur le parcours initié par le SP ne sert à rien tant que la même assertion peut être rejouée après suppression de `InResponseTo`. Relancer le flux garde la vignette fonctionnelle sans rien accepter de tout cela : l'utilisateur finalement connecté est celui que l'IdP désigne dans le *nouvel* échange.

La relance n'a lieu qu'une fois par navigateur. Un IdP qui répond à l'AuthnRequest par une autre Response non sollicitée est refusé avec `error=saml_unsolicited` au lieu d'être renvoyé une nouvelle fois, de sorte qu'un IdP mal configuré ne peut pas provoquer de boucle de redirection.

Pour accepter plutôt l'assertion non sollicitée telle quelle, définissez `allowUnsolicitedResponses: true` sur la connexion (**désactivé par défaut**). Dans ce cas, la vérification de l'identifiant de requête est ignorée pour les réponses non sollicitées, mais l'usage unique de l'identifiant d'assertion reste appliqué (voir Sécurité).

## Configuration d'Azure AD {#azure-ad-setup}

### 1. Créer un fournisseur SAML {#1-create-a-saml-provider}

**Option A : configuration (recommandée pour les installations statiques)**

Ajoutez à `appsettings.json` :

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Les fournisseurs sont initialisés au démarrage. `ConnectionId`, `EntityId` et `MetadataLocation` sont obligatoires pour une nouvelle connexion (le démarrage échoue sans eux). Les correspondances de domaines SSO sont enregistrées automatiquement à partir de `AllowedDomains`, sauf pour une connexion propre à une organisation, dont les domaines ne sont mis en correspondance qu'au sein de son organisation. Un fournisseur nouvellement initialisé ne reçoit pas de paire de clés SP (donc ni AuthnRequests signées, ni assertions chiffrées, ni messages de déconnexion signés) ; utilisez l'API d'administration pour ces fonctionnalités.

L'initialisation peut aussi définir `OrganizationId`, `JitProvisioningEnabled` (`false` par défaut), `ChallengeMfaAfterLogin` (`true` par défaut), `ProvisioningAttributeParams`, `AllowUninvitedJit` et `AllowUnsolicitedResponses`. L'initialisation lit la connexion stockée et fusionne ; une connexion existante conserve donc sa paire de clés SP, ses métadonnées collées, son format de NameID, `signAuthnRequests` et son icône, pour lesquels l'initialisation n'a pas de champ. Les indicateurs de comportement ci-dessus sont écrits depuis l'initialisation à chaque démarrage ; un indicateur que vous omettez revient donc à sa valeur par défaut.

`EntityId` est **l'identifiant d'entité de votre SP** (l'identifiant que vous enregistrez auprès de l'IdP), et non l'identifiant d'entité de l'IdP.

> **Un IdP sur votre propre réseau privé.** `MetadataLocation` doit être en https et, par défaut, doit pointer vers une adresse routable publiquement : le document de métadonnées contient les certificats au regard desquels chaque assertion est validée, et Authagonal refuse les cibles internes pour chaque URL qu'il récupère. Pour fédérer avec un IdP sur site, déclarez-le dans [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Si l'IdP ne publie aucun point de terminaison de métadonnées en https, collez plutôt le document dans `MetadataXml` via l'API d'administration.

**Option B : API d'administration (pour la gestion à l'exécution)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

L'API génère le `connectionId` (un GUID) et le renvoie dans l'en-tête `Location` et dans le corps de la réponse. Champs facultatifs supplémentaires : `metadataXml` (métadonnées collées, voir ci-dessous), `nameIdFormat` (voir ci-dessous), `signAuthnRequests` (impose la signature des AuthnRequests), `iconUrl` (icône du bouton de connexion), `jitProvisioningEnabled` (crée automatiquement les utilisateurs inconnus à leur première connexion ; **désactivé par défaut**, de sorte qu'un utilisateur inconnu est refusé tant que vous ne l'activez pas), `challengeMfaAfterLogin` (`true` par défaut ; `false` fait confiance à la MFA propre à l'IdP), `provisioningAttributeParams` et `allowUninvitedJit` (voir [SSO en libre-service](self-service-sso)), `organizationId` (limite la connexion à une organisation, voir [SSO en libre-service](self-service-sso#organisation-scoped-connections)), `allowUnsolicitedResponses` (accepte une assertion initiée par l'IdP telle quelle au lieu de relancer le flux ; désactivé par défaut, voir ci-dessus). Les connexions créées par l'API reçoivent aussi une paire de clés SP générée automatiquement (voir Paire de clés SP ci-dessous).

Les connexions se gèrent via `POST` / `GET` / `PUT` / `DELETE` sur `/api/v1/saml/connections[/{connectionId}]`. `PUT` est une mise à jour partielle : seuls les champs transmis sont modifiés.

### 2. Configurer Azure AD {#2-configure-azure-ad}

1. Dans Azure AD → Applications d'entreprise → Nouvelle application → Créer votre propre application
2. Configurez l'authentification unique → SAML
3. **Identificateur (ID d'entité) :** `https://auth.example.com/saml/acme-azure`
4. **URL de réponse (ACS) :** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de connexion :** `https://auth.example.com/saml/acme-azure/login`

### 3. Acheminement par domaine SSO {#3-sso-domain-routing}

Lorsque `AllowedDomains` est indiqué (dans la configuration ou via l'API de création), les correspondances de domaines SSO sont enregistrées automatiquement. Lorsqu'un utilisateur saisit `user@acme.com` sur la page de connexion, la SPA détecte que le SSO est obligatoire et affiche « Continuer avec SSO ». Un domaine ne peut être associé qu'à une seule connexion ; l'API refuse un domaine déjà revendiqué par une autre connexion.

Vous pouvez aussi gérer les domaines à l'exécution via l'API d'administration ; voir [API d'administration](admin-api).

## XML de métadonnées collé {#pasted-metadata-xml}

Certains IdP ne publient pas d'URL de métadonnées (Google Workspace), ou leur point de terminaison de métadonnées est inaccessible depuis le SP (ADFS sur un réseau privé). Dans ces cas, collez plutôt le document de métadonnées : fournissez `metadataXml` à la création ou à la mise à jour. Il faut fournir exactement l'un des deux, `metadataLocation` ou `metadataXml` ; en fournir un lors d'une mise à jour efface l'autre.

Les métadonnées collées sont validées à l'enregistrement et **condensées** (`SamlMetadataParser.Condense`) en un `EntityDescriptor` minimal et canonique ne contenant que ce que le SP utilise : entityID, certificats de signature, point de terminaison SSO, point de terminaison SLO s'il existe et indicateur `WantAuthnRequestsSigned`. Les documents des éditeurs peuvent dépasser 100 Ko (`FederationMetadata.xml` d'ADFS), au-delà de la limite de 64 Ko par propriété d'Azure Table, alors que les parties utilisées par le SP ne pèsent que quelques Ko. Un contenu collé impossible à analyser est refusé avec un 400 ; le document doit contenir un `IDPSSODescriptor` comportant un certificat de signature et un `SingleSignOnService`.

## Format du NameID {#nameid-format}

Le champ `nameIdFormat` détermine le Format de `NameIDPolicy` demandé dans l'AuthnRequest :

| Valeur | Comportement |
|---|---|
| omis / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (la valeur par défaut historique) |
| `"none"` | Omet entièrement l'élément `NameIDPolicy`. Le réglage sûr pour ADFS : ADFS fait échouer toute la connexion (MSIS7070) lorsque ses règles de revendication n'émettent pas le format demandé. |
| toute autre valeur | Envoyée telle quelle comme URN de Format (doit commencer par `urn:`) |

Lors d'une mise à jour, `""` rétablit la valeur par défaut emailAddress. Les métadonnées du SP annoncent le format demandé par la connexion (et omettent `NameIDFormat` lorsqu'il vaut `"none"`).

## Points de terminaison {#endpoints}

| Point de terminaison | Description |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Lance le SSO initié par le SP. Construit une AuthnRequest (signée le cas échéant) et redirige vers l'IdP. `loginHint` est transmis sous la forme `login_hint` aux IdP qui le prennent en compte (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Reçoit la Response SAML, la valide, puis crée ou connecte l'utilisateur. |
| `GET /saml/{connectionId}/metadata` | XML de métadonnées du SP pour configurer l'IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Déconnexion unique initiée par le SP. Met fin à la session locale, puis envoie une LogoutRequest à l'IdP lorsqu'il prend en charge le SLO. |
| `GET/POST /saml/{connectionId}/slo` | Point de terminaison de déconnexion unique. Reçoit les LogoutRequests initiées par l'IdP (liaison Redirect ou POST) ainsi que la LogoutResponse du SLO initié par le SP. |

L'URL de retour après connexion est conservée côté serveur, sur l'AuthnRequest stockée (indexée par identifiant de requête), et non dans le RelayState : la spécification SAML limite le RelayState à 80 octets et certains IdP le tronquent. Le RelayState n'est consulté que pour les flux initiés par l'IdP.

## Paire de clés SP et assertions chiffrées {#sp-keypair--encrypted-assertions}

Chaque connexion créée par l'API reçoit une paire de clés SP générée automatiquement : un certificat RSA 2048 bits auto-signé (valable 10 ans), stocké au format PKCS#12 et protégé au repos par le fournisseur de secrets de l'hôte. Il reste côté serveur et n'est jamais renvoyé par l'API. La paire de clés permet :

- **Les AuthnRequests signées** (signature des paramètres de requête `SigAlg`/`Signature` de la liaison Redirect). La signature s'active automatiquement lorsque les métadonnées de l'IdP déclarent `WantAuthnRequestsSigned`, ou systématiquement lorsque la connexion définit `signAuthnRequests: true`.
- **Le déchiffrement des assertions chiffrées.** Lorsque les métadonnées du SP annoncent un certificat de chiffrement, ADFS se met par défaut à chiffrer les assertions ; l'ACS les déchiffre avec la clé privée du SP et fait passer l'assertion déchiffrée par la même chaîne de vérification de signature et de conditions qu'une assertion en clair. Pris en charge : transport de clé RSA-OAEP (SHA-1/SHA-256) ; chiffrement des données AES-128/192/256-CBC et 3DES. **Le transport de clé RSA-1.5 est refusé** (le déballage PKCS#1 v1.5 constitue un oracle Bleichenbacher/ROBOT) et **AES-GCM n'est pas pris en charge** (limitation de `EncryptedXml` dans .NET). Configurez l'IdP en RSA-OAEP et AES-CBC. Les deux échecs renvoient délibérément le même message constant (« Could not decrypt the assertion. ») : nommer l'algorithme ou l'étape en échec est précisément ce qui crée l'oracle ; diagnostiquez donc à partir de la configuration de l'IdP plutôt que de l'erreur.
- **Les messages de déconnexion signés** (LogoutRequest/LogoutResponse sur la liaison Redirect).

Les métadonnées du SP publient le certificat à la fois comme `KeyDescriptor` `signing` et `encryption`, et définissent `AuthnRequestsSigned="true"` lorsque la connexion impose la signature.

## Déconnexion unique {#single-logout}

L'ACS enregistre la session SAML sur le cookie d'authentification (revendications `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) afin que la déconnexion puisse être rattachée à la session de l'IdP.

- **Initiée par le SP :** `GET /saml/{connectionId}/logout` met toujours fin d'abord à la session locale par cookie (l'utilisateur a demandé à se déconnecter ; le SLO auprès de l'IdP se fait au mieux). Si la session du navigateur provient de cette connexion et que les métadonnées de l'IdP annoncent un `SingleLogoutService`, une LogoutRequest (NameID + SessionIndex, signée lorsque le SP dispose d'une clé) est envoyée via la liaison Redirect ; la LogoutResponse de l'IdP revient sur `/slo`, qui amène l'utilisateur sur le `returnUrl` stocké. Les IdP sans point de terminaison SLO (Google) ne bénéficient que de la déconnexion locale.
- **Initiée par l'IdP :** l'IdP envoie une LogoutRequest à `/saml/{connectionId}/slo` (liaison Redirect en GET ou POST). Les requêtes signées sont validées au regard des certificats des métadonnées de l'IdP. **Une LogoutRequest non signée ou invérifiable est refusée avec un 400** avant toute consultation de session. Il n'existe pas de solution de repli limitée à la session : une page tierce qui fait naviguer le navigateur de la *victime* jusqu'ici fournit la session de la victime, et non celle de l'attaquant ; limiter le repli à la session courante n'aurait donc pas restreint qui pouvait être déconnecté. Le §4.4.3.1 des Profiles exige de toute façon que l'IdP signe une LogoutRequest sur la liaison Redirect ou POST, et les métadonnées de la connexion fournissent déjà les certificats ; refuser une requête non signée ne coûte donc rien à un IdP conforme. Une LogoutResponse signée est renvoyée lorsque l'IdP dispose d'un point de terminaison SLO. Front-channel uniquement : le message arrive dans le navigateur de l'utilisateur ; mettre fin à la session par cookie déconnecte donc exactement ce navigateur.

## Mise en cache des métadonnées et renouvellement des certificats {#metadata-caching--cert-rollover}

- Les métadonnées de l'IdP récupérées depuis `MetadataLocation` sont mises en cache en mémoire pendant 60 minutes (configurable via `Cache:SamlMetadataCacheMinutes`), indexées par l'URL des métadonnées (et non par l'identifiant de connexion ; aucune confusion de cache entre locataires n'est donc possible).
- Les métadonnées collées sont mises en cache par contenu (hachage du XML) et ne sont jamais récupérées de nouveau.
- **Nouvelle récupération après un échec de signature :** un échec de validation de signature juste après un renouvellement du certificat de l'IdP signifie que les métadonnées en cache sont périmées. Sur cet échec précis, l'entrée de cache est évincée et les métadonnées sont récupérées une nouvelle fois, puis la validation est retentée, avec un délai de 5 minutes par emplacement de métadonnées afin qu'une assertion fantaisiste ne puisse pas servir à bombarder le point de terminaison de métadonnées de l'IdP. Sans cela, un renouvellement de certificat ferait échouer les connexions jusqu'à l'expiration de la durée de vie du cache. (Uniquement pour les métadonnées récupérées par URL ; les métadonnées collées n'ont rien à récupérer.)

## Compatibilité avec Azure AD {#azure-ad-compatibility}

| Comportement d'Azure AD | Traitement |
|---|---|
| Signe uniquement l'assertion (par défaut) | Valide la signature sur l'élément Assertion |
| Signe uniquement la réponse | Valide la signature sur l'élément Response |
| Signe les deux | Valide les deux signatures |
| SHA-256 (par défaut) | Prend en charge SHA-256 et SHA-1 |
| NameID : emailAddress | Extraction directe de l'e-mail |
| NameID : persistent (opaque) | Se rabat sur la revendication d'e-mail des attributs |
| NameID : unspecified | Se rabat sur la revendication d'e-mail des attributs |
| NameID : transient | Change à chaque connexion ; il n'est donc jamais utilisé comme clé fédérée. L'attribut object-id stable de l'IdP est utilisé à la place ; si aucun n'est affirmé, la connexion est refusée avec une erreur indiquant comment y remédier (configurer un NameID persistent ou emailAddress, ou affirmer un attribut object-id). |

## Correspondance des attributs {#attribute-mapping}

Les attributs sont indexés sans tenir compte de la casse, à la fois sous leur `Name` et leur `FriendlyName` (Okta et Shibboleth émettent des Names sous forme d'OID accompagnés de FriendlyNames lisibles ; accepter l'un ou l'autre est ce qui fait fonctionner la correspondance avec chaque éditeur). Chaque champ essaie une liste d'alias dans l'ordre ; le premier alias est l'URI de revendication de Microsoft, de sorte que le comportement avec Entra/ADFS est inchangé, et les autres couvrent les noms usuels et les OID qu'Okta, OneLogin, Ping, Google et Shibboleth émettent par défaut :

| Champ | Noms d'attributs acceptés |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrège l'URI complète `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` ou `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Ordre de priorité pour déterminer l'e-mail : attribut d'e-mail explicite (n'importe quel alias) → NameID lorsque son format est emailAddress → revendication `name` si elle contient `@` → refus (un e-mail est obligatoire).

**Les groupes sont multivalués :** chaque élément `AttributeValue` est enregistré (un par appartenance à un groupe), et pas seulement le premier.

## Provisionnement JIT {#jit-provisioning}

Le provisionnement JIT est **désactivé par défaut**. Une connexion avec `jitProvisioningEnabled: true` crée automatiquement les utilisateurs inconnus à leur première connexion (e-mail, prénom et nom issus de l'assertion, e-mail marqué comme confirmé) et les lie à la connexion par leur identité fédérée stable (`saml:{connectionId}` + NameID, ou l'object-id pour les NameID transient). Sans cela, un utilisateur inconnu est refusé. Une connexion qui déclare `provisioningAttributeParams` exige en outre ce contexte d'invitation lors de la connexion, sauf si `allowUninvitedJit` est défini ; voir [SSO en libre-service](self-service-sso). Les utilisateurs qui reviennent sont d'abord identifiés par le lien fédéré, jamais par l'e-mail seul ; un compte local existant n'est rattaché par e-mail que si les `AllowedDomains` de la connexion couvrent le domaine de cet e-mail (l'administrateur affirme ainsi explicitement que cet IdP est propriétaire du domaine), ce qui empêche la prise de contrôle de comptes par un IdP malveillant.

## Durée de la session {#session-lifetime}

Si l'`AuthnStatement` de l'assertion porte un `SessionNotOnOrAfter`, il s'agit de la limite supérieure que l'IdP fixe lui-même à la session qu'il vient d'établir, et Authagonal la respecte. Le cookie de connexion expire au plus tard à cet instant (lorsqu'il se situe dans les 30 jours), et la même limite est portée par la session sous la forme `session_max_exp`, qui borne chaque jeton d'accès, d'identité et d'actualisation émis à partir d'elle. Une assertion sans `SessionNotOnOrAfter` n'impose aucune limite supplémentaire. SAML n'a pas de jeton d'actualisation amont ; c'est donc le seul moyen pour un IdP de limiter une session après la connexion. Pour les connexions OIDC, voir [Sessions fédérées](federated-sessions).

## Sécurité {#security}

- **Prévention du rejeu :** pour les flux initiés par le SP, `InResponseTo` est validé au regard d'un identifiant de requête stocké (à usage unique). Indépendamment, l'identifiant de chaque assertion acceptée est stocké et soumis à un usage unique, ce qui couvre aussi les réponses initiées par l'IdP et celles dont l'`InResponseTo` a été supprimé (l'identifiant d'assertion se trouve à l'intérieur de l'assertion signée ; il ne peut donc pas être modifié sans invalider la signature).
- **Décalage d'horloge :** tolérance de 5 minutes sur NotBefore/NotOnOrAfter
- **Âge maximal des assertions :** une assertion présentée plus d'une heure (plus la tolérance de décalage) après son propre `IssueInstant` est refusée, quoi qu'indique son `NotOnOrAfter`, et un `IssueInstant` situé dans le futur est refusé
- **Émetteur, destination et audience :** l'`Issuer` de la Response et de l'Assertion doit être égal à l'identifiant d'entité de l'IdP de la connexion, une Response signée doit porter une `Destination` correspondant à l'URL de cet ACS, et l'audience doit être l'identifiant d'entité SP de cette connexion
- **Validité du certificat de l'IdP :** un certificat de signature de l'IdP épinglé dont la période `NotBefore`/`NotAfter` est dépassée (tolérance de 5 minutes) est ignoré, aussi bien pour les assertions que pour les signatures de déconnexion de la liaison Redirect ; actualisez donc les métadonnées après un renouvellement
- **Prévention des attaques par enveloppement (wrapping) :** l'URI de Reference de la signature doit correspondre à l'ID de l'élément signé
- **Prévention des redirections ouvertes :** l'URL de retour après connexion doit être un chemin relatif à la racine (commençant par `/`, sans `//` ni barre oblique inverse, puisque les navigateurs traitent `\` comme `/`)
- **Garantie des domaines :** lorsque `AllowedDomains` est configuré, les assertions portant des e-mails hors de ces domaines sont refusées ; une connexion ne peut donc pas affirmer le domaine d'une autre ni l'e-mail d'un utilisateur local
- **MFA :** la fédération ne prouve que le premier facteur. Si la politique effective de l'utilisateur exige la MFA, la connexion passe par le défi ou la configuration MFA locale au lieu d'émettre une session entièrement authentifiée, sauf si la connexion définit `challengeMfaAfterLogin: false`.
