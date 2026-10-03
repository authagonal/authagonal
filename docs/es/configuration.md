---
layout: default
title: Configuración
locale: es
---

# Configuración

Authagonal se configura mediante `appsettings.json` o variables de entorno. Las variables de entorno usan `__` como separador de secciones (p. ej., `Storage__ConnectionString`).

## Ajustes obligatorios {#required-settings}

El almacenamiento se puede configurar de dos maneras: indique **o bien** `Storage:ConnectionString` **o bien** `Storage:TableServiceUri` (la vía de identidad administrada, preferible en producción).

| Ajuste | Variable de entorno | Descripción |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Cadena de conexión de Azure Table Storage con una clave de cuenta. Adecuada para desarrollo / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint de Table Storage con identidad administrada, p. ej. `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` y **preferible en producción**: se autentica mediante `DefaultAzureCredential`, de modo que ninguna clave de acceso acaba nunca en un secreto. El host debe conceder a la identidad de carga de trabajo el rol **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | La URL base pública de este servidor (p. ej., `https://auth.example.com`) |

## Almacenamiento {#storage}

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(ninguno)* | Cadena de conexión con clave de cuenta (consulte Ajustes obligatorios). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(ninguno)* | URI de Table Storage con identidad administrada (consulte Ajustes obligatorios). Tiene prioridad sobre `Storage:ConnectionString` cuando se establecen ambos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Si se mantienen las tablas de índice de búsqueda por prefijo `UserFirstNames` / `UserLastNames` en las que se apoya la búsqueda de administración por prefijo del nombre. Establezca `false` en los hosts que no expongan la búsqueda de administración por nombre para omitir esas escrituras. **Nota sobre el escalado:** estos índices usan una única partición muy activa y limitan el rendimiento a unas 2.000 operaciones por segundo a escala; desactívelos si no necesita la búsqueda por nombre. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base a la que el endpoint `/connect/authorize` redirige para la SPA de inicio de sesión (pantallas de inicio de sesión, verificación adicional y consentimiento). Establézcala cuando la interfaz de inicio de sesión se sirva desde un origen distinto del servidor; por defecto es la ruta relativa `/login` que sirve la SPA incluida. |

