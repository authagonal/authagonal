---
layout: default
title: API de autenticación
locale: es
---

# API de autenticación

Estos endpoints dan servicio a la SPA de inicio de sesión. Usan autenticación por cookies (`SameSite=Lax`, `HttpOnly`).

Si está creando una interfaz de inicio de sesión personalizada, estos son los endpoints contra los que debe implementarla.

## Endpoints {#endpoints}

### Inicio de sesión {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Éxito (200):** establece una cookie de autenticación y devuelve:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` es `true` cuando el `MfaPolicy` del cliente es `Enabled` pero el usuario todavía no se ha inscrito (la interfaz puede ofrecer la configuración); en ese caso también se incluye un campo `clientId`.

**MFA obligatoria (200):** si el usuario tiene MFA inscrita, **siempre** se le exige, independientemente del `MfaPolicy` del cliente solicitante (la MFA es una propiedad del usuario y de la sesión, no del cliente):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

El cliente debe redirigir a una página de comprobación de MFA y llamar a `POST /api/auth/mfa/verify`.

**Configuración inicial de MFA obligatoria (200):** si `MfaPolicy` es `Required` y el usuario no tiene MFA inscrita:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

El cliente debe redirigir a una página de configuración inicial de MFA. El token de configuración inicial autentica al usuario ante los endpoints de configuración inicial de MFA mediante la cabecera `X-MFA-Setup-Token`.

**Respuestas de error:**

| `error` | Estado | Descripción |
|---|---|---|
| `invalid_credentials` | 401 | Correo electrónico o contraseña incorrectos. Deliberadamente idéntica para correos electrónicos desconocidos (contra la enumeración). |
| `locked_out` | 423 | Demasiados intentos fallidos. Se incluye `retryAfter` (en segundos). |
| `account_disabled` | 403 | La cuenta está desactivada (solo se indica tras una contraseña correcta) |
| `email_not_confirmed` | 403 | El correo electrónico aún no se ha verificado (solo se indica tras una contraseña correcta) |
| `sso_required` | 409 | El dominio exige SSO. `redirectUrl` apunta al inicio de sesión de SSO. |
| `captcha_failed` | 400 | La verificación de Turnstile falló (solo cuando Turnstile está configurado; las solicitudes necesitan entonces un campo `turnstileToken`) |
| `email_required` | 400 | El campo de correo electrónico está vacío |
| `password_required` | 400 | El campo de contraseña está vacío |

### Registro {#register}

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

Crea una nueva cuenta de usuario y envía un correo de verificación. Devuelve `201 { "success": true, "userId": "..." }`. Campos opcionales: `locale` (etiqueta BCP-47 que se persiste en el usuario) y `customAttributes` (un mapa de cadenas).

