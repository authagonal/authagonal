---
layout: default
title: Autenticación multifactor
locale: es
---

# Autenticación multifactor (MFA)

Authagonal admite la autenticación multifactor. Hay tres métodos disponibles: TOTP (aplicaciones de autenticación), WebAuthn/passkeys (llaves de hardware y biometría) y códigos de recuperación de un solo uso. Las passkeys también se pueden usar para el [inicio de sesión sin contraseña](#passwordless-passkey-login).

Los inicios de sesión federados (SAML/OIDC) también están cubiertos: una aserción SAML u OIDC demuestra el primer factor, no el segundo. Un usuario federado con MFA inscrita pasa por el mismo desafío de MFA local que un inicio de sesión con contraseña, y una política `Required` obliga a la inscripción antes de emitir cualquier sesión. Solo cuando la MFA no está inscrita ni es obligatoria basta la federación por sí sola. Una conexión puede excluirse del desafío local con `ChallengeMfaAfterLogin: false` (consulte más abajo).

## Métodos admitidos {#supported-methods}

| Método | Descripción |
|---|---|
| **TOTP** | Contraseñas de un solo uso basadas en el tiempo (RFC 6238): 6 dígitos, intervalo de 30 segundos, SHA-1, verificadas con una ventana de desfase de reloj de un intervalo. Funciona con cualquier aplicación de autenticación (Google Authenticator, Authy, 1Password, etc.). Un código que ya se aceptó no se puede reutilizar dentro de su ventana de validez. |
| **WebAuthn / passkeys** | Llaves de seguridad de hardware FIDO2, biometría de la plataforma (Touch ID, Windows Hello) y passkeys sincronizadas. Los usuarios pueden registrar varias passkeys, y las passkeys permiten iniciar sesión sin contraseña. |
| **Códigos de recuperación** | 10 códigos de respaldo de un solo uso (10 caracteres de un alfabeto de 32 caracteres, mostrados como `XXXXX-XXXXX`) para recuperar la cuenta cuando los demás métodos no están disponibles. Se almacenan con hash y cifrados en reposo. |

## Política de MFA {#mfa-policy}

La aplicación de la MFA se configura **por cliente** mediante la propiedad `MfaPolicy` en `appsettings.json`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (por defecto) | No obliga a la inscripción; la interfaz de configuración inicial de autoservicio oculta la MFA cuando todos los clientes son `Disabled` |
| `Enabled` | Ofrece la inscripción en MFA; no la impone |
| `Required` | Obliga a la inscripción a los usuarios sin MFA |

A un usuario con MFA inscrita **siempre se le plantea el desafío al iniciar sesión, sea cual sea la política del cliente**. La MFA es una propiedad del usuario y de su sesión, no del cliente que hace la solicitud, de modo que una solicitud encaminada a través de un cliente `Disabled` no puede usarse para saltarse el segundo factor de un usuario inscrito.

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

El valor por defecto es `Disabled`, así que los clientes existentes no se ven afectados hasta que usted lo active.

### Sustitución por usuario {#per-user-override}

Implemente `IAuthHook.ResolveMfaPolicyAsync` para sustituir la política del cliente para usuarios concretos:

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

La política resultante rige la inscripción (si se ofrece o se impone). No exime del desafío a un usuario ya inscrito; a los usuarios inscritos siempre se les plantea el desafío.

Consulte [Extensibilidad](extensibility) para la documentación completa de los hooks.

## Flujo de inicio de sesión {#login-flow}

El flujo de inicio de sesión con MFA funciona así:

1. El usuario envía el correo electrónico y la contraseña a `POST /api/auth/login`
2. El servidor verifica la contraseña y después resuelve la política de MFA efectiva
3. Según la política y el estado de inscripción del usuario:

| Política | ¿El usuario tiene MFA? | Resultado |
|---|---|---|
| Cualquiera | Sí | Devuelve `mfaRequired`: el usuario debe verificarse |
| `Disabled` / `Enabled` | No | Se establece la cookie y el inicio de sesión se completa |
| `Required` | No | Devuelve `mfaSetupRequired`: el usuario debe inscribirse |

### Desafío de MFA {#mfa-challenge}

Cuando se devuelve `mfaRequired`, la respuesta del inicio de sesión incluye un `challengeId`, los `methods` disponibles del usuario y (cuando el usuario tiene passkeys) las opciones de aserción `webAuthn`. El cliente redirige a una página de desafío de MFA donde el usuario se verifica con uno de sus métodos inscritos mediante `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` es `totp`, `recovery` o `webauthn` (WebAuthn envía una `assertion` en lugar de un `code`).

Los desafíos caducan a los 5 minutos (configurable mediante `Auth:MfaChallengeExpiryMinutes`) y se consumen cuando la verificación tiene éxito.

#### Margen de reintentos {#retry-budget}

Un código incorrecto no agota el desafío. El endpoint de verificación valida primero el código y solo consume el desafío si tiene éxito, de modo que un dígito de TOTP mal tecleado se puede volver a intentar sin más con el mismo `challengeId`. Los intentos fallidos devuelven `invalid_code` (o `assertion_failed` para WebAuthn) con un 401 e incrementan un contador acotado en el desafío; el quinto intento erróneo consume el desafío y devuelve `too_many_attempts`, lo que obliga a iniciar sesión de nuevo. Esto se aplica a los tres métodos.

El margen por desafío es una vía rápida, no el límite de seguridad, así que a `POST /api/auth/mfa/verify` se le aplican dos controles más:

- **Límite de frecuencia por usuario.** Más de 10 intentos de verificación por minuto para un mismo usuario devuelven `too_many_attempts` con un 429, sea cual sea el `challengeId` que se use.
- **Bloqueo de cuenta compartido.** Cada código fallido cuenta también para el mismo contador de intentos fallidos que el paso de la contraseña (`Auth:MaxFailedAttempts`, `Auth:LockoutDurationMinutes`). Cuando salta, el desafío se consume y la respuesta es `locked_out` (423). Mientras la cuenta está bloqueada, la verificación se rechaza con `locked_out` antes de comprobar el código.

Solo las credenciales confirmadas pueden satisfacer una verificación; una inscripción que se inició pero nunca se completó no cuenta como factor.

Un desafío inexistente, caducado o ya consumido devuelve `invalid_challenge`.

### Inicios de sesión federados {#federated-logins}

Tras una aserción SAML u OIDC correcta, el servidor resuelve la misma política de MFA efectiva. Un usuario con MFA inscrita se redirige a la página alojada de desafío de MFA (con un `challengeId`) en lugar de recibir una sesión; un usuario sin MFA bajo una política `Required` se redirige a la página de configuración inicial de MFA (con un `setupToken`). La sesión solo se marca como autenticada con MFA una vez que se completa la verificación.

Este desafío es por conexión: una conexión SAML u OIDC con `ChallengeMfaAfterLogin` establecido en `false` omite el desafío local para los usuarios que llegan a través de ella. El valor por defecto es `true`.

### Inscripción obligatoria {#forced-enrollment}

Cuando se devuelve `mfaSetupRequired`, la respuesta incluye un `setupToken`. Este token autentica al usuario ante los endpoints de configuración inicial de MFA (mediante el encabezado `X-MFA-Setup-Token`) para que pueda inscribir un método antes de obtener una sesión con cookie. Los tokens de configuración inicial caducan a los 15 minutos (configurable mediante `Auth:MfaSetupTokenExpiryMinutes`).

## Inscripción en MFA {#enrolling-mfa}

Los usuarios se inscriben en MFA mediante los endpoints de configuración inicial de autoservicio. Estos requieren una sesión con cookie autenticada o un token de configuración inicial.

### Configuración inicial de TOTP {#totp-setup}

1. Llame a `POST /api/auth/mfa/totp/setup`, que devuelve un código QR (`data:image/png;base64,...`), una `manualKey` (Base32 para la introducción manual) y el token de configuración inicial
2. El usuario escanea el código QR con su aplicación de autenticación
3. El usuario introduce el código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

El paso de confirmación se limita igual que la verificación: más de 10 intentos por minuto para un mismo usuario devuelven `too_many_attempts` (429) y, con un token de configuración inicial, el quinto código erróneo consume el desafío de configuración inicial. Una inscripción sin confirmar caduca a los 30 minutos (`setup_expired`).

### Configuración inicial de WebAuthn / passkeys {#webauthn--passkey-setup}

1. Llame a `POST /api/auth/mfa/webauthn/setup`, que devuelve un `setupToken` y `PublicKeyCredentialCreationOptions`
2. El cliente llama a `navigator.credentials.create()` con las opciones
3. Envíe la respuesta de atestación a `POST /api/auth/mfa/webauthn/confirm`

La inscripción de passkeys requiere antes una credencial TOTP confirmada (`totp_required_first`). Las passkeys son una comodidad por dispositivo que se superpone a un factor base portátil, de modo que toda cuenta conserva un factor independiente del dispositivo y una política `Required` no puede satisfacerse solo con una passkey.

Los usuarios pueden registrar varias passkeys (una por dispositivo). Un ID de credencial ya registrado (en cualquier cuenta, incluida la del propio usuario que se inscribe) se rechaza con `credential_already_registered` (409). Volver a inscribir un autenticador que ya está inscrito crearía una segunda fila de credencial que comparte un mismo ID de credencial: su contador de firmas volvería a empezar, lo que debilitaría la detección de clones, y eliminar cualquiera de las dos filas borraría la entrada de búsqueda de la que dependen ambas. La entrada de búsqueda se reserva con una escritura de inserción solo si no existe, de modo que dos registros del mismo ID de credencial no pueden tener éxito a la vez. Los usuarios cuyo dominio de correo se encamina a un IdP externo mediante SSO forzado no pueden inscribir una passkey local (`sso_managed`), ya que eso eludiría el IdP y su desaprovisionamiento.

### Host de la parte de confianza {#relying-party-host}

El ID y el origen de la parte de confianza (RP) de FIDO2 se resuelven por solicitud a partir del host, de modo que cada nombre de host de inquilino es su propia parte de confianza. Establezca `Auth:WebAuthnAllowedHosts` con los nombres de host que sirve, para que un host fuera de esa lista no pueda actuar como parte de confianza. Una lista vacía (el valor por defecto) mantiene el comportamiento anterior en lugar de dejar fuera a los usuarios de passkeys existentes al actualizar, y se registra como una carencia en el primer uso. No es un estado en el que convenga quedarse. Establecer además `AllowedHosts` en `appsettings.json`, para que el filtrado de hosts de ASP.NET Core rechace los encabezados `Host` no reconocidos antes de que se ejecute ningún manejador, es la capa exterior más barata.

Con independencia de esa lista, cada credencial registra la parte de confianza bajo la que se inscribió y se rechaza en cualquier otra. Esa es la parte en la que la solicitud no puede influir: de lo contrario, ambas ceremonias construyen sus expectativas a partir del mismo encabezado `Host` que están verificando, de modo que el origen y el `rpIdHash` se comparaban con un valor proporcionado por quien llama, y un host intermediario que reenvíe su propio `Host` haría que la vinculación al origen (la propiedad que hace que una passkey resista el phishing) lo diera por bueno en lugar de impedirlo. Las credenciales inscritas antes de que se registrara el ID de RP no lo llevan y siguen funcionando; obtienen la vinculación cuando se vuelven a inscribir.

### Códigos de recuperación {#recovery-codes}

Llame a `POST /api/auth/mfa/recovery/generate` para generar 10 códigos de un solo uso. Antes debe haber inscrito al menos un método principal confirmado (TOTP o WebAuthn) (`primary_method_required`), y la llamada requiere una sesión autenticada real: un token de configuración inicial recibe `session_required` (403).

Cada código tiene 10 caracteres de un alfabeto de 32 caracteres y se muestra como dos grupos de cinco (`XXXXX-XXXXX`).

Regenerar los códigos sustituye todos los códigos de recuperación existentes. Cada código solo se puede usar una vez; un código canjeado se marca como consumido y deja de aceptarse.

Los códigos nunca se almacenan en texto plano: cada código se somete a hash, y el hash se cifra además en reposo con el proveedor de secretos del inquilino, de modo que un volcado del almacenamiento produce texto cifrado en lugar de un hash atacable por fuerza bruta sin conexión.

## Inicio de sesión sin contraseña con passkey {#passwordless-passkey-login}

Las passkeys no son solo un segundo factor: un usuario con una passkey inscrita puede iniciar sesión sin contraseña.

1. `POST /api/auth/mfa/passwordless/begin` devuelve un `challengeId` y las `options` de aserción para credenciales detectables, de modo que el autenticador ofrece cualquier passkey residente para el sitio
2. El cliente llama a `navigator.credentials.get()` con las opciones
3. `POST /api/auth/mfa/passwordless/complete` con `{ challengeId, assertion }`: el servidor resuelve el usuario a partir de la propia passkey e inicia su sesión

La página de inicio de sesión alojada integra esto en el campo de correo electrónico mediante mediación condicional (autocompletado de passkeys): cuando el navegador lo admite, se ofrece una passkey disponible como sugerencia de autocompletado sin ninguna interfaz adicional.

Una passkey es una autenticación fuerte resistente al phishing, así que la sesión resultante lleva el marcador de MFA y no se le vuelve a plantear el desafío. Si el dominio de correo del usuario se encamina a un IdP externo mediante SSO forzado, el inicio de sesión sin contraseña se rechaza con una respuesta 409 `sso_required` que incluye la URL de redirección de SSO, de modo que una passkey local no puede esquivar el IdP.

## Gestión de la MFA {#managing-mfa}

### Autoservicio del usuario {#user-self-service}

- `GET /api/auth/mfa/status`: consulta los métodos inscritos (también indica si algún cliente ofrece la MFA)
- `DELETE /api/auth/mfa/credentials/{id}`: elimina una credencial concreta

Eliminar una credencial requiere una sesión autenticada real; un token de configuración inicial solo autoriza a añadir un primer factor y aquí recibe `session_required`, de modo que un token de configuración inicial filtrado no puede rebajar la MFA de un usuario.

Si se elimina el último método principal, la MFA se deshabilita para el usuario.

### API de administración {#admin-api}

Los administradores pueden gestionar la MFA de cualquier usuario mediante la [API de administración](admin-api):

- `GET /api/v1/profile/{userId}/mfa`: consulta el estado de MFA de un usuario
- `DELETE /api/v1/profile/{userId}/mfa`: restablece toda la MFA (para usuarios que se han quedado sin acceso)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: elimina una credencial concreta

### Hooks de auditoría {#audit-hooks}

Implemente `IAuthHook.OnMfaVerifiedAsync` para registrar los eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Todo el ciclo de vida de la MFA admite hooks: `OnMfaVerifyFailedAsync` (un intento de verificación fallido), `OnMfaEnrolledAsync` (un método confirmado), `OnMfaCredentialRemovedAsync` (una credencial eliminada, con un indicador de si eso deshabilitó la MFA) y `OnRecoveryCodesRegeneratedAsync`.

## Interfaz de inicio de sesión personalizada {#custom-login-ui}

Si está creando una interfaz de inicio de sesión personalizada, gestione estas respuestas de `POST /api/auth/login`:

1. **Inicio de sesión normal**: `{ userId, email, name }` con la cookie establecida. Redirija a `returnUrl`.
2. **MFA obligatoria**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Muestre el formulario de desafío de MFA.
3. **Configuración inicial de MFA obligatoria**: `{ mfaSetupRequired: true, setupToken }`. Muestre el flujo de inscripción en MFA.

Al gestionar los errores de `POST /api/auth/mfa/verify`: `invalid_code` y `assertion_failed` admiten reintento con el mismo `challengeId` (hasta agotar el margen de intentos); `too_many_attempts` e `invalid_challenge` son definitivos, así que devuelva al usuario al formulario de inicio de sesión.

Consulte [API de autenticación](auth-api) para la referencia completa de endpoints.