## Autenticación {#authentication}

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Duración de la sesión de la cookie (deslizante) |
| `Authentication:AllowInsecureCookie` | `false` | Permite enviar la cookie de sesión por http sin cifrar (`SameAsRequest` en lugar de `Always`). **Solo para desarrollo.** La cookie ES la sesión, y `SameAsRequest` solo parece equivalente detrás de un proxy que termina TLS: depende de que `X-Forwarded-Proto` llegue y se considere de confianza, de modo que un ingress mal configurado, una sonda de estado por HTTP sin cifrar o un proxy que descarte la cabecera producen una cookie sin Secure que luego viaja en cualquier solicitud sin cifrar al mismo host. El fallo es silencioso. |
| `Authentication:CookieDomain` | *(sin establecer)* | Limita la cookie de sesión a un dominio padre, de modo que se envíe a los subdominios hermanos (`app.example.com` además de `auth.example.com`). **Esto tiene el coste de la vinculación al origen:** la cookie ya no puede llevar el prefijo `__Host-`, que es lo que hace que el navegador la rechace salvo que sea Secure, tenga `Path=/` y no tenga `Domain`, de modo que cualquier subdominio que pueda establecer cookies en el padre, y cualquier cosa que pueda apoderarse de uno, queda dentro del alcance. Déjelo sin establecer salvo que un origen hermano necesite realmente la sesión. |
| `Auth:AllowInsecureHttp` | `false` | Permite que los endpoints OAuth (`/connect/*`) respondan a solicitudes http sin cifrar. **Solo para desarrollo.** RFC 6749 §3.1/§3.2 exigen TLS en los endpoints de autorización y de tokens, por lo que, por defecto, una solicitud que no sea https a cualquiera de ellos se rechaza con `invalid_request`. El esquema se evalúa *después* del procesamiento de las cabeceras reenviadas, de modo que un proxy que termina TLS y reenvía `X-Forwarded-Proto: https` supera el control con este ajuste desactivado, siempre que ese proxy esté declarado en [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms), sin lo cual la cabecera se ignora. Solo lo necesita un despliegue realmente sin cifrar (el `docker-compose.yml` incluido, la demo del servidor personalizado), y el servidor registra una advertencia al iniciarse siempre que está activado. Se propaga a `AuthagonalProtocolOptions.AllowInsecureHttp`, por lo que también gobierna los endpoints que pertenecen a `Authagonal.Protocol` (consulte [Extensibilidad](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Se niega a iniciarse cuando el marco compartido de .NET es anterior al mínimo de seguridad que exige Authagonal (**9.0.18 / 10.0.10**). El mínimo existe porque las correcciones de GHSA-37gx-xxp4-5rgx y GHSA-w3x6-4m5h-cxqf (un bucle infinito y un par de XXE / agotamiento de recursos en `System.Security.Cryptography.Xml`, ambos accesibles desde el endpoint ACS de SAML **anónimo**) se distribuyen en el runtime, no en un paquete que esta biblioteca pueda fijar, de modo que ninguna de sus dependencias puede garantizarlas. Con `false`, un runtime antiguo genera un registro `Critical` y el servidor se inicia: negarse por defecto convertiría una actualización de versión de Authagonal en una interrupción del servicio en una flota cuyo runtime vaya un parche por detrás. Establézcalo en `true` donde sea preferible no iniciarse a servir XML no autenticado sobre un runtime sin parchear. |
| `Auth:MaxFailedAttempts` | `5` | Intentos fallidos de inicio de sesión antes del bloqueo de la cuenta |
| `Auth:LockoutDurationMinutes` | `10` | Duración del bloqueo de la cuenta tras alcanzar el máximo de intentos fallidos |
| `Auth:MaxLoginAttemptsPerIp` | `30` | Intentos de contraseña permitidos por dirección de origen en cada `Auth:LoginWindowMinutes` y (por separado) por correo electrónico enviado en la misma ventana. El bloqueo por cuenta no puede limitar un ataque de difusión de contraseñas (un intento contra cada una de miles de cuentas), y cada intento no autenticado paga un PBKDF2 completo, de modo que esto limita ambas cosas. Superarlo responde `429 too_many_attempts` (`AuthEndpoints.cs:107-119`). |
| `Auth:LoginWindowMinutes` | `5` | Ventana de `Auth:MaxLoginAttemptsPerIp` |
| `Auth:MaxRegistrationsPerIp` | `5` | Número máximo de registros por dirección IP dentro de la ventana |
| `Auth:RegistrationWindowMinutes` | `60` | Ventana de limitación de frecuencia de los registros |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Número máximo de correos de restablecimiento de contraseña por dirección de destino dentro de la ventana (con el correo electrónico como clave, no la IP de quien llama, para que no se pueda inundar de correos una dirección) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Número máximo de solicitudes de contraseña olvidada por IP de origen dentro de la ventana. El límite por correo electrónico acota el correo enviado a una víctima; este acota a un llamador que recorre una lista de direcciones, lo que de otro modo sería correo anónimo ilimitado desde su dominio de envío verificado más una lectura del almacén por dirección. |
| `Auth:PasswordResetWindowMinutes` | `60` | Ventana de limitación de frecuencia del restablecimiento de contraseña |
| `Auth:DurableRateLimiting` | `false` | Mantiene los contadores de límite de frecuencia en el almacén configurado para que todas las réplicas compartan un mismo presupuesto, en lugar de que cada nodo mantenga el suyo. Cuesta un viaje de ida y vuelta al almacén por comprobación; un despliegue de un solo nodo no gana nada. Requiere un proveedor que proporcione `IRateLimitCounterStore` (Azure, SQL, AWS). En caso contrario, el host se niega a iniciarse en lugar de volver sin aviso a los límites por nodo. Consulte [Límites para todo el clúster](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(vacío)* | Dominios de correo electrónico (matriz de cadenas) cuyos registros de autoservicio se confirman automáticamente: se omite el correo de verificación. Vacío (el predeterminado) significa que todo registro debe verificarse. Pensado solo para desarrollo y pruebas; no incluya nunca un dominio que pueda recibir correo real. |
| `Auth:AllowPasswordlessAccountClaim` | `false` | Registrar un correo electrónico que pertenece a una cuenta existente **sin credencial local** (federada o aprovisionada por JIT) prepara una contraseña en ella en lugar de devolver la respuesta de duplicado neutral frente a la enumeración. La credencial preparada y los atributos quedan inactivos hasta que quien toma posesión de la cuenta hace clic en un nuevo correo de verificación, de modo que conocer el correo electrónico de una cuenta federada no basta para apoderarse de ella. Una cuenta que ya tiene contraseña nunca se ve afectada. Consulte [Actualizar un usuario](user-upgrade). |
| `Auth:ClaimAllowedAttributeKeys` | *(vacío)* | Claves de atributos personalizados que una toma de posesión sin contraseña puede trasladar de la solicitud de registro a la cuenta de la que se toma posesión. Vacío permite todas las claves (compatibilidad con versiones anteriores); enumere claves para restringir lo que una toma de posesión puede inyectar en el aprovisionamiento descendente y en los tokens. |
| `Auth:EmailVerificationExpiryHours` | `24` | Duración del enlace de verificación del correo electrónico |
| `Auth:PasswordResetExpiryMinutes` | `60` | Duración del enlace de restablecimiento de contraseña |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Duración del token de comprobación de MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Duración del token de configuración inicial de MFA (para la inscripción obligatoria) |
| `Auth:WebAuthnAllowedHosts` | *(vacío)* | Hosts autorizados a actuar como parte de confianza de WebAuthn. Vacío acepta cualquier host (los despliegues existentes siguen funcionando) y es una brecha: el RP ID y el origen esperado se derivan, en otro caso, de la propia solicitud que se valida. En un despliegue multiinquilino, enumere todos los hosts de los inquilinos. Consulte [MFA](mfa). |
| `Auth:Pbkdf2Iterations` | `100000` | Número de iteraciones de PBKDF2 para el hash de contraseñas |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Tiempo mínimo real durante el que se retiene un inicio de sesión fallido antes de devolver `invalid_credentials`, medido desde el inicio de la solicitud. Cierra el oráculo de tiempos para la enumeración de usuarios: una cuenta inexistente se verifica contra un hash ficticio en el formato PBKDF2 nativo, pero una cuenta real puede seguir teniendo un hash importado de bcrypt, Scrypt.NET o ASP.NET Identity V3 con un coste distinto, por lo que es imposible igualar el trabajo y lo que se impone es igualar el tiempo transcurrido. Súbalo por encima del hash más lento que tenga el despliegue, p. ej. si importó bcrypt con un coste superior a 11, un hash `$s2$` de Scrypt.NET con un `N` alto, o subió `Pbkdf2Iterations` muy por encima del valor predeterminado. Se registra una única advertencia la primera vez que un inicio de sesión fallido lo supera. `0` desactiva el relleno y vuelve a abrir el oráculo. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Ventana de gracia opcional (en segundos) para la reutilización simultánea de tokens de actualización. `0` (predeterminado) mantiene la postura estricta: cualquier reutilización de un token de actualización consumido revoca todos los tokens de ese usuario y cliente. Establezca un valor `> 0` para tratar una reutilización dentro de la ventana como un reintento idempotente (vuelve a entregar los tokens sucesores), lo que resulta útil para clientes móviles con conectividad intermitente. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Habilita el endpoint de registro dinámico de clientes `POST /connect/register` (RFC 7591). Desactivado por defecto porque el registro abierto se presta a abusos en despliegues multiinquilino. Consulte [Registro dinámico de clientes](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(vacío)* | Ámbitos que un registrante anónimo puede asignarse a sí mismo, además de los integrados de OIDC que siempre se pueden registrar (`openid`, `profile`, `email`, `phone`, `offline_access`). Vacío significa los integrados y nada más: que un ámbito exista en el almacén no es permiso para que un cliente autorregistrado lo declare. Los ámbitos restringidos por rol nunca se pueden registrar, en ningún caso. Consulte [Registro dinámico de clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Duración de la clave de firma antes de la rotación automática (las claves son ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Con qué frecuencia se vuelven a cargar las claves de firma desde el almacenamiento |
| `Auth:KeyRotationEnabled` | `false` | Habilita la rotación automática de claves de firma |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Con qué frecuencia se comprueba si la clave activa necesita rotación |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rota cuando la clave activa caduca dentro de este número de días |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre comprobaciones del sello de seguridad de la cookie |
| `Auth:AllowedInternalTargets` | *(vacío)* | Destinos internos de los que Authagonal puede obtener datos en las vías en las que **usted** proporcionó la URL: metadatos SAML del IdP de origen, descubrimiento OIDC del IdP de origen, callbacks de aprovisionamiento. Vacío significa que se rechaza toda dirección interna. Consulte [Solicitudes salientes](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Envía esas mismas solicitudes configuradas por el operador a través del proxy HTTP del entorno, aceptando que la comprobación de direcciones no puede ver a través de él. Nunca se aplica a un `jwks_uri` registrado por un cliente ni a un URI de cierre de sesión por back-channel. Consulte [Solicitudes salientes](#outbound-fetches-ssrf-guard). |
| `Auth:AtRestBackfillEnabled` | `false` | Ejecuta una vez al iniciarse, en el líder del clúster, el relleno retroactivo del cifrado en reposo. Reescribe cada fila de usuario existente y sus filas de índice derivadas del perfil al esquema actual de cifrado en reposo, que es la vía de migración para habilitar `IFieldCipher` / `IIndexTokenizer` en un despliegue que ya tiene datos (consulte [Extensibilidad](extensibility#pii-field-encryption-ifieldcipher)). Registrar solo un cifrador cifra únicamente las filas escritas después. Supone un volumen real de escrituras, es idempotente y se ejecuta una vez por proceso, así que desactívelo cuando el registro indique una ejecución completa. |
| `Auth:MaxScimGroupsPerClient` | `5000` | Número máximo de grupos SCIM que puede poseer un cliente de aprovisionamiento; por encima de él, la creación se rechaza. El almacenamiento de grupos no está indexado, por lo que una tabla sin límite haría que cada emisión de tokens pagara por ella. |
| `Auth:MaxScimGroupMembers` | `10000` | Número máximo de miembros que puede tener un grupo SCIM; por encima de él, se rechazan la creación, la sustitución y la modificación parcial. |

## Data Protection {#data-protection}

Las claves de ASP.NET Core Data Protection (que cifran la cookie de sesión) deben compartirse entre instancias; consulte [Escalado](scaling#cookie-encryption-data-protection). Opciones de persistencia, por orden de precedencia:

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `DataProtection:BlobUri` | *(ninguno)* | URI explícito de Azure Blob para el conjunto de claves (p. ej. `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Se autentica mediante `DefaultAzureCredential`, la vía preferible en producción junto con `Storage:TableServiceUri`. |
| *(alternativa)* | *(ninguno)* | Cuando `DataProtection:BlobUri` no está establecido, el conjunto de claves se persiste automáticamente: en un contenedor `dataprotection` de la cuenta indicada por `Storage:ConnectionString` (salvo que sea Azurite) o, en la vía de identidad administrada, en el endpoint de blobs derivado de `Storage:TableServiceUri` (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), lo que requiere Storage Blob Data Contributor en la misma cuenta. Solo un endpoint de tablas no reconocido (Azurite, emuladores con estilo de ruta) recurre al almacén de archivos por máquina, que es efímero y por pod; `KeyRingStartupCheck` registra un mensaje Critical cuando ocurre. |

En el backend de AWS, pase un cliente de S3 y un bucket a `AddAuthagonalAwsStorage` para persistir el conjunto de claves en S3; consulte [Instalación → backend de AWS](installation#aws-backend). En el backend SQL, el conjunto de claves lo persisten `AddAuthagonalPostgres` / `AddAuthagonalSqlite`; consulte [Instalación → backend SQL](installation#sql-backend).

Persistir no es cifrar. Sea cual sea el backend que guarde el conjunto de claves, se escribe como XML sin cifrar (clave maestra incluida) salvo que se establezca uno de estos ajustes. Ese conjunto de claves protege la cookie de autenticación, de modo que poder leer el almacén equivale a poder falsificar una sesión de cualquier usuario:

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(ninguno)* | URI de clave de Azure Key Vault con el que se envuelve el conjunto de claves. Se autentica mediante `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(ninguno)* | Huella digital de un certificado del almacén de la máquina con el que se envuelve el conjunto de claves. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Acepta deliberadamente un conjunto de claves sin cifrar. Se vuelve a indicar con nivel `Critical` en cada inicio para que aparezca en una auditoría y no solo en un archivo de configuración. |

El inicio lo impone a partir de las opciones *resueltas* del conjunto de claves, de modo que se aplica igual a los repositorios de Azure, AWS y SQL y a cualquiera registrado por el host. Un despliegue que persiste el conjunto de claves sin cifrado y **aún sin claves** se rechaza, de modo que el estado inseguro nunca llega a crearse; uno cuyo conjunto **ya tiene claves** se inicia y registra con nivel `Critical`, porque negarse en ese caso tumbaría un despliegue en funcionamiento con una actualización de versión. En desarrollo nunca se rechaza.

## Caché y tiempos de espera {#cache-and-timeouts}

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Cuánto tiempo se almacenan en caché los orígenes CORS permitidos |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duración de la caché del documento de descubrimiento OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duración de la caché de los metadatos del IdP SAML |
| `Cache:OidcStateLifetimeMinutes` | `10` | Duración del parámetro de estado de la autorización OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Duración del ID de AuthnRequest de SAML (prevención de la reutilización) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Tiempo de espera de la comprobación de estado de Table Storage |
| `Cache:HealthCheckCacheSeconds` | `5` | Cuánto tiempo se reutiliza la respuesta de `/health` antes de volver a consultar el almacenamiento (coincide con el `Cache-Control: max-age` que anuncia el endpoint). `0` sondea en cada solicitud, lo que vuelve a abrir la amplificación anónima que la caché cierra. |

## Servicios en segundo plano {#background-services}

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Retardo inicial antes de la primera limpieza de tokens caducados |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalo de limpieza de tokens caducados |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Retardo inicial antes de la primera conciliación de concesiones |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalo de conciliación de concesiones |

### Barridos de caducidad (Azure Table) {#expiry-sweeps-azure-table}

Azure Table Storage no tiene TTL, así que en el backend de Azure el servidor ejecuta un `TableExpirySweepService` por tabla (cada 15 minutos, solo en el líder del clúster) sobre `MfaChallenges`, `RevokedTokens` y `UpstreamRefreshTokens`, eliminando las filas cuya caducidad ha pasado. Es solo retención: cada una de esas filas ya se rechaza al leerla mediante su propia comprobación de caducidad. Una fila sin caducidad indicada (posible en `UpstreamRefreshTokens`) no se barre nunca, deliberadamente. DynamoDB y SQL depuran esas mismas tres tablas de forma nativa. No hay nada que configurar.

## Protección contra bots (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

Opcional. Cuando se establece una clave secreta, el inicio de sesión, el registro, la contraseña olvidada y el restablecimiento de contraseña verifican un `turnstileToken` con Cloudflare antes de hacer ningún trabajo; sin clave secreta, nada cambia y no se muestra ningún widget.

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `Turnstile:SiteKey` | *(sin establecer)* | Sitekey pública, expuesta a la interfaz de inicio de sesión (`turnstileSiteKey` en `GET /api/auth/providers`) para que pueda mostrar el widget |
| `Turnstile:SecretKey` | *(sin establecer)* | Secreto para la verificación del lado del servidor. Sin establecer o vacío desactiva Turnstile por completo |

Un host que atiende dominios aportados por clientes no puede usar un único par de claves (Cloudflare limita los nombres de host de un widget); sustituye [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider). Consulte [API de autenticación](auth-api#providers) para el error `captcha_failed`.

## Roles {#roles}

Los roles se definen en la matriz `Roles` y se cargan al iniciar, junto con los clientes, los ámbitos y los
proveedores. Cargarlos importa sobre todo cuando un ámbito se restringe con
[`AllowedRoles`](scopes#role-gated-scopes): un ámbito restringido a un rol que nada crea queda restringido
para todos, incluido el operador que lo configuró, y falla sin aviso: el ámbito simplemente
nunca se concede.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Campo | Descripción |
|---|---|
| `Name` | El nombre del rol, tal como se usa en `Scope.AllowedRoles` y en el claim `roles` del token |
| `Description` | Legible por personas; se actualiza en arranques posteriores cuando la configuración indica una |
| `Members` | Correos electrónicos que se incluyen en el rol en cada arranque. Una dirección que aún no tiene usuario se omite con una advertencia y se reintenta en el siguiente arranque, de modo que el inicio nunca depende de una cuenta que alguien no ha creado |

La carga desde la configuración es **aditiva e idempotente**. Nunca elimina un rol ni revoca una pertenencia: la configuración
no es la fuente de verdad sobre quién tiene qué, así que un rol concedido mediante la API de administración sobrevive al
siguiente reinicio.

## Clientes {#clients}

Los clientes se definen en la matriz `Clients` y se cargan al iniciar. Cada cliente puede tener:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "SecretHashes": ["pbkdf2-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "Audiences": ["https://api.example.com"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "RestrictedToOrganizationIds": [],
      "InitiateLoginUri": "https://app.example.com/login",
      "ClientUri": "https://app.example.com",
      "IsDefaultApplication": false
    }
  ]
}
```

La carga es de **lectura, fusión y escritura**: un campo que la configuración no indica conserva el valor almacenado, de modo que un reinicio nunca deshace un cambio realizado mediante la API de administración (un cliente deshabilitado sigue deshabilitado, un secreto rotado sobrevive, `Audiences` y el JWKS del cliente se conservan). Un campo que la configuración sí indica se sobrescribe en cada arranque.

Notas sobre los campos (de `ClientSeedService.ClientSeedConfig`):

- **Alias.** `ClientId`/`Id`, `ClientName`/`Name`, `AllowedGrantTypes`/`GrantTypes`, `AllowedScopes`/`Scopes`, `AllowedCorsOrigins`/`CorsOrigins` y `RequireClientSecret`/`RequireSecret` son intercambiables. Un único objeto `SeedClient` también se lee como una entrada más.
- **Secretos.** Indique `SecretHashes` (ya con hash) o `ClientSecret` (sin cifrar, se le aplica el hash al iniciar y solo se usa cuando no se indican hashes). El valor de la configuración solo se aplica cuando aporta uno, de modo que un secreto rotado mediante la API de administración sobrevive al siguiente reinicio. No existe ninguna clave `ClientSecretHashes` en el formato de configuración.
- **`BackChannelLogoutUri`**: adonde se envían por POST los tokens de cierre de sesión por back-channel; consulte [Cierre de sesión por back-channel](#back-channel-logout).
- **`RestrictedToOrganizationIds`**: ids de las organizaciones con las que se puede usar el cliente. Vacío significa sin restricción; una sola entrada además selecciona esa organización para una solicitud que no nombre ninguna (consulte [Organizaciones](organizations)).
- **`InitiateLoginUri`, `ClientUri`, `IsDefaultApplication`**: alimentan la lista de `/api/auth/apps` y el botón de continuar a la aplicación de la pantalla de inicio de sesión.
- **No se pueden cargar desde la configuración.** `RequireConsent`, `ProvisioningApps`, `RequirePushedAuthorizationRequests`, el JWKS del cliente y los campos de cierre de sesión por front-channel no tienen clave en el formato de configuración, así que la configuración no puede establecerlos. Las claves desconocidas se ignoran sin advertencia.
- Una entrada cuyos ámbitos o audiencias incumplan las reglas de ámbitos reservados o de audiencias se rechaza con un registro de error y se omite.

### Audiencias e indicadores de recurso (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` es la lista de permitidos del cliente para el parámetro `resource` (RFC 8707) y para el parámetro `audience` de un intercambio de tokens (RFC 8693). Lo que supera esa comprobación se convierte en el claim `aud` del token de acceso emitido; si la solicitud no lleva `resource`, `aud` recurre a `Audiences` y, si no hay ninguno de los dos, es el `client_id`.

Una lista `Audiences` vacía significa **"ninguno"** para cualquier cliente que realmente respondió a la pregunta: uno cuya solicitud de creación llevaba el campo `audiences`, ya fuera mediante registro dinámico (donde el campo es una extensión de Authagonal a RFC 7591), la API de administración o la configuración. Un cliente así no puede nombrar ningún `resource`, en ninguna vía: la autorización, `client_credentials` y el intercambio de tokens coinciden en ello.

Un registro dinámico que **omite** `audiences` (todo cliente estándar de RFC 7591, es decir, todo cliente MCP) nunca recibió la pregunta. Su lista está "sin establecer" y puede nombrar cualquier URI absoluto como `resource`; la especificación de autorización de MCP depende de ello. La misma interpretación se aplica a los clientes almacenados antes de que existiera `AudiencesDeclared`, porque endurecer todos los clientes almacenados al actualizar rompería flujos que hoy funcionan.

| Cliente | `Audiences` vacío significa |
|---|---|
| La solicitud de creación llevaba `audiences` (campo de extensión de DCR, API de administración, configuración) | **denegar**: no se puede nombrar ningún `resource` |
| Registro DCR que omitió `audiences` | **"sin establecer"**: se acepta cualquier URI absoluto como `resource` |
| Almacenado antes de que existiera `AudiencesDeclared` | **"sin establecer"**: se acepta cualquier URI absoluto como `resource` |

**Adaptar un cliente heredado** consiste en un `PUT` a la API de administración de clientes con `audiencesDeclared: true` (y los `audiences` a los que deba quedar fijado). El indicador solo endurece: una actualización puede establecerlo y no puede borrarlo, de modo que una edición no relacionada nunca devolverá sin aviso a un cliente a la interpretación permisiva.

La consecuencia para las filas heredadas merece decirse claramente en lugar de dejarla enterrada:

> Un cliente preexistente sin `Audiences` configurado puede nombrar **cualquier** URI absoluto como `resource` en el endpoint de autorización o con `client_credentials`, y recibir un token de acceso cuyo `aud` es ese valor, firmado con la clave de este inquilino, con el `sub` del usuario solicitante y los ámbitos que el cliente tenga permitidos.

Una lista `audiences` declarada se valida al escribirla: como máximo 20 entradas de como máximo 512 caracteres, cada una un URI absoluto con esquema explícito y sin fragmento. Los valores de `resource` deben tener la misma forma; tenga en cuenta que una ruta sin más, como `/admin`, **no** se acepta, aunque el analizador `Uri` de .NET la considere un URI `file:` absoluto en Linux.

Nombrar un recurso no da acceso a él. Pero sí significa que el servidor de autorización no puede ser lo único que se interponga entre un cliente y una API a la que nunca debió llamar, así que:

- **Los servidores de recursos DEBEN autorizar en función de `scope`** (o de su propio modelo), no solo de `iss` + `aud` + `sub`. Un token que nombra su API en `aud` demuestra que el cliente pidió su API. No demuestra que el cliente tenga permiso para llamarla, y este servidor no puede hacer que lo demuestre.
- **Los servidores de recursos DEBEN validar `aud` contra su propio identificador**, no simplemente comprobar que "hay algún valor".
- **Establezca `Audiences` en todo cliente que deba quedar fijado a un conjunto fijo de API.** Con él configurado, un `resource` que no figure en la lista se rechaza con `invalid_target` en el endpoint de autorización y con `client_credentials`. Este es el único lugar donde se puede imponer la restricción.
- **Adapte con `audiencesDeclared: true` los clientes creados antes de que existiera**, para que su lista de audiencias vacía signifique "ninguno" y no "cualquiera".
- **Un cliente autorregistrado puede declarar `audiences`** al registrarse y queda sujeto a lo que declare, incluida una lista vacía. `Auth:DynamicClientRegistrationEnabled` sigue desactivado por defecto; consulte [Registro dinámico de clientes](client-registration).

### Tipos de concesión {#grant-types}

| Tipo de concesión | Caso de uso |
|---|---|
| `authorization_code` | Inicio de sesión interactivo del usuario (aplicaciones web, SPA, móviles) |
| `client_credentials` | Comunicación entre servicios |
| `refresh_token` | Renovación de tokens (requiere `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Concesión de autorización de dispositivos (RFC 8628) para dispositivos con entrada limitada |

### Uso del token de actualización {#refresh-token-usage}

| Valor | Comportamiento |
|---|---|
| `OneTime` (predeterminado) | Cada actualización emite un nuevo token de actualización e invalida el anterior. Por defecto (`Auth:RefreshTokenReuseGraceSeconds = 0`), cualquier reutilización de un token consumido revoca inmediatamente todos los tokens de ese usuario y cliente: **no** hay ninguna ventana de gracia activada por defecto. Establezca `Auth:RefreshTokenReuseGraceSeconds` en un valor positivo para activar una ventana de tolerancia a reintentos. |
| `ReUse` | Se reutiliza el mismo token de actualización hasta que caduca. |

### Aplicaciones de aprovisionamiento {#provisioning-apps}

La matriz `ProvisioningApps` de un cliente (que se lee en el momento de la autorización, `AuthorizeEndpoint.cs:578`; el cargador de configuración no la vincula y las rutas de clientes de la API de administración no la incluyen, así que la establece el host en el registro de cliente almacenado) hace referencia a ids de aplicaciones definidos en la sección de configuración `ProvisioningApps`. Cuando un usuario autoriza a través de este cliente, se le aprovisiona en esas aplicaciones mediante TCC. Consulte [Aprovisionamiento](provisioning) para más detalles.

## Ámbitos {#scopes}

Los [ámbitos de OAuth](scopes) personalizados se pueden cargar desde la matriz `Scopes`. Cada entrada se inserta o actualiza por `Name` al iniciar (una entrada sin `Name` se omite con una advertencia):

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

Un campo que usted establece prevalece sobre el valor almacenado en cada inicio; un campo que omite conserva el valor almacenado. Por tanto, la configuración puede añadir o cambiar `UserClaims` y `AllowedRoles`, pero no vaciarlos (use `PUT /api/v1/scopes/{name}` para eso). El significado de los campos está en [Modelo de ámbito](scopes#scope-model).

## Aplicaciones de aprovisionamiento {#provisioning-apps-1}

Defina las aplicaciones descendentes en las que se debe aprovisionar a los usuarios:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Consulte [Aprovisionamiento](provisioning) para la especificación completa del protocolo TCC.

## Política de MFA {#mfa-policy}

La autenticación multifactor se impone por cliente mediante la propiedad `MfaPolicy`:

| Valor | Comportamiento |
|---|---|
| `Disabled` (predeterminado) | Sin comprobación de MFA, aunque el usuario tenga MFA inscrita |
| `Enabled` | Se exige la comprobación a los usuarios que tienen MFA inscrita; no se obliga a inscribirse |
| `Required` | Se exige la comprobación a los usuarios inscritos; se obliga a inscribirse a los usuarios sin MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Cuando `MfaPolicy` es `Required` y el usuario no ha inscrito la MFA, el inicio de sesión devuelve `{ mfaSetupRequired: true, setupToken: "..." }`. El token de configuración inicial autentica al usuario ante los endpoints de configuración inicial de MFA (mediante la cabecera `X-MFA-Setup-Token`) para que pueda inscribirse antes de obtener una sesión de cookie.

Los inicios de sesión federados (SAML/OIDC) también respetan la política de MFA: un usuario con MFA inscrita pasa por la comprobación de MFA después de que el IdP externo lo autentique, y `Required` obliga a inscribirse a los usuarios federados sin MFA.

### Sustitución mediante IAuthHook {#iauthhook-override}

El método `IAuthHook.ResolveMfaPolicyAsync` puede sustituir la política del cliente por usuario:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Política de contraseñas {#password-policy}

Personalice los requisitos de seguridad de las contraseñas:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propiedad | Predeterminado | Descripción |
|---|---|---|
| `MinLength` | `8` | Longitud mínima de la contraseña |
| `MinUniqueChars` | `2` | Número mínimo de caracteres distintos |
| `RequireUppercase` | `true` | Exige al menos una letra mayúscula |
| `RequireLowercase` | `true` | Exige al menos una letra minúscula |
| `RequireDigit` | `true` | Exige al menos un dígito |
| `RequireSpecialChar` | `true` | Exige al menos un carácter no alfanumérico |

La política se impone en el restablecimiento de contraseña y en el registro de usuarios por parte de la administración. La interfaz de inicio de sesión obtiene la política activa de `GET /api/auth/password-policy` para mostrar los requisitos de forma dinámica.

## Proveedores SAML {#saml-providers}

Defina los proveedores de identidad SAML en la configuración. Se cargan al iniciar:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propiedad | Obligatoria | Descripción |
|---|---|---|
| `ConnectionId` | Sí | Identificador estable (se usa en URL como `/saml/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (por defecto, ConnectionId) |
| `EntityId` | Sí | El entity ID de SP de **este servidor**, el identificador que usted registra en el IdP, no el entity ID propio del IdP |
| `MetadataLocation` | Sí | URL del XML de metadatos SAML del IdP. Debe ser https y enrutable públicamente, salvo que el host figure en [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard): este documento contiene los certificados con los que se valida cada aserción. Si su IdP no publica ningún endpoint de metadatos https, establezca `metadataXml` mediante la [API de administración](admin-api); la configuración no tiene clave para ello. |
| `AllowedDomains` | No | Dominios de correo electrónico enrutados a este proveedor mediante SSO |
| `OrganizationId` | No | Limita esta conexión a una [organización](organizations). Null (el predeterminado) la convierte en una conexión de nivel de inquilino; solo las conexiones de nivel de inquilino registran sus `AllowedDomains` como rutas de dominio de SSO |
| `JitProvisioningEnabled` | No | Crea un usuario en el primer inicio de sesión. Predeterminado `false` |
| `AllowUninvitedJit` | No | Permite que JIT cree un usuario en una organización a la que no fue invitado. Predeterminado `false` |
| `ChallengeMfaAfterLogin` | No | Exige la comprobación de la política de MFA de la aplicación tras el inicio de sesión en el IdP. Predeterminado `true` |
| `ProvisioningAttributeParams` | No | Atributos de la aserción que se pasan al aprovisionamiento descendente |
| `AllowUnsolicitedResponses` | No | Acepta respuestas iniciadas por el IdP (no solicitadas) en esta conexión. Predeterminado `false` |

Los booleanos se escriben desde la configuración en cada arranque, incluido el valor predeterminado, de modo que una conexión cargada desde la configuración que un operador haya modificado mediante la API de administración los revierte en el siguiente reinicio. Los campos para los que la configuración no tiene clave (`SpCertificate`, `SignAuthnRequests`, `NameIdFormat`, `MetadataXml`, `IconUrl`) se conservan.

## Proveedores OIDC {#oidc-providers}

Defina los proveedores de identidad OIDC en la configuración. Se cargan al iniciar:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propiedad | Obligatoria | Descripción |
|---|---|---|
| `ConnectionId` | Sí | Identificador estable (se usa en URL como `/oidc/{connectionId}/login`) |
| `ConnectionName` | No | Nombre para mostrar (por defecto, ConnectionId) |
| `MetadataLocation` | Sí | URL del documento de descubrimiento OpenID Connect del IdP |
| `ClientId` | Sí | ID de cliente OAuth2 registrado en el IdP |
| `ClientSecret` | Sí | Secreto de cliente OAuth2 (protegido mediante `ISecretProvider` al iniciar) |
| `RedirectUrl` | No | **Se ignora.** El URI de redirección se deriva por solicitud como `{Issuer}/oidc/callback`: registre *ese* en el IdP. Un valor aquí no tiene efecto y se registra como ignorado. |
| `AllowedDomains` | No | Dominios de correo electrónico enrutados a este proveedor mediante SSO |
| `OrganizationId` | No | Limita esta conexión a una [organización](organizations); null significa nivel de inquilino |
| `JitProvisioningEnabled` | No | Crea un usuario en el primer inicio de sesión. Predeterminado `false` |
| `AllowUninvitedJit` | No | Permite que JIT cree un usuario en una organización a la que no fue invitado. Predeterminado `false` |
| `UseUpstreamSubjectAsUserId` | No | Usa el `sub` del IdP de origen como id de usuario local. Predeterminado `false` |
| `ShowOnLogin` | No | Muestra un botón para esta conexión en la pantalla de inicio de sesión. Predeterminado `true`; a las conexiones enrutadas por dominio se llega igualmente primero por el correo electrónico |
| `ChallengeMfaAfterLogin` | No | Exige la comprobación de la política de MFA de la aplicación tras el inicio de sesión en el IdP. Predeterminado `true` |
| `AutoLinkExistingByEmail` | No | Vincula un primer inicio de sesión a una cuenta local existente con el mismo correo electrónico. Predeterminado `false` |
| `PassthroughParams`, `ProvisioningAttributeParams` | No | Parámetros que se transmiten al IdP / al aprovisionamiento descendente |
| `RevalidateOnRefresh` | No | Vuelve a comprobar la sesión del IdP de origen cuando se canjea un token de actualización. Predeterminado `false` |
| `IsExternalConnection`, `SessionExpClaim` | No | Ajustes de sesiones federadas; consulte [Sesiones federadas](federated-sessions) |
| `InteractionPath` | No | Ruta de la aplicación de inicio de sesión (por ejemplo `/guest`) que se muestra antes de federar a través de esta conexión una solicitud `idp_hint` no autenticada. Vacío federa directamente |

La configuración OIDC sobrescribe más cosas que la SAML, en cada arranque. Los booleanos se escriben desde la configuración, incluido el valor predeterminado, de modo que una conexión cargada desde la configuración que un operador haya modificado mediante la API de administración los revierte en el siguiente reinicio. Lo mismo se aplica a `AllowedDomains`, `PassthroughParams`, `ProvisioningAttributeParams`, `SessionExpClaim` e `InteractionPath`: una clave que la configuración omite se restablece a vacío o a su valor predeterminado en lugar de conservar el valor almacenado. Solo `IconUrl` y `CreatedAt` se conservan siempre, y `ConnectionName` y `OrganizationId` se conservan cuando la configuración los omite.

> **Nota:** Los proveedores también se pueden gestionar en tiempo de ejecución mediante la [API de administración](admin-api). Los proveedores cargados desde la configuración se insertan o actualizan en cada inicio, así que los cambios de configuración surten efecto al reiniciar.

## Proveedor de secretos {#secret-provider}

Los secretos de cliente OIDC del IdP de origen y las semillas TOTP / MFA se pueden almacenar en Azure Key Vault en lugar de sin cifrar:

| Ajuste | Descripción |
|---|---|
| `SecretProvider:VaultUri` | URI de Key Vault (p. ej., `https://my-vault.vault.azure.net/`). Si no se establece, se usa el proveedor **sin cifrar** y los secretos se almacenan tal cual en Table Storage. |
| `SecretProvider:RequireVaultReferences` | `false` por defecto. Cuando es `true`, una referencia almacenada sin prefijo de almacén de secretos (`kv:` para Key Vault, `sm:` para AWS Secrets Manager) es un **error** en lugar de aceptarse como valor sin cifrar. Establézcalo cuando haya terminado una migración al almacén de secretos. |

Cuando está configurado, los valores secretos que parecen referencias de Key Vault se resuelven en tiempo de ejecución. Usa `DefaultAzureCredential` para la autenticación.

### Migrar a un almacén de secretos y cerrar la puerta después {#migrating-into-a-vault-and-closing-the-door-afterwards}

Ambos proveedores respaldados por un almacén de secretos devuelven tal cual una referencia sin prefijo, tratándola como un valor sin cifrar escrito antes de que el despliegue tuviera almacén de secretos. Eso es lo que permite migrar un sistema en funcionamiento secreto a secreto en lugar de todo a la vez, pero si se deja abierto es una vía de degradación permanente: cualquier cosa que pueda escribir una columna de configuración (una migración a medias, una vía de administración que almacene un valor sin procesar donde corresponde una referencia, un atacante con acceso al almacenamiento pero no al almacén de secretos) sustituye un secreto protegido por el almacén por un valor de su elección, y se verifica perfectamente, porque en una referencia sin prefijo la referencia *es* el valor.

Establezca `SecretProvider:RequireVaultReferences` cuando termine la migración. A partir de entonces, resolver una referencia sin prefijo lanza una excepción en lugar de devolver silenciosamente texto sin cifrar. Establecerlo mientras el proveedor resuelto es el de texto sin cifrar se rechaza al iniciar, ya que esa combinación no tiene ningún estado válido: todas las referencias que escribe el proveedor sin cifrar carecen de prefijo.

El servidor también registra una advertencia al iniciarse siempre que un host que no es de desarrollo termina con el proveedor sin cifrar.

> ⚠️ **Producción: establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **sin cifrar**. Cuando `SecretProvider:VaultUri` no está establecido, los secretos de cliente OIDC del IdP de origen y las semillas TOTP / MFA se escriben en Azure Table Storage sin cifrar y, por tanto, aparecen sin cifrar en cualquier [copia de seguridad](backup-restore). En cualquier despliegue de producción, configure `SecretProvider:VaultUri` para que estos secretos se almacenen en Key Vault.

## API de administración {#admin-api}

| Ajuste | Predeterminado | Descripción |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Habilitada por defecto.** Establézcalo en `false` para desactivar todos los endpoints de administración (no se registrarán). |
| `AdminApi:Scope` | `authagonal-admin` | Ámbito JWT necesario para acceder a los endpoints de administración. Cámbielo para que coincida con el nombre de su ámbito existente (p. ej., `projects-identity-admin` en migraciones desde IdentityServer). |

> ⚠️ **La API de administración está habilitada por defecto y tiene privilegios muy elevados.** El ámbito de administración concede gestión completa y suplantación de usuarios: cualquiera que tenga un token con `AdminApi:Scope` puede emitir tokens para cualquier usuario, gestionar clientes y leer y escribir toda la configuración. Restrinja a nivel de red los endpoints de administración (las rutas de administración `/api/v1/*`) y controle estrictamente a quién se le puede emitir el ámbito de administración. Como medida de defensa en profundidad, el ámbito está *reservado*: nunca se puede conceder a un cliente OAuth (consulte [API de administración](admin-api)) ni emitir mediante el endpoint de suplantación. Establezca `AdminApi:Enabled = false` si no se usa la API de administración.

## Consentimiento {#consent}

El consentimiento por cliente se puede habilitar con la propiedad `RequireConsent`:

| Valor | Comportamiento |
|---|---|
| `false` (predeterminado) | La autorización continúa inmediatamente después de la autenticación |
| `true` | Se muestra al usuario una pantalla de consentimiento con los ámbitos solicitados. El consentimiento se persiste durante 5 años y solo se vuelve a pedir cuando se solicitan ámbitos nuevos. |

Los usuarios pueden ver y revocar sus concesiones de consentimiento en `GET /consent/grants` y `DELETE /consent/grants/{clientId}`.

## Cierre de sesión por back-channel {#back-channel-logout}

Registre un `BackChannelLogoutUri` en un cliente para recibir notificaciones de OIDC Back-Channel Logout 1.0. Cuando un usuario cierra la sesión, Authagonal envía un token de cierre de sesión firmado (JWT) al URI registrado de cada cliente.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Correo electrónico {#email}

El remitente de correo integrado usa [Resend](https://resend.com) y **se activa automáticamente** cuando se configura `Email:ResendApiKey`, sin necesidad de registrar ningún servicio. Para usar otro proveedor, registre su propia implementación de `IEmailService` antes de llamar a `AddAuthagonal()` (tiene prioridad independientemente de las claves `Email:*`).

| Ajuste | Descripción |
|---|---|
| `Email:ResendApiKey` | Clave de API de Resend. Cuando se establece, se usa el remitente integrado de Resend. |
| `Email:SenderEmail` | Dirección de correo electrónico del remitente |
| `Email:SenderName` | Nombre para mostrar del remitente (por defecto `"Authagonal"`) |

> ⚠️ **Sin ningún remitente de correo, el autorregistro no funciona.** Cuando `Email:ResendApiKey` no está establecido y no hay ningún `IEmailService` personalizado registrado, un servicio sin efecto descarta silenciosamente todo el correo: los correos de verificación y de restablecimiento de contraseña nunca llegan y, como el inicio de sesión exige por defecto un correo electrónico confirmado, los usuarios autorregistrados nunca pueden iniciar sesión. `UseAuthagonal` registra una advertencia al iniciarse en ese estado. Vía de escape para desarrollo y pruebas: `Auth:AutoConfirmEmailDomains` confirma automáticamente los registros de los dominios indicados.

Los correos a direcciones `@example.com` se omiten silenciosamente (útil para pruebas).

## Clúster {#cluster}

La capa de clúster proporciona **elección de líder** (para que los trabajos reservados al líder, como la rotación de claves de firma, se ejecuten en exactamente un nodo) y un **bus de eventos entre nodos**, con backends intercambiables. El predeterminado es en proceso: un único nodo siempre es su propio líder, el ajuste adecuado para un solo nodo y para el desarrollo local, sin ninguna configuración.

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor general. Cuando es `false`, el nodo funciona de forma independiente (siempre líder, bus de eventos en proceso). |
| `Cluster:Secret` | `Cluster__Secret` | *(ninguno)* | Secreto compartido exigido en el endpoint de uso exclusivamente interno `/_internal/backchannel-logout`. Cuando se establece, los llamadores deben presentarlo en la cabecera `X-Cluster-Secret` (se compara en tiempo constante). Cuando **no está establecido, el endpoint no autoriza a nadie** y responde 404: una dirección de origen no es una credencial, y la dirección de bucle invertido es exactamente lo que presenta un proxy inverso en el mismo host en cada solicitud que reenvía, incluidas las originadas en Internet. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Opción explícita para desarrollo: sin `Cluster:Secret`, acepta a un llamador cuya **dirección de par previa al reenvío** sea de bucle invertido. Los rangos privados se siguen rechazando: en una red de clúster compartida, eso daría confianza a todas las cargas de trabajo vecinas. No lo establezca en un host detrás de un proxy inverso. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Si este nodo ejecuta el bucle de renovación del arrendamiento (lease) y puede convertirse en líder. `false` sigue uniéndose al clúster y consumiendo el bus de eventos; simplemente nunca compite por el arrendamiento. Es adecuado para un nodo que debe recibir eventos del clúster pero nunca debe ostentar el liderazgo. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Duración del arrendamiento del liderazgo. Se renueva aproximadamente a la mitad de este intervalo. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Con qué frecuencia el backend del bus de eventos consulta los mensajes publicados por otros nodos. |

**Los despliegues de varios nodos** incorporan un backend real mediante la función de callback `configureClustering` de `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registran solo el bus de eventos y mantienen el arrendamiento en proceso, para los nodos que deben recibir eventos del clúster pero nunca deben competir por el liderazgo.

Consulte [Escalado](scaling) para ver cómo se comportan el liderazgo y el bus de eventos entre instancias.

## Cabeceras reenviadas (proxy de confianza) {#forwarded-headers-trusted-proxy}

Authagonal usa la IP del cliente como clave para la limitación de frecuencia y el bloqueo de cuentas, y solo emite HSTS en solicitudes HTTPS. Detrás de un proxy inverso o un ingress, la IP y el esquema reales del cliente llegan en las cabeceras `X-Forwarded-For` / `X-Forwarded-Proto`. Estos ajustes controlan **en qué saltos de proxy se confía** para establecer esos valores, de modo que un llamador no pueda falsificar `X-Forwarded-For` para suplantar la IP del cliente.

| Ajuste | Variable de entorno | Predeterminado | Descripción |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Número de saltos de proxy que se respetan desde la derecha de la cadena `X-Forwarded-For`. El valor predeterminado `1` solo confía en el salto que añade su ingress e ignora todo lo que haya más a la izquierda en la cadena. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (matriz) | *(vacío)* | Rangos CIDR (matriz de cadenas, p. ej. `"10.0.0.0/8"`) autorizados a establecer cabeceras reenviadas. Establézcalo en el CIDR de su proxy, ingress o pods. Declararlo es lo que permite que `X-Forwarded-Proto` se respete en absoluto; consulte más abajo. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (matriz) | *(vacío)* | Direcciones IP de proxies individuales (matriz de cadenas) autorizadas a establecer cabeceras reenviadas. Úselo junto con `KnownNetworks` o en su lugar. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Las dos cabeceras no reciben la misma confianza {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` ajusta la **IP del cliente**, la clave de la que dependen la limitación de frecuencia, el bloqueo y la protección de `/_internal`. Sin nada declarado, Authagonal la respeta desde los rangos de bucle invertido y RFC1918 y registra una advertencia. Es un valor predeterminado de mejor esfuerzo, y mejora el comportamiento del marco con un conjunto de confianza vacío, que consiste en respetar la cabecera desde *cualquier* llamador.

`X-Forwarded-Proto` cambia el **esquema**, y el esquema decide si `/connect/*` responde siquiera (RFC 6749 §3.1/§3.2), si las cookies se marcan como `Secure` y si las URL absolutas generadas son https. Se respeta **solo** desde un proxy que usted haya declarado en `KnownNetworks` / `KnownProxies`. Una dirección privada no es una declaración: Authagonal se distribuye como biblioteca y no puede ver la red en la que se desplegó, así que "el par tiene una dirección privada" es una conjetura sobre la topología. En una LAN plana, una VPC compartida o un puente de contenedores compartido, todas las cargas de trabajo vecinas están dentro de esos rangos y podrían afirmar `https` sobre una solicitud que llegó sin cifrar.

**Si su proxy no tiene una dirección fija** (un ingress de Kubernetes, un equilibrador de carga rotativo, una plataforma que no le dice el CIDR del salto), declare como proxy a todo par:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Eso es seguro exactamente cuando nada salvo el proxy puede llegar al proceso, que es la suposición en la que ya se basa un despliegue así. Escribirlo lo deja en un lugar donde se puede revisar, en lugar de dejar que la biblioteca lo deduzca. Si otras cargas de trabajo *pueden* llegar directamente a Kestrel, con este ajuste pueden falsificar el esquema y la IP del cliente, así que fije en su lugar el CIDR real.

### Proxy no declarado: todas las cuotas por origen se comparten {#undeclared-proxy-every-per-source-quota-is-shared}

Los límites de frecuencia basados en la dirección de quien llama (inicio de sesión, registro, contraseña olvidada, registro dinámico de clientes, el ACS de SAML) necesitan saber qué cliente hizo la solicitud. Detrás de un proxy inverso, eso es la IP del cliente reenviada, y la IP del cliente reenviada solo sirve como prueba si usted declaró el proxy que la escribió. Sin nada declarado, Authagonal usa como clave de esas cuotas el par que realmente observa, que detrás de un proxy es el propio proxy: **todos los clientes comparten un mismo presupuesto, y cualquier llamador puede agotarlo para todos** (el predeterminado del inicio de sesión es de 30 intentos cada 5 minutos).

Esto es deliberado y no un error, y no se puede corregir en el servidor. La alternativa, usar como clave el valor reenviado de todos modos, da a un llamador un presupuesto nuevo en cada solicitud con solo variar una cabecera, porque detrás de un equilibrador de carga L4 el salto reenviado situado más a la derecha *es* la propia cabecera de quien llama. En cuál de las dos situaciones se encuentra es exactamente lo que la declaración comunica al servidor, y nada más puede hacerlo. Declare el proxy y las cuotas pasan a ser por cliente.

> ⚠️ **Se necesita un proxy que termine TLS, y debe estar declarado.** Authagonal debe ejecutarse detrás de un proxy inverso que termine TLS (o terminar TLS por sí mismo). HSTS (`Strict-Transport-Security`) solo se emite en solicitudes HTTPS, y los endpoints OAuth rechazan directamente las solicitudes sin cifrar salvo que se establezca `Auth:AllowInsecureHttp`, de modo que el proxy debe reenviar `X-Forwarded-Proto: https` **y** figurar en `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` para que se envíe HSTS y `/connect/*` responda siquiera. No declarar nada es el fallo habitual al actualizar: la cabecera llega, nada tiene derecho a actuar en función de ella y todas las solicitudes a `/connect/*` responden 400 en un despliegue que realmente usa TLS. El registro de inicio lo indica, y también el cuerpo del rechazo.

## Solicitudes salientes (protección contra SSRF) {#outbound-fetches-ssrf-guard}

Authagonal hace solicitudes HTTP iniciadas por el servidor a URL que no eligió: los metadatos SAML o el documento de descubrimiento OIDC de un IdP de origen, el `jwks_uri` de un cliente durante la autenticación `private_key_jwt`, un URI de cierre de sesión por back-channel, un callback de aprovisionamiento. Algunas de esas URL las aporta quien registró un cliente, y una URL que nombra `169.254.169.254` o un host dentro de su clúster es entonces una solicitud que Authagonal hace en nombre de un atacante.

Cada una de esas solicitudes está protegida dos veces. La **comprobación de URL** rechaza los esquemas que no son http(s), las direcciones internas literales y los nombres `localhost` / `.local` / `.internal`, en el momento en que se acepta la URL (una escritura de administración, un registro dinámico de cliente), donde el error se puede atribuir a quien la escribió. La **comprobación de direcciones** se ejecuta en el socket: resuelve el host, rechaza cada dirección devuelta que sea interna y se conecta a una dirección que realmente comprobó en lugar de devolver el nombre al sistema operativo. Esa segunda es la que una comprobación de texto no puede hacer, porque un nombre de host no es un texto con el que el atacante tenga que ser honesto: `logout.attacker.test` supera todas las reglas de sufijos y literales y después responde con la dirección de metadatos de la nube. Como una redirección es una nueva conexión, la comprobación de direcciones se vuelve a ejecutar en cada salto.

Ambas están activadas por defecto y la mayoría de los despliegues nunca las notan. Hay dos cosas que las hacen visibles.

### Llegar a un destino interno a propósito {#reaching-an-internal-destination-on-purpose}

Federar con un IdP al que solo se puede llegar por su red privada, o aprovisionar una aplicación que se ejecuta en el mismo clúster, se rechaza exactamente por la misma regla que detiene el ataque. Nombre esos destinos:

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Forma de la entrada | Permite |
|---|---|
| `idp.corp.internal` | Exactamente ese host y todas las direcciones a las que se resuelve |
| `*.corp.internal` | Cualquier host bajo el sufijo y todas las direcciones a las que se resuelven |
| `10.4.0.0/16`, `fd00:1234::/48` | Esa red, con cualquier nombre |
| `10.4.1.7` | Esa única dirección, con cualquier nombre |

La forma como variable de entorno es `Auth__AllowedInternalTargets__0`, `__1`, etc. Una entrada CIDR mal formada falla al iniciar en lugar de no permitir nada sin aviso.

**Esta lista solo alcanza a las URL que usted proporcionó.** La obtención de los metadatos SAML del IdP de origen, el descubrimiento OIDC del IdP de origen (incluidos el `token_endpoint`, el `userinfo_endpoint` y el `jwks_uri` que nombra ese documento) y los callbacks de aprovisionamiento. Deliberadamente **no** alcanza a un `jwks_uri` registrado por un cliente ni a un URI de cierre de sesión por back-channel, donde un host interno nunca corresponde a un despliegue, de modo que abrir un destino de federación no puede abrir también el servicio de metadatos a una solicitud anónima a `/connect/token`. No hay ningún "desactivar" global.

Tenga en cuenta que https sigue siendo obligatorio en ambas URL de metadatos de federación, independientemente de esta lista. Ese documento contiene las claves y los certificados con los que se valida cada aserción del IdP de origen, y una red privada no es un canal seguro.

> ⚠️ **Hosts multiinquilino: compruebe quién escribe la URL de metadatos antes de añadir nada.** Esta lista se limita a los destinos que *usted* configuró, y en un despliegue de un solo inquilino quien administra las conexiones es usted. Si ejecuta Authagonal para otras personas (un SaaS en el que los administradores de los inquilinos configuran sus propias conexiones SAML/OIDC a través del portal o de la API de administración), `MetadataLocation` lo aporta el **cliente**, y cada entrada que añada aquí queda al alcance de cualquier inquilino que apunte una conexión a ella. Déjela vacía en un host así (el predeterminado) y, si un inquilino necesita realmente un IdP local, dele una vía de salida que termine fuera de su red en lugar de abrir una desde dentro.

### Si su salida a Internet requiere un proxy HTTP {#if-your-egress-requires-an-http-proxy}

La comprobación de direcciones está vinculada a `SocketsHttpHandler.ConnectCallback` y, con un proxy activo, .NET invoca esa función de callback con el endpoint del **proxy** y nunca con el del destino, de modo que la comprobación inspeccionaría el proxy, lo encontraría perfectamente enrutable y lo permitiría todo. Fallaría en modo abierto precisamente en las redes que más probablemente tienen un proxy. Por eso los clientes protegidos establecen `UseProxy = false`, y en una red que solo sale por proxy sus solicitudes fallan.

`Auth:AllowOutboundProxy` vuelve a enviar a través del proxy las solicitudes configuradas por el operador (metadatos SAML, descubrimiento OIDC, callbacks de aprovisionamiento). Para ellas conserva la comprobación de URL y pierde la comprobación de direcciones: un nombre de host que se resuelve a una dirección interna ya no se detecta. **No** alcanza a la obtención del `jwks_uri` del cliente ni a la entrega del cierre de sesión por back-channel: esos destinos los elige el registrante y son accesibles desde solicitudes anónimas, así que no hay ningún interruptor para ellos. Una red que deba pasarlos por un proxy necesita delante una pasarela de salida que filtre SSRF.

`UseAuthagonal()` registra una advertencia al iniciarse cuando encuentra establecido `HTTPS_PROXY`, `HTTP_PROXY` o `ALL_PROXY`, indicando qué clientes lo omiten; de lo contrario, el síntoma es "el SSO dejó de funcionar" sin nada que apunte a la causa.

### Lo que no está protegido {#what-is-not-guarded}

Los clientes salientes del BFF y el envío de correo. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` es su propia configuración, cuyo ejemplo documentado es una dirección interna; el cliente de tokens del BFF se comunica con la autoridad que usted configuró, y el proxy ya rechaza cualquier destino compuesto que salga de la autoridad de origen configurada, de modo que un llamador no puede dirigir esas solicitudes. `Resend` envía a una constante de tiempo de compilación. Los tres usan normalmente el proxy del entorno.

## Limitación de frecuencia {#rate-limiting}

Los límites de frecuencia integrados protegen los endpoints propensos al abuso:

| Endpoint | Límite | Ventana | Clave |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 minutos (`Auth:LoginWindowMinutes`) | Dirección de origen y, por separado, el correo electrónico enviado |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hora (`Auth:RegistrationWindowMinutes`) | IP del cliente |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | Correo electrónico de destino |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | IP del cliente |
| `POST /connect/register` (cuando está habilitado) | 10 | 1 hora | IP del cliente |
| Endpoints SCIM | 200 | 1 minuto | Cliente SCIM |

Por defecto, los límites se imponen **en proceso, por nodo** (detrás del punto de extensión `IRateLimiter`), de modo que con N instancias el límite efectivo es N veces el valor configurado. Trátelos como una red de seguridad e imponga el límite global de referencia en el perímetro (WAF / ingress / CDN). Consulte [Escalado](scaling#rate-limiting).

### Límites para todo el clúster (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

Establezca `Auth:DurableRateLimiting` en `true` para trasladar los contadores al almacén que ya usa el
despliegue, de modo que todas las réplicas compartan un mismo presupuesto y el límite deje de multiplicarse por el número de instancias.

| | en proceso (predeterminado) | duradero |
|---|---|---|
| Límite con N réplicas | N veces el valor configurado | el valor configurado |
| Coste por comprobación | ninguno | un viaje de ida y vuelta al almacén |
| Sobrevive a un reinicio del pod | no | sí |
| Backends | cualquiera | Azure Table, SQL, DynamoDB |

Merece la pena activarlo cuando un presupuesto protege algo que se puede adivinar, sobre todo el `user_code` del flujo de dispositivo, donde
el límite de intentos es lo único que separa a un atacante de un código que concede una sesión activa, y un
presupuesto que crece con el número de réplicas no tiene la forma adecuada. Es menos útil para los límites de volumen, donde el
perímetro es de todos modos el límite de referencia.

Detalles que importan en producción:

- **No es gratis.** Cada comprobación del límite de frecuencia se convierte en un viaje de ida y vuelta al almacén, incluso en las vías de inicio de sesión, de tokens
  y de SCIM. Un despliegue de un solo nodo no gana nada (ahí, por nodo *es* para todo el clúster) y debería
  dejarlo desactivado.
- **Ventanas fijas, así que las ráfagas pueden abarcar un límite de ventana.** Un presupuesto de N es "N por ventana, y hasta 2N
  a ambos lados de un límite", y los presupuestos incluidos tienen ese margen. Esto es lo que permite que el contador sea un único
  incremento atómico en todos los backends, que es la propiedad en la que se basa su corrección.
- **Falla en modo abierto.** Si el almacén no está accesible, la solicitud se permite y se registra un error: el
  limitador protege la vía de inicio de sesión y no debe convertirse en una forma de tumbarla. Mantenga la regla del perímetro.
- **El host no se iniciará** si lo establece sin un proveedor que proporcione `IRateLimitCounterStore`.
  Se niega en lugar de volver silenciosamente a la limitación por nodo que acaba de desactivar.
- **Las filas de los contadores se depuran automáticamente**: DynamoDB mediante TTL nativo, SQL mediante `SqlExpiryReaper`, Azure
  Table mediante un barrido exclusivo del líder (Table Storage no tiene ni TTL ni aritmética en el servidor, así que también es
  el backend en el que un incremento cuesta una lectura más una escritura condicional).

## CORS {#cors}

CORS se configura dinámicamente y **con alcance por ruta**: la anterior descripción de una línea ("los orígenes de todos
los clientes registrados se permiten automáticamente") describía bastante más de lo que hace el proveedor.

- **Los orígenes registrados por clientes** (`AllowedCorsOrigins` en un cliente) solo se respetan bajo `/connect/` y
  `/.well-known/`. **No** abren `/api/auth/`, `/api/v1/` ni `/scim/`. Un cliente deshabilitado no aporta
  nada, y un origen mal formado se descarta.
- **Las credenciales nunca se permiten** bajo `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` ni `/approvals`, para
  ningún origen, ya sea configurado por el operador o registrado por un cliente. Un cliente de navegador que llame a esas rutas con
  `credentials: 'include'` desde otro origen fallará independientemente de la configuración; use un
  backend-for-frontend (consulte el paquete `@authagonal/bff`) en lugar de llamadas de origen cruzado con credenciales.
- Las políticas resueltas se almacenan en caché durante 60 minutos.

Así, un origen añadido a los `AllowedCorsOrigins` de un cliente hace que `/connect/*` funcione y no hace que `/api/v1/*`
funcione. Es deliberado: esas rutas transportan la cookie de sesión y la superficie de administración.

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` se comunica con el motor de secretos Transit de Vault: firma, verificación, cifrado, descifrado y HMAC con clave.
Es la pieza básica para un `IFieldCipher` o un `IIndexTokenizer` respaldado por Vault, que usted mismo registra.

**La firma de JWT no se delega en Vault.** `ProtocolKeyManager` siempre firma con la clave de
`ISigningKeyStore`, y no hay ningún punto de extensión que la sustituya por una clave de Vault. Consulte
[Extensibilidad](extensibility) para ver lo que eso requeriría.

Esto se configura mediante código cuando se aloja como biblioteca.

## Ejemplo completo {#full-example}

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