El registro es deliberadamente **neutral frente a la enumeración**: si el correo electrónico ya está registrado, la respuesta es el mismo `201` neutral (con un `userId` desechable) y, en su lugar, se envía a la persona titular real un aviso de inicio de sesión o restablecimiento. El registro también tiene un límite de frecuencia por IP: `429 rate_limited` cuando se supera (la ventana y el límite se configuran mediante `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmar correo electrónico {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirma la dirección de correo electrónico del usuario con el token del correo de verificación. `GET` es el enlace en el que se hace clic en el correo; redirige a `/login?email_confirmed=1` (más un parámetro `continue_client` cuando el registro se originó en un flujo OAuth). `POST` es la vía programática y devuelve JSON (el token también puede enviarse en un cuerpo JSON como `{ "token": "..." }`); la respuesta incluye un `appLink` opcional (destino de "continuar a la aplicación").

### Proveedores {#providers}

```
GET /api/auth/providers
```

Devuelve la lista de proveedores de identidad externos configurados (para mostrar los botones de SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Las conexiones con `AllowedDomains` configurado se **excluyen**: a esas se llega primero por el correo electrónico mediante `/api/auth/sso-check`, en lugar de mediante un botón. `turnstileSiteKey` se establece cuando Cloudflare Turnstile está configurado (la interfaz de inicio de sesión debe entonces enviar un `turnstileToken` con las solicitudes de inicio de sesión, registro y contraseña).

### Cierre de sesión {#logout}

```
POST /api/auth/logout
```

Termina la sesión de quien llama de la misma forma que `/connect/endsession`: se envían tokens de cierre de sesión por back-channel (canal de retorno) a las partes de confianza que tienen un URI registrado, se revocan las concesiones emitidas para esa sesión y se borra la cookie de autenticación. Requiere autenticación por cookie y una solicitud del mismo origen. Devuelve `200`:

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` enumera las URL de cierre de sesión por front-channel que quien llama debe cargar (iframes ocultos) para terminar el cierre de sesión en el navegador; está vacío cuando ningún cliente ha registrado una. Consulte [Cierre de sesión por front-channel](front-channel-logout).

### Contraseña olvidada {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Siempre devuelve `200` (contra la enumeración). Si el usuario existe, envía un correo de restablecimiento.

### Restablecer contraseña {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Descripción |
|---|---|
| `weak_password` | No cumple los requisitos de seguridad |
| `invalid_token` | El token está mal formado |
| `token_expired` | El token ha caducado (validez predeterminada de 60 minutos, configurable mediante `Auth:PasswordResetExpiryMinutes`) |

### Sesión {#session}

```
GET /api/auth/session
```

Devuelve la información de la sesión actual si hay autenticación:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Devuelve `401` si no hay autenticación.

### Aplicaciones {#apps}

```
GET /api/auth/apps
```

Devuelve los enlaces a las aplicaciones del inquilino para el lanzador "volver a la aplicación" de la página de cuenta: los clientes habilitados que tienen un URI de inicio (se prefiere `initiateLoginUri` a `clientUri`). Cada entrada es `{ clientId, clientName, homeUri, logoUri, isDefault }`; exactamente una aplicación se marca como predeterminada (el cliente marcado, o el único cliente con un URI de inicio). Requiere autenticación por cookie.

### Perfil (autoservicio) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

El usuario autenticado lee y actualiza sus propios campos de perfil no sensibles: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Los campos null no se modifican; el correo electrónico, la contraseña, los roles, el estado de activación y la organización **no** se pueden editar aquí. Ambos devuelven el perfil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Sesiones (autoservicio) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

Enumera y termina las propias sesiones de SSO del usuario autenticado. Necesitan sesiones del lado del servidor, que son opcionales: llame a `AddAuthagonalServerSideSessions(configuration)` después de `AddAuthagonal` (Azure Table Storage, leyendo `Storage:ConnectionString` o `Storage:TableServiceUri`), o registre sus propios `ITicketStore` e `IUserSessionRegistry`. Sin un registro de sesiones, `GET` devuelve una lista vacía, `revoke-others` devuelve `{ "revoked": 0 }` y `DELETE` devuelve `404 not_supported`. Las rutas `DELETE` y `POST` requieren una solicitud del mismo origen.

`GET` devuelve las sesiones, con la actividad más reciente primero:

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

`DELETE` termina una sesión y devuelve `{ "revoked": 1 }`, o `404 session_not_found`. `POST /revoke-others` termina todas las sesiones excepto la de quien llama y devuelve `{ "revoked": <count> }`. Ambos notifican además a las partes de confianza de cada sesión terminada (cierre de sesión por back-channel y por front-channel) y revocan las concesiones vinculadas a ella, de modo que los tokens de actualización guardados en ese dispositivo dejan de funcionar. La página de cuenta de la interfaz de inicio de sesión muestra esta lista cuando hay un registro de sesiones registrado.

### Comprobación de SSO {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

Comprueba si el dominio del correo electrónico exige SSO:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Si no se exige SSO:

```json
{
  "ssoRequired": false
}
```

### Política de contraseñas {#password-policy}

```
GET /api/auth/password-policy
```

Devuelve los requisitos de contraseña del servidor (configurados mediante `PasswordPolicy` en la configuración):

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

La interfaz de inicio de sesión predeterminada consulta este endpoint en la página de restablecimiento de contraseña para mostrar los requisitos de forma dinámica.

## Requisitos de contraseña predeterminados {#default-password-requirements}

Con la configuración predeterminada, las contraseñas deben cumplir todos estos requisitos:

- Al menos 8 caracteres
- Al menos una letra mayúscula
- Al menos una letra minúscula
- Al menos un dígito
- Al menos un carácter no alfanumérico
- Al menos 2 caracteres distintos

Se pueden personalizar mediante la sección de configuración `PasswordPolicy`; consulte [Configuración](configuration).

## Endpoints de MFA {#mfa-endpoints}

### Verificación de MFA {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifica una comprobación de MFA. Si tiene éxito, establece la cookie de autenticación y devuelve la información del usuario.

**Métodos:**

| `method` | Campos obligatorios | Descripción |
|---|---|---|
| `totp` | `code` (6 dígitos) | Contraseña de un solo uso basada en el tiempo, de una aplicación de autenticación |
| `webauthn` | `assertion` (cadena JSON) | Respuesta de aserción de WebAuthn de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Código de recuperación de un solo uso (se consume al usarlo) |

**Semántica de los reintentos:** un código incorrecto **no** consume la comprobación: el código se valida primero y la comprobación solo se consume si tiene éxito, de modo que el usuario puede reintentar con el mismo `challengeId` tras teclear mal un dígito (`401 invalid_code` / `assertion_failed`). Cada comprobación tolera **5 intentos fallidos**; el quinto fallo la consume y devuelve `401 too_many_attempts`, lo que obliga a iniciar sesión de nuevo (esto limita la fuerza bruta contra TOTP a 5 intentos por comprobación). Las comprobaciones también caducan (5 minutos por defecto, `Auth:MfaChallengeExpiryMinutes`); un `challengeId` caducado, desconocido o ya consumido devuelve `invalid_challenge`. Los códigos TOTP tienen además protección contra la reutilización: se rechaza un código de un intervalo de tiempo ya usado.

### Estado de MFA {#mfa-status}

```
GET /api/auth/mfa/status
```

Devuelve los métodos de MFA inscritos del usuario. Requiere autenticación por cookie o la cabecera `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` es `false` cuando el `MfaPolicy` de todos los clientes es `Disabled`, es decir, el inquilino tiene la MFA desactivada, de modo que la interfaz de configuración puede ocultarse. Las entradas de códigos de recuperación llevan además `isConsumed`.

### Configuración inicial de TOTP {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuración inicial de WebAuthn / passkey {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

La inscripción de una passkey requiere **primero una credencial TOTP confirmada** (`400 totp_required_first`): las passkeys son una comodidad por dispositivo que se superpone a un factor base portátil, de modo que una cuenta nunca puede quedarse solo con passkeys y atada a un dispositivo. Los usuarios cuyo dominio de correo electrónico se enruta a SSO no pueden inscribir una passkey local (`400 sso_managed`), porque eludiría el IdP del inquilino. Un ID de credencial ya registrado en **cualquier** cuenta, incluida la del propio usuario que se inscribe, se rechaza con `409 credential_already_registered`, porque un duplicado reiniciaría el contador de firmas de esa credencial y haría que dos filas compartieran una misma entrada de búsqueda.

### Códigos de recuperación {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Genera 10 códigos de recuperación de un solo uso. Requiere que haya al menos un método principal (TOTP o WebAuthn) inscrito. Regenerarlos sustituye todos los códigos de recuperación existentes.

### Eliminar una credencial de MFA {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Elimina una credencial de MFA concreta. Si se elimina el último método principal, la MFA se desactiva para el usuario. Requiere una sesión de cookie real: un token de configuración inicial se rechaza con `403 session_required` (los tokens de configuración solo existen para añadir un primer factor, nunca para rebajar la MFA).

### Inicio de sesión sin contraseña con passkey {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Inicio de sesión con credencial detectable (passkey residente) sin contexto previo del usuario: `begin` emite un desafío de aserción con una lista `allowCredentials` vacía, y `complete` resuelve el usuario **a partir de** la passkey elegida, verifica la aserción y le inicia sesión (la sesión lleva el marcador de MFA, ya que una passkey es una autenticación fuerte resistente al phishing). Como no se identificó a ningún usuario antes de la ceremonia, el paso 6 de WebAuthn §7.2 hace aquí obligatorio el identificador de usuario del autenticador: una aserción sin él se rechaza con `401 user_handle_required`, y una que nombre una cuenta distinta de la propietaria de la credencial, con `401 credential_not_found`. Si el dominio de correo electrónico del usuario resuelto se enruta a SSO, el inicio de sesión se rechaza con `409 sso_required` + `redirectUrl`, para que una passkey local no pueda esquivar un IdP obligatorio.

## Autorización de dispositivos (RFC 8628) {#device-authorization-rfc-8628}

### Solicitar un código de dispositivo {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Devuelve un código de dispositivo, un código de usuario y un URI de verificación:

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

`expires_in` procede del `DeviceCodeLifetimeSeconds` del cliente (300 por defecto). El dispositivo muestra al usuario el `verification_uri` y el `user_code`, y después consulta periódicamente el endpoint de tokens con el `device_code`, con una separación no inferior a `interval` segundos, o el endpoint de tokens responde `slow_down` (RFC 8628 §3.5). Mientras el usuario no lo haya aprobado, el endpoint de tokens devuelve `authorization_pending`. El usuario visita el URI de verificación, inicia sesión e introduce el código de usuario para aprobarlo.

### Mostrar la solicitud antes de aprobarla {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Requiere autenticación por cookie. Describe lo que concedería el código, para que la pantalla de aprobación pueda mostrar al usuario qué aplicación lo solicita antes de que la apruebe (un flujo de dispositivo iniciado por un atacante y aprobado en una solicitud opaca es el patrón de consentimiento ilícito del que advierte RFC 8628 §5.4):

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` es lo que se concedería realmente, tras el control de roles por usuario sobre los ámbitos restringidos por rol, no la solicitud sin procesar. Errores: `401 not_authenticated`, `400 user_code_required`, `400 invalid_user_code` (desconocido, consumido o caducado), `400 expired`. Comparte el contador de límite de frecuencia de la aprobación (más abajo).

### Aprobar el dispositivo {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Requiere autenticación por cookie y una solicitud del mismo origen. `scopes` es opcional (separado por espacios): solo puede reducir aquello a lo que el usuario tiene derecho, nunca ampliarlo, y omitirlo concede todo aquello a lo que tiene derecho. Aprueba el código de dispositivo para el usuario actual y devuelve `200 { "approved": true }`. El dispositivo puede entonces canjear el código de dispositivo por tokens en el endpoint de tokens con el tipo de concesión `urn:ietf:params:oauth:grant-type:device_code`.

El código enviado se normaliza según RFC 8628 §6.1 antes de buscarlo: se pasa a mayúsculas y se descarta todo carácter que no pertenezca al alfabeto de 31 caracteres de los códigos. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` y un copiar y pegar que convirtió el guion en una raya son todos el mismo código. El guion solo existe para que el código sea más fácil de leer en voz alta.

| Estado | `error` | Significado |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | Igual que para `info` |
| 400 | `invalid_scope` | Se indicó `scopes`, pero ninguno de ellos es un ámbito al que el usuario tenga derecho |
| 403 | `access_denied` | El usuario no tiene derecho a ninguno de los ámbitos solicitados (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | La política de MFA efectiva del cliente es `Required` y el usuario no tiene segundo factor; inscríbase y vuelva a aprobar |

La introducción del código tiene un límite de diez intentos por minuto y por sujeto (RFC 8628 §5.1), compartido entre `info`, `approve` y `deny`; el undécimo devuelve `429`. Ese contador es por nodo con el limitador de frecuencia en proceso predeterminado, por lo que un despliegue con varias réplicas también debería aplicar el límite en el perímetro.

### Denegar el dispositivo {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Requiere autenticación por cookie y una solicitud del mismo origen. Registra la negativa del usuario y devuelve `200 { "success": true }`. La siguiente consulta del dispositivo al endpoint de tokens recibe `access_denied` (RFC 8628 §3.5) en lugar de `authorization_pending` hasta que el código caduca. Los mismos errores y el mismo contador de límite de frecuencia que `info`.

## Introspección de tokens (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

O con credenciales codificadas como formulario:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Devuelve los metadatos del token:

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

Los tokens inactivos o no válidos devuelven `{ "active": false }`. Admite tanto tokens de acceso JWT como tokens de actualización opacos.

## Endpoints de consentimiento {#consent-endpoints}

### Información del consentimiento {#consent-info}

```
GET /consent/info?client_id=my-app
```

Requiere autenticación por cookie. Devuelve los detalles del cliente y los ámbitos solicitados para la página de consentimiento. Los ámbitos no se toman de la cadena de consulta: son la oferta que el endpoint de autorización registró para este usuario y este cliente (tras el filtrado por derechos de rol), de modo que un enlace manipulado no puede poner el nombre de un cliente de confianza encima de una lista de permisos elegida por quien llama.

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

`scopeDetails` va en paralelo a `scopes` (mismo orden, una entrada por ámbito), de modo que una aplicación de inicio de sesión que solo lee `scopes` sigue funcionando. Cada entrada lleva la presentación registrada para ese ámbito:

| Campo | Significado |
|---|---|
| `name` | El nombre del ámbito, como en `scopes`. |
| `displayName` | El nombre para mostrar registrado, o `null` cuando el ámbito no está registrado. |
| `description` | La descripción registrada, o `null`. |
| `emphasize` | `true` cuando el ámbito está registrado como relevante, de modo que la pantalla puede destacarlo. El valor predeterminado es `false`. |
| `required` | `true` cuando el ámbito está registrado como no rechazable: la pantalla lo muestra marcado y bloqueado. El valor predeterminado es `false`. |
| `group` | El encabezado bajo el que agrupar el ámbito, o `null` para mostrarlo por separado. |

Un ámbito que no está registrado produce `null` en `displayName`, `description` y `group` y `false` en los dos indicadores, y la aplicación de inicio de sesión recurre a sus propios textos. Consulte [Ámbitos](scopes) para registrar los textos.

Errores:

| Estado | Cuerpo | Cuándo |
|---|---|---|
| `401` | ninguno | No hay ningún usuario con la sesión iniciada. |
| `404` | `{ "error": "client_not_found" }` | `client_id` desconocido. |
| `400` | `{ "error": "no_pending_consent_request" }` | No hay ninguna oferta de consentimiento vigente para este usuario y este cliente (no se registró ninguna, o caducó). |

### Enviar el consentimiento {#submit-consent}

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

Registra la decisión de consentimiento del usuario (requiere autenticación por cookie) y devuelve `{ "redirect": "..." }` para que la SPA navegue a ella. Si se permite, los ámbitos concedidos se persisten (filtrados por el `AllowedScopes` del cliente, de modo que un cuerpo manipulado no puede registrar ámbitos que el cliente no podría solicitar) y la redirección apunta de vuelta al flujo de autorización. Con `"decision": "deny"`, la redirección apunta al `redirect_uri` del cliente con un error `access_denied`.

### Listar concesiones {#list-grants}

```
GET /consent/grants
```

Devuelve todas las aplicaciones que el usuario ha autorizado:

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

### Revocar una concesión {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

Revoca el consentimiento de una aplicación concreta. Se pedirá al usuario que vuelva a dar su consentimiento en su próximo inicio de sesión.

## Descubrimiento y claves de firma (JWKS) {#discovery-and-signing-keys-jwks}

Ambos son públicos y anónimos. Son lo que usa un servidor de recursos para validar los tokens que emite este servidor.

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- Las dos rutas de metadatos devuelven el mismo documento de descubrimiento; su `jwks_uri` es `{issuer}/.well-known/openid-configuration/jwks`.
- El JWKS enumera todas las claves de firma no caducadas (`kty`, `use`, `kid`, `alg`, y `crv`/`x`/`y` para las claves EC). La rotación publica la siguiente clave con días de antelación, de modo que a una copia en caché nunca le falta la clave con la que se firmó un token.
- Las respuestas llevan `Cache-Control: public, max-age=3600`.
- La firma es solo ES256; el descubrimiento anuncia `id_token_signing_alg_values_supported: ["ES256"]`.
- El emisor procede de `ITenantContext` y las claves de `IKeyManager`, de modo que un host multiinquilino con un gestor de claves por inquilino sirve claves por inquilino.

## Comportamiento del endpoint de autorización {#authorization-endpoint-behaviour}

`GET /connect/authorize` es el punto de entrada del flujo de código de autorización. Hay dos comportamientos que importan a cualquiera que cree un cliente o una interfaz de inicio de sesión contra él.

### Emisor en la respuesta (RFC 9207) {#issuer-in-the-response-rfc-9207}

Cada redirección de vuelta al `redirect_uri` del cliente lleva un parámetro de consulta `iss` con el emisor, tanto si tiene éxito (junto a `code` y `state`) como si hay error (junto a `error`, `error_description` y `state`). Lo mismo se aplica a la redirección de error cuando un usuario deniega el consentimiento en `/consent`. El documento de descubrimiento lo anuncia con `authorization_response_iss_parameter_supported: true`. Un cliente que se comunica con varios servidores de autorización debe comparar `iss` con el emisor con el que inició el flujo, que es lo que neutraliza el ataque de confusión (mix-up); los clientes que ignoran el parámetro no se ven afectados. Los errores que se producen antes de conocer un `redirect_uri` de confianza (`client_id` desconocido, un URI de redirección no registrado) se devuelven como cuerpo de error JSON, no como redirección, por lo que no llevan `iss`.

### `prompt` y `max_age` {#prompt-and-max_age}

| Solicitud | Comportamiento |
|---|---|
| `prompt=login` | Se cierra la sesión existente y se envía al usuario a `/login` para que vuelva a autenticarse. El `prompt` se elimina del `returnUrl` para que el nuevo inicio de sesión no se vea obligado a volver a autenticarse en bucle. En una [solicitud enviada (pushed)](par), el prompt va en la carga almacenada, y el bucle se rompe exigiendo que el `auth_time` de la sesión sea igual o posterior al momento en que se envió la solicitud |
| `prompt=select_account` | Se trata como `prompt=login`: el servidor mantiene una sesión por navegador, así que la elección de cuenta es la pantalla de inicio de sesión |
| `prompt=create` | Un usuario no autenticado se envía a `/login/register` en lugar de al formulario de inicio de sesión. Una sesión existente simplemente continúa |
| `prompt=consent` | La pantalla de consentimiento se muestra aunque una concesión almacenada satisfaga la solicitud, una vez por solicitud (el marcador de satisfecho es de un solo uso) |
| `prompt=none` | Nunca se muestra ninguna interfaz. El servidor responde con una redirección que lleva `login_required` (sin sesión), `interaction_required` (se necesita una verificación adicional de MFA o una inscripción) o `consent_required` (se necesita consentimiento) |
| `max_age=N` | Si el `auth_time` de la sesión tiene más de `N` segundos, o falta, el usuario se vuelve a autenticar exactamente igual que con `prompt=login`. `max_age=0` siempre vuelve a autenticar |

`prompt=none` combinado con cualquier otro valor se rechaza con `invalid_request`, igual que cualquier valor distinto de `none`, `login`, `consent`, `select_account` y `create`. El host integrable `Authagonal.Protocol` trata `prompt=login`, `select_account`, `none` y `max_age` de la misma forma, pero no tiene interfaz de consentimiento, así que responde a `prompt=consent` con `consent_required`.

## Crear una interfaz de inicio de sesión personalizada {#building-a-custom-login-ui}

La SPA predeterminada (`login-app/`) es una implementación de esta API. Para crear la suya:

1. Sirva su interfaz en las rutas `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. El endpoint de autorización redirige a los usuarios no autenticados a `/login?returnUrl={encoded-authorize-url}`
3. Tras un inicio de sesión correcto (cookie establecida), redirija al usuario al `returnUrl`
4. Los enlaces de restablecimiento de contraseña usan `{Issuer}/login/reset-password?p={token}` (la SPA de inicio de sesión se monta en `/login`)

Su interfaz debe servirse desde el **mismo origen** que la API porque:
- La autenticación por cookie usa `SameSite=Lax` + `HttpOnly`
- El endpoint de autorización redirige a `/login` (relativo)
- Los enlaces de restablecimiento usan `{Issuer}/login/reset-password`
