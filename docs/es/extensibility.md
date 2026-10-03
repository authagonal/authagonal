---
layout: default
title: Extensibilidad
locale: es
---

# Extensibilidad

Authagonal se puede alojar como biblioteca en su propio proyecto de ASP.NET Core, con control total sobre las implementaciones de los servicios.

## Métodos de extensión {#extension-methods}

Tres métodos integran Authagonal en cualquier aplicación de ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Alojamiento multiinquilino {#multi-tenant-hosting}

Para despliegues multiinquilino, use `AddAuthagonalCore()` en su lugar. Registra los endpoints, el middleware y los servicios principales, pero omite el almacenamiento y los servicios en segundo plano; usted los proporciona por inquilino. La gestión de claves de firma usa por defecto el singleton `ProtocolKeyManager` de `Authagonal.Protocol`, y un host que registre su propio `IKeyManager` antes de `AddAuthagonalCore()` lo conserva:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` y las interfaces de almacén (`IClientStore`, `IScimTokenStore`, etc.) se resuelven desde `HttpContext.RequestServices` en el momento de la solicitud, de modo que los registros con ámbito de solicitud (scoped) funcionan correctamente para el aislamiento por inquilino.

### Integrar solo `Authagonal.Protocol` {#embedding-authagonalprotocol-alone}

Un host que solo quiere la superficie del protocolo OIDC (su propia autenticación, su propia canalización, endpoints `/connect/*` listos para usar) llama a `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` sin nada de `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` y `/connect/par` también rechazan http sin cifrar en esa configuración, según RFC 6749 §3.1/§3.2. Como el paquete se asigna en una canalización que no le pertenece, el requisito va en los endpoints como filtro y no como middleware, de modo que se cumple independientemente de cómo componga su canalización y de si asigna toda la superficie o un endpoint cada vez. Dos consecuencias que conviene conocer antes de actualizar:

- **Detrás de un proxy que termina TLS, llame a `UseForwardedHeaders` con el proxy declarado.** El filtro lee el esquema después del enrutamiento, así que un `X-Forwarded-Proto: https` reenviado lo satisface. Sin ese middleware, su host ve texto sin cifrar, lo que además significa que sus cookies no se marcan como `Secure` y que las URL absolutas que genera son incorrectas, así que merece la pena corregirlo en lugar de esquivarlo. Rellene `KnownProxies` / `KnownNetworks` al registrarlo: ASP.NET Core interpreta un conjunto de confianza vacío como "todo llamador es un proxy de confianza", lo que entrega el esquema a cualquiera que pueda llegar a su host. Si el cuerpo del rechazo menciona un `X-Forwarded-Proto` no aplicado, este es el middleware al que se refiere.
- **Un host que realmente sirve la superficie del protocolo por http activa la opción explícita**, igual que hace el servidor:

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

El descubrimiento y JWKS no se restringen, deliberadamente: son metadatos públicos, y un cliente que no puede leerlos no tiene forma de saber, de entrada, que necesita https.

Cuando usa `AddAuthagonal()` (el servidor completo), no lo configura por separado: `Auth:AllowInsecureHttp` se propaga automáticamente a las opciones del protocolo, de modo que un único interruptor gobierna toda la superficie.

## Sustituir servicios {#overriding-services}

Registre sus implementaciones personalizadas **antes** de llamar a `AddAuthagonal()`. Authagonal usa `TryAdd` internamente, así que sus registros tienen prioridad:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` es especial: es una canalización de registros múltiples. Registre tantos hooks como quiera (con cualquier ciclo de vida, `AddScoped` incluido) y todos se ejecutan en el orden de registro. El `NullAuthHook` sin efecto solo se añade cuando no se ha registrado ningún hook en el momento en que se ejecuta `AddAuthagonal()` / `AddAuthagonalCore()`, así que registre siempre sus hooks primero.

### Puntos de extensibilidad {#extensibility-points}

| Interfaz | Predeterminado | Propósito |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (sin efecto, se añade solo cuando no hay ningún hook registrado) | Hooks del ciclo de vida para eventos de autenticación: registro de auditoría, validación personalizada, webhooks. Se pueden registrar varios hooks; todos se ejecutan en orden |
| `IEmailService` | `NullEmailService` (sin efecto), o el remitente integrado de Resend cuando se configura `Email:ResendApiKey` | Envío de correo para verificación, restablecimiento de contraseña y avisos de cuenta existente |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Aprovisionamiento de usuarios en aplicaciones descendentes |
| `ISecretProvider` | `PlaintextSecretProvider`, o el `KeyVaultSecretProvider` integrado cuando se configura `SecretProvider:VaultUri` | Almacenamiento reversible de secretos (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lee de `IConfiguration`) | Resolución del inquilino para despliegues multiinquilino |
| `IKeyManager` | `ProtocolKeyManager` (singleton, de `Authagonal.Protocol`) | Gestión de claves de firma; sustitúyalo para aislar las claves por inquilino |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Resuelve las aplicaciones de aprovisionamiento disponibles; sustitúyalo para una resolución dinámica o por inquilino |
| `IAuditLogger` | `NullAuditLogger` (sin efecto) | Registro de auditoría de cambios de configuración y eventos relevantes para la seguridad |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (singleton, de `Authagonal.Protocol`) | Validar el contexto aportado por quien llama en una emisión `client_credentials` e imponer claims en el token, o rechazarla |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (singleton, de `Authagonal.Protocol`) | Correspondencia de sujetos para el intercambio de tokens de RFC 8693; consulte [Autenticación agéntica](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (scoped, lee `TurnstileOptions`) | Qué sitekey y qué secreto de Turnstile se aplican a esta solicitud |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (singleton, deniega todos los orígenes) | Orígenes autorizados a hacer llamadas de origen cruzado con credenciales a `/api/auth/*` |

Otros tres puntos de extensión residen en el **nivel de almacén** en lugar de en la inyección de dependencias: `IFieldCipher`, `IIndexTokenizer` e `IChangeWriter` (todos en `Authagonal.Core.Services`). Los proveedores de almacenamiento los aceptan como parámetros opcionales del constructor; consulte sus secciones más abajo.

## IAuthHook {#iauthhook}

La interfaz `IAuthHook` proporciona hooks en el ciclo de vida de la autenticación. Los métodos de la ruta crítica (autenticación, creación de usuarios, emisión de tokens) pueden lanzar una excepción para abortar la operación; los métodos más recientes son notificaciones a posteriori. Se pueden registrar varias implementaciones de `IAuthHook` y todas se ejecutan en el orden de registro.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parámetros {#parameters}

| Método | Notas y valores de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (los hosts pueden pasar los suyos, p. ej. un origen SCIM) |
| `OnUserDeletedAsync` | `"admin"`; solo notificación, es posible que el registro ya no se pueda leer |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Tipos de concesión: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Se llama tras verificar la contraseña; devuelve la política de MFA efectiva para el usuario. Predeterminado: devolver `clientPolicy` sin cambios. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Los mismos métodos que `OnMfaVerifiedAsync`. Se dispara solo tras unas credenciales de primer factor válidas, por lo que las ráfagas son una señal fuerte de un intento de eludir la MFA (distinta de `OnLoginFailedAsync`, la fase de la contraseña) |
| `OnEmailConfirmedAsync` | El usuario confirmó su correo electrónico mediante el enlace de verificación; ya está persistido |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; la credencial ya está activa |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` es true cuando la eliminación no dejó ningún factor principal |
| `OnRecoveryCodesRegeneratedAsync` | El conjunto anterior de códigos de recuperación queda invalidado |
| `OnPasswordChangedAsync` | p. ej. `"reset"`; el cambio está persistido y las sesiones existentes, invalidadas |
| `OnTokenIssuingAsync` | Control previo a la emisión, a diferencia de `OnTokenIssuedAsync`. Se dispara en `authorization_code`, `refresh_token` y `device_code`, y en las dos emisiones agénticas (intercambio de tokens delegado, y `client_credentials` para un cliente con perfil de agente). Lance una excepción para rechazar: una excepción normal se convierte en `access_denied` con su mensaje; lance `ProtocolTokenException` para indicar su propio error OAuth. En la actualización se ejecuta antes de la rotación, de modo que un rechazo deja utilizable el token de actualización presentado. El contexto lleva `ClientId`, `SubjectId`, `GrantType`, `Scopes`, `RequestedAuthorityJson` y `OrganizationId` / `OrganizationSlug` cuando la solicitud seleccionó una organización |
| `OnDelegationMintedAsync` | Se emitió un token delegado (de identidad compuesta) mediante intercambio de tokens; solo notificación |
| `OnApprovalRequestedAsync` | Un intercambio delegado quedó en espera ante una acción con política de consulta y se creó una aprobación pendiente |
| `OnApprovalResolvedAsync` | El usuario aprobó o denegó una aprobación pendiente |
| `OnAgentConsentChangedAsync` | `change` es `"granted"` o `"revoked"` (consentimiento permanente del agente) |
| `OnConsentRevokedAsync` | Un usuario revocó una aplicación autorizada; el consentimiento y las concesiones del cliente vinculadas a la sesión ya no existen. `grantsRemoved` indica cuántas se eliminaron (0 significa ninguna) |
| `OnCapabilityTicketRedeemedAsync` | Se canjeó un ticket de capacidad por su token vinculado |

### Ejemplo: registro de auditoría {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Ejemplo: restricción de dominio {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

Un token `client_credentials` no tiene sujeto, por lo que el punto de extensión del intercambio de tokens no puede llegar a él. Este punto de extensión está pensado para un llamador de servicio propio cuyo token debe nombrar el contexto en el que actúa (una organización, un inquilino) sin un usuario. Se ejecuta después de validar el cliente, sus ámbitos y cualquier recurso de RFC 8707, y antes de emitir el token.

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` contiene los parámetros de formulario de la solicitud de token que no son del protocolo (de valor único; gana el primero), por ejemplo un `organization_id` que haya enviado quien llama.
- Devuelva `ClientCredentialsClaimsResult.Allow(claims)` para imponer `claims` en el token (null o vacío lo deja sin cambios), o `ClientCredentialsClaimsResult.Reject(error, description)` para rechazar la emisión con ese error OAuth.
- Los nombres de claims reservados del protocolo siguen bloqueados en la emisión.
- Valide el vínculo aportado por quien llama contra su propia autoridad; no lo copie en el token sin comprobarlo.
- El `NullClientCredentialsClaimsTransformer` predeterminado se registra con `TryAddSingleton`, así que registre el suyo primero para sustituirlo.

## ITurnstileKeyProvider {#iturnstilekeyprovider}

Ambas claves de Turnstile proceden de un mismo objeto, de modo que el widget que muestra el navegador y el secreto con el que verifica el servidor nunca pueden discrepar. El `OptionsTurnstileKeyProvider` predeterminado lee `SiteKey` y `SecretKey` de `TurnstileOptions`, lo que sirve a un host que atiende un único dominio. Un host que atiende dominios aportados por clientes, donde Cloudflare limita los nombres de host de un widget, registra su propia implementación scoped que devuelve el par de claves del widget asignado al host solicitante.

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

Se registra con `TryAddScoped`, así que un registro realizado antes de `AddAuthagonal` tiene prioridad.

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

La API de autenticación interactiva (`/api/auth/*`) rechaza por defecto las llamadas de origen cruzado con credenciales, porque la usa la aplicación de inicio de sesión servida desde el mismo origen. Un host que permite a un inquilino crear su propia pantalla de inicio de sesión en otro origen implementa esto para responder por orígenes concretos.

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- Se consulta por solicitud y por origen; la resolución del inquilino ya se ha ejecutado cuando se llama.
- Devolver true permite a ese origen leer las respuestas autenticadas de los endpoints de cuenta, sesión, perfil y configuración de MFA para quien haya iniciado sesión. Responda solo por orígenes que el host controle o haya verificado, nunca por uno tomado de la solicitud.
- El predeterminado (`DenyInteractiveCorsOriginPolicy`, `TryAddSingleton`) devuelve false para todos los orígenes.

## ISecretProvider {#isecretprovider}

`ISecretProvider` (en `Authagonal.Core.Services`) es el punto de extensión de cifrado reversible para secretos almacenados, como los secretos de cliente de SSO, las contraseñas SMTP y las semillas TOTP. `ProtectAsync` convierte un texto sin cifrar en una referencia que el almacén persiste; `ResolveAsync` convierte de nuevo la referencia en el texto sin cifrar. El `PlaintextSecretProvider` predeterminado almacena los valores tal cual (la referencia ES el valor).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Establecer `SecretProvider:VaultUri` conecta automáticamente el `KeyVaultSecretProvider` integrado (Azure Key Vault mediante `DefaultAzureCredential`). Para cualquier otra cosa, registre su propia implementación antes de `AddAuthagonal()`.

## Cifrado de campos de PII: IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` cifra en reposo los valores de campos individuales de PII del usuario (teléfono, empresa, atributos personalizados, correo electrónico y nombres en la fila del perfil). Es un punto de extensión del nivel de almacén: los proveedores de almacenamiento lo reciben como parámetro opcional del constructor (p. ej. `TableUserStore`) y, cuando falta, se aplica el `NullFieldCipher` de paso directo, de modo que el cifrado es estrictamente opcional y los hosts sin configurar siguen almacenando texto sin cifrar.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Hay dos puntos del contrato que importan. `ProtectAsync` debe devolver un token de texto cifrado autodescriptivo (p. ej. `vault:v{n}:...` de Vault Transit), y `ResolveAsync` debe dejar pasar sin cambios un valor que no reconozca como su propio texto cifrado. La regla de paso directo es lo que permite desplegar el cifrado de forma diferida sobre las filas existentes: una lectura de una fila sin migrar devuelve el texto sin cifrar heredado, y la siguiente escritura lo vuelve a proteger.

## Búsqueda por índice ciego: IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` mantiene los campos cifrados disponibles para búsquedas. Convierte un valor normalizado sin cifrar en un token de índice ciego determinista y seguro como clave de tabla, normalmente un HMAC con clave cuya clave reside fuera de la base de datos. El determinismo significa que una búsqueda por igualdad sigue funcionando ("email = x" se convierte en "token = HMAC(x)"), mientras que un volcado de la base de datos no puede ni recalcular ni invertir un token. La búsqueda por prefijo se construye encima convirtiendo en token por separado cada prefijo de un valor, ya que un HMAC con clave destruye el orden y los escaneos por rango.

> **Lo que un volcado sigue revelando.** "Ni recalcular ni invertir" es cierto para un único token, no para
> el índice en su conjunto. Sobreviven tres residuos, y conviene conocerlos antes de confiar en esto:
>
>   *(Corregido.)* ~~**Estructura.** El índice de prefijos escribe una fila por prefijo, así que el número de filas de un registro
>   es igual a la longitud del campo indexado.~~ Cada valor indexado escribe ahora un número fijo de filas,
>   completado con señuelos que ninguna consulta puede producir y que un volcado no puede distinguir de los prefijos reales.
> - **Igualdad y frecuencia.** Los tokens son deterministas por construcción, que es lo que hace que la búsqueda
>   funcione, así que un volcado muestra qué registros comparten un valor y lo común que es cada valor. El índice de dominios
>   agrupa a su población por empleador, lo que a menudo identifica a las personas sin recuperar una dirección.
> - **Texto sin cifrar elegido.** Un atacante que pueda a la vez leer el almacén *y* hacer que se indexen valores
>   (registrar una cuenta, ser aprovisionado por SCIM) puede enviar un candidato y buscar su token.
>   Eso recupera cualquier valor adivinable (dominios comunes, nombres de pila comunes) esté donde esté la clave,
>   porque el oráculo es la vía de escritura y no el cifrado.
>
> La conversión en tokens protege frente al caso para el que se creó: alguien que tiene un volcado y nada más
> e intenta leer direcciones. Los dos residuos que quedan son exactamente lo que un oráculo de registro revela
> de todos modos. Si son inaceptables, deje sin configurar las tablas de índices de prefijos y de dominios
> (la búsqueda por coincidencia exacta no conlleva ninguno de los dos) en lugar de suponer que el HMAC los cubre.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Al igual que `IFieldCipher`, es un parámetro opcional del constructor del almacén con un predeterminado de paso directo (`NullIndexTokenizer`), de modo que las filas de índice siguen usando el texto sin cifrar como clave hasta que lo active. Los tokens devueltos deben ser seguros como valores de PartitionKey/RowKey de Azure Table (sin ninguno de `/ \ # ?` ni caracteres de control).

## Captura del registro de cambios: IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (renombrado desde `ITombstoneWriter` en 0.6.0) registra la clave de cada fila modificada en una tabla dedicada de registro de cambios, para que las copias de seguridad incrementales encuentren lo que cambió sin escanear la columna `Timestamp`, no indexada, de las tablas activas. Las eliminaciones se capturan en todas las tablas (un escaneo de filas activas no puede ver una fila que ya no existe); las inserciones o actualizaciones se capturan en las tablas que la copia de seguridad lee del registro en lugar de escanearlas. Implementaciones integradas: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) y `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrato de orden para implementadores y llamadores: escriba el tombstone de eliminación ANTES de eliminar la fila de datos. Un fallo en el orden inverso pierde la eliminación en todas las copias de seguridad futuras, ya que las eliminaciones son la única clase de mutación que un nuevo escaneo no puede corregir por sí solo. El fallo contrario es seguro: una escritura posterior en la clave vuelve a estampar una marca de tiempo más reciente, y la fusión y la restauración conservan las filas escritas después del tombstone.

## Endpoints personalizados {#custom-endpoints}

Añada sus propios endpoints junto a los de Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Integración con HashiCorp Vault Transit {#hashicorp-vault-transit-integration}

> **La firma de JWT no se delega en Vault.** Esta sección mostraba antes un fragmento de inyección de dependencias que parecía
> habilitarla. Registrar `VaultTransitCryptoProvider` **no tiene ningún efecto en la firma de tokens**:
> `ProtocolKeyManager` llama a `ProtocolSigningKeyOps.BuildSigningCredentials`, que construye una
> `ECDsaSecurityKey` a partir del material de `ISigningKeyStore`, y nada la sustituye por una
> `VaultTransitSecurityKey`. Un host que siguió el fragmento anterior veía que los tokens ES256 se verificaban contra JWKS
> y concluía, con razón aparente, que Vault los estaba firmando, mientras que la clave privada se generaba localmente en el primer arranque
> y se persistía en el almacén de datos principal, sin cifrar salvo que hubiera registrado un `IFieldCipher`.
> El acceso de lectura a ese almacén permite suplantar por completo al emisor. Si tiene un requisito de cumplimiento normativo según el cual
> las claves de firma nunca deben salir de un HSM, esto no lo satisface.
>
> El servidor registra ahora un error al iniciarse si encuentra `VaultTransitCryptoProvider` registrado, para que el
> malentendido no pueda persistir sin aviso.
>
> Hacerlo realidad requiere más que un registro en la inyección de dependencias: `ISigningKeyStore` tendría que representar una clave que
> no tiene material local (un *nombre* de clave de Transit en lugar de un escalar privado), `BuildSigningCredentials` necesitaría un
> punto de extensión para devolver una `VaultTransitSecurityKey`, `BuildJwksAsync` tendría que publicar la clave pública leída
> de Vault, y la rotación y la publicación anticipada tendrían que crear y promover versiones de claves de Transit en lugar de
> generarlas localmente. `VaultTransitClient`, `VaultTransitSecurityKey`, `VaultTransitSignatureProvider` y
> `VaultTransitCryptoProvider` se conservan porque son las piezas que funcionan; lo que falta es la conexión entre ellas.

Para lo que `VaultTransitClient` **sí** sirve hoy es para los puntos de extensión de cifrado y HMAC: un
`IFieldCipher` respaldado por Vault para la PII en reposo, o un `IIndexTokenizer` para índices ciegos con clave:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

Registrar un `IFieldCipher` es también lo que silencia `PlaintextSigningKeyWarning`, porque los almacenes de claves de
firma envían su material de clave a través de ese mismo punto de extensión, que es lo más parecido a la afirmación original
que está disponible hoy: la clave privada sigue existiendo localmente, pero no sin cifrar.

`VaultTransitClient` proporciona estas operaciones:

| Método | Descripción |
|---|---|
| `SignAsync(keyName, data)` | Firma datos con una clave de Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Verifica una firma en formato JWS mediante el endpoint de verificación de Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Cifrado simétrico con una clave `aes256-gcm96`; devuelve tokens `vault:v{n}:...` que se almacenan tal cual |
| `HmacAsync` / `HmacBatchAsync` | HMAC con clave bajo una clave `hmac` (tokens de índice ciego) |
| `CreateKeyAsync(keyName, type)` | Crea una nueva clave de Transit (predeterminado: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garantiza de forma idempotente que existe una clave con el tipo deseado (la vuelve a crear si el tipo no coincide; las claves de Transit no se pueden cambiar de tipo en su lugar) |
| `RotateKeyAsync(keyName)` | Rota una clave a una nueva versión |
| `DeleteKeyAsync(keyName)` | Elimina una clave (habilita antes `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Lee los metadatos, las versiones y las claves públicas de una clave |
| `KeyExistsAsync(keyName)` | Comprueba si existe una clave |

`VaultTransitCryptoProvider` se integra con `JsonWebTokenHandler` de .NET para que la firma de JWT use Vault de forma transparente. `VaultTransitSecurityKey` y `VaultTransitSignatureProvider` se encargan de la integración de bajo nivel.

## Correo electrónico {#email}

El remitente integrado de Resend se activa automáticamente cuando se configura `Email:ResendApiKey` (establezca también `Email:SenderEmail`). Sin ningún `IEmailService`, el correo se descarta mediante `NullEmailService` y, como el control de inicio de sesión con correo confirmado está activado por defecto, los usuarios autorregistrados nunca podrían iniciar sesión; `UseAuthagonal()` registra una advertencia destacada al iniciarse en ese estado.

Para usar otro proveedor, registre su propio `IEmailService` antes de `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` también declara `SendAccountExistsEmailAsync` (se envía cuando alguien intenta registrar un correo electrónico ya registrado, para que la respuesta del registro siga siendo neutral frente a la enumeración de cuentas). Tiene una implementación predeterminada sin efecto, así que las implementaciones existentes siguen compilando.

## Véase también {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): ejemplo completo y funcional
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): ejemplo de aplicación cliente
