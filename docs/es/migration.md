---
layout: default
title: Migración
locale: es
---

# Migración desde Duende IdentityServer

El paquete `Authagonal.Migration` realiza una migración única desde Duende IdentityServer + SQL
Server a los almacenes de Authagonal. El mismo motor está disponible de dos maneras:

- **Ejecutor alojado** (recomendado): un servicio en segundo plano dentro de su host de Authagonal que ejecuta la
  migración una vez durante el despliegue, condicionado al liderazgo del clúster, sin bloquear el arranque.
- **CLI**: `tools/Authagonal.Migration.Cli`, para ejecuciones locales o sin conexión contra un destino de Table Storage.

SqlClient solo reside en este paquete, de modo que los hosts que no migran nunca lo heredan.

## Ejecutor alojado {#hosted-runner}

Añádalo después de `AddAuthagonal` (depende de los almacenes, del proveedor de secretos y del liderazgo del clúster):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

La segunda llamada `Map` es obligatoria e independiente: este paquete hace referencia a `Authagonal.Server`, así que
`MapAuthagonalEndpoints` no puede alcanzarlo. Sin ella, `GET /admin/migration/status` responde 404,
algo indistinguible de que la política `IdentityAdmin` le deniegue el acceso, y la ejecución registra una advertencia
al arrancar que lo indica.

Configúrelo mediante la sección `Migration`:

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

El ejecutor:

1. Espera `StartupDelaySeconds` (los servicios de carga desde la configuración terminan primero; el arranque nunca se bloquea).
2. Se omite si ya existe un marcador `Completed` que no sea `DryRun` para `Version`.
3. Espera hasta `LeaseWaitMinutes` a convertirse en líder del clúster (solo un pod ejecuta la migración).
4. Escribe un marcador `Started`, ejecuta el motor y después un marcador `Completed`/`Failed` con el informe.

Perder el liderazgo a mitad de la ejecución cancela el motor; el nuevo líder vuelve a ejecutarla, lo cual es seguro
porque cada pasada es idempotente. Consulte el progreso en `GET /admin/migration/status` (protegido por la política
`IdentityAdmin`).

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(Sin separador `--` después del nombre de la imagen). O desde el código fuente:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## Qué se migra {#what-gets-migrated}

| Origen (SQL Server) | Destino | Notas |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Usuarios + índices de correo y nombre | Los id se conservan literalmente. Incorporación de claims: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (también las variantes xmlsoap); los claims de correo se descartan; todo lo demás → atributos personalizados. Los hashes de contraseña nulos (usuarios que solo usan SSO externo) no son un problema. Los hashes BCrypt / ASP.NET Identity V3 se verifican sin cambios y se actualizan a PBKDF2 nativo en el siguiente inicio de sesión. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = se omite (idempotente) |
| `AspNetRoles` + `AspNetUserRoles` | Roles + vínculos de rol de usuario | Un mapa de id de rol→nombre resuelve las asignaciones de los usuarios |
| `ApiScopes` + `IdentityResources` | Ámbitos | Los nombres existentes (cargados desde la configuración) se omiten; se copian los claims de los ámbitos |
| `Clients` de Duende + tablas hijas | Clientes | Secretos etiquetados como `SHA256$`/`SHA512$` según la longitud del resumen (los demás se descartan con una advertencia); los secretos caducados se omiten; los clientes cargados desde la configuración prevalecen (se omiten) |
| `ApiResources` de Duende | (aplanado) | Audiencias → clientes creados por la migración; claims de recursos → ámbitos creados por la migración |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | El CSV de `AllowedDomains` se divide en registros de dominio SSO |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | La misma división de dominios |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | Secreto TOTP base32→protegido (`duende-totp`); códigos de recuperación con hash (`duende-rc-{n}`); se omite el usuario si ya tiene MFA |
| `PersistedGrants` de Duende (tokens de actualización) | Concesiones | **No es posible con Duende estándar**; consulte más abajo. Requiere `MigrateRefreshTokens` *y* `SourceGrantKeysAreUnhashed`; de lo contrario se omite con una advertencia y los usuarios vuelven a iniciar sesión. |

## Opciones {#options}

| Opción | Valor por defecto | Descripción |
|---|---|---|
| `Enabled` | `false` | Interruptor general del ejecutor alojado |
| `DryRun` | `false` | Recorre el origen y genera el informe de validación completo (juego de caracteres y longitud de los id, correos duplicados, inventario de tablas y columnas, recuentos por pasada) sin escribir nada |
| `Version` | `"1"` | Marcador de ejecución. Increméntelo para volver a ejecutar un barrido de diferencias. Solo un marcador `Completed` que no sea `DryRun` impide volver a ejecutar |
| `UsersMode` | `CreateOnly` | `CreateOnly` omite los usuarios existentes; `Upsert` los sobrescribe. **Nunca use `Upsert` después de la transición**: machaca las contraseñas ya rehasheadas y la MFA nueva |
| `MigrateClients` | `true` | Migra los clientes OAuth. Los clientes cargados desde la configuración siempre prevalecen y los clientes existentes se omiten |
| `MigrateRefreshTokens` | `false` | Incluye los tokens de actualización activos. Requiere `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Afirma que `PersistedGrants.Key` del origen contiene los identificadores literalmente. Solo es cierto para un fork con un almacén de concesiones personalizado |
| `Source:ConnectionString` | *(ninguno)* | Conexión al SQL Server de Duende de origen |
| `MaxDegreeOfParallelism` | `32` | Concurrencia de escritura acotada para las pasadas de gran volumen (usuarios, inicios de sesión externos, MFA, tokens de actualización). Redúzcala para cuentas pequeñas o propensas a la limitación; `1` es totalmente secuencial |
| `LeaseWaitMinutes` | `10` | Ejecutor alojado: deja de esperar el liderazgo del clúster pasado este tiempo; un reinicio posterior lo vuelve a intentar |
| `StartupDelaySeconds` | `30` | Ejecutor alojado: retardo antes de empezar, para que los servicios de carga desde la configuración terminen y el arranque no se bloquee |

## Idempotencia y barridos de diferencias {#idempotency--delta-sweeps}

Cada pasada es idempotente (se omite si ya existe, id de MFA deterministas), así que es seguro volver a ejecutar la
migración. Ejecútela días antes de la transición y después incremente `Version` para un barrido final de diferencias
cerca de la transición, que recoja los usuarios registrados desde entonces. Los registros existentes se omiten (o se
actualizan con `Upsert`), nunca se duplican.

## Qué NO se migra {#what-is-not-migrated}

- **Los tokens de actualización en vigor, con Duende estándar.** El `DefaultGrantStore` de Duende nunca persiste el
  identificador de un token de actualización: `PersistedGrants.Key` contiene `base64(SHA-256(handle + ":" + grantType))`,
  y el identificador presentado se vuelve a someter a hash en la búsqueda. Por tanto, el identificador no se puede
  recuperar de la base de datos de origen, y las filas migradas serían imposibles de canjear para siempre, lo que es
  peor que no migrarlas, porque el informe las cuenta como creadas y el fallo solo aflora en la primera renovación de
  token después de la transición. Planifique la transición asumiendo un nuevo inicio de sesión, o ejecute una capa de
  lectura dual durante la ventana de transición. `SourceGrantKeysAreUnhashed` solo existe para un fork cuyo almacén de
  concesiones persista los identificadores literalmente, y ese fork también se encarga de traducir
  `PersistedGrants.Data` de la forma `RefreshToken` de Duende a `RefreshTokenData`.
- **Tokens y grupos SCIM**, **aprovisionamientos de usuarios**: no tienen equivalente en Duende; empiezan vacíos.
- **Claves de firma**: no está automatizado. Para mantener válidos los tokens existentes tras la transición, exporte la
  clave de firma RSA de Duende e impórtela en la tabla `SigningKeys` cerca de la transición.

## Estrategia de transición {#cutover-strategy}

1. Despliegue en modo inactivo (`Enabled=false`).
2. `Enabled=true, DryRun=true` → reinicie → revise el informe en `/admin/migration/status`.
3. `DryRun=false` → reinicie → verifique que el marcador sea `Completed` y compruebe algunos inicios de sesión.
4. Incremente `Version` para el barrido final de diferencias y después apunte los clientes y BFF a Authagonal.
   **Cuente con un nuevo inicio de sesión forzado**; consulte más arriba.
5. Supervise; la reversión consiste en volver a apuntar al despliegue de Duende, que queda intacto.

## Importación de usuarios desde NDJSON {#ndjson-user-import}

Una segunda fuente de importación independiente en el mismo paquete `Authagonal.Migration`: un archivo NDJSON plano
(un objeto JSON por línea) en lugar de una conexión a una base de datos en vivo, y solo usuarios, sin clientes, roles,
ámbitos ni configuración de federación. Está pensada para migrar la tabla de usuarios propia de una aplicación heredada
(un almacén de ASP.NET Identity hecho a mano, una tabla de Rails/Devise exportada a bcrypt, una aplicación Node con
scrypt, ...) de modo que las personas sigan iniciando sesión con su contraseña antigua mientras esta se rehashea de
forma transparente a PBKDF2 nativo en su siguiente inicio de sesión correcto, la misma vía de rehash diferido en la
que se apoya el importador de Duende descrito arriba.

### Esquema del registro {#record-schema}

Un objeto JSON por línea. `email` es el único campo obligatorio; todos los demás son opcionales. **Los campos de nivel
superior desconocidos hacen fallar esa línea** (estricto por defecto) salvo que se pase `--AllowUnknownFields true`.

| Campo | Tipo | Notas |
|---|---|---|
| `email` | string | Obligatorio. Debe ser una dirección de correo plausible. Clave de duplicados sin distinguir mayúsculas y minúsculas. |
| `username` | string | Sin columna propia en `AuthUser`; se guarda en `CustomAttributes["username"]`. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | Sin columna propia; se guarda en `CustomAttributes["displayName"]`. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Si falta, toma `false`. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, almacenado **literalmente**. Cualquier formato que `PasswordHasher` reconozca al iniciar sesión (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`) se verifica sin cambios y a partir de ahí se actualiza a PBKDF2 nativo. No se inspecciona más allá de comprobar que no esté vacío; un hash mal formado simplemente no se verifica al iniciar sesión, igual que ocurriría fuera de la migración. Omítalo para usuarios que solo usan SSO o que no usan contraseña. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | Se combina en `AuthUser.CustomAttributes` |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Si falta, el usuario queda activo. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Si falta, toma la hora de la importación. |
| `externalId` | string | → `AuthUser.ExternalId`, el mismo campo que el importador de Duende ocupa con el id de usuario de la base de datos de origen. |

Archivo de ejemplo (5 líneas):

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI {#cli-1}

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

El mismo destino (Azure Table Storage) y la misma salvaguarda de PII en texto plano que la CLI de Duende anterior:
esta fuente escribe filas `AuthUser` directamente en Table Storage sin ningún `IFieldCipher`/`IIndexTokenizer`
registrado por el host, así que se niega a ejecutarse salvo que `--AllowPlaintextPii true` confirme que el destino no
tiene ninguno de los dos configurado (o que, en su lugar, conecte `NdjsonUserImportEngine` al contenedor de DI del
propio host, donde esos puntos de extensión sí se resuelven). A diferencia de la CLI de Duende, no hay salvaguarda
`--AllowPlaintextSecrets`: esta fuente nunca escribe semillas TOTP de MFA ni secretos de clientes OAuth, solo campos
del perfil del usuario y un hash de contraseña almacenado literalmente.

### Opciones {#options-1}

| Opción | Valor por defecto | Descripción |
|---|---|---|
| `--Input` | *(obligatorio)* | Ruta al archivo NDJSON |
| `--Target:ConnectionString` | *(obligatorio)* | Cadena de conexión de Azure Table Storage |
| `--DryRun` | `false` | Analiza y valida cada línea, resuelve los duplicados frente al destino y genera el informe completo, sin escribir nada |
| `--OnDuplicate` | `skip` | Cómo tratar una línea cuyo correo (sin distinguir mayúsculas y minúsculas) ya coincide con un usuario existente: `skip` (lo deja intacto, idempotente), `update` (combina los campos presentes en la línea sobre el usuario existente) o `fail` (aborta la ejecución sin más) |
| `--BatchSize` | `500` | Cada cuántas líneas se escribe una línea de registro de progreso. No es un mecanismo de escritura por lotes: `IUserStore` no tiene API masiva, así que cada importación o actualización sigue siendo una llamada al almacén |
| `--AllowUnknownFields` | `false` | Acepta e ignora las propiedades JSON de nivel superior ajenas al esquema anterior, en lugar de hacer fallar la línea |
| `--ContinueOnError` | `false` | Sale con 0 aunque una o más líneas no hayan podido analizarse o validarse. No se aplica a `--OnDuplicate fail`, que siempre aborta la ejecución sea cual sea esta opción |

### Salida del resumen y códigos de salida {#summary-output--exit-codes}

El informe se imprime como JSON: `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed` y los primeros
20 `Failures` (`LineNumber` + `Reason`). Las líneas en blanco no se cuentan en ninguna parte. Códigos de salida:

- `0`: éxito (o `--ContinueOnError true` con una o más líneas fallidas)
- `1`: una o más líneas no pudieron analizarse o validarse, y no se estableció `--ContinueOnError`
- `2`: la ejecución se abortó: `--OnDuplicate fail` encontró un correo existente, o faltaba una opción obligatoria

### Idempotencia {#idempotency}

El valor por defecto `--OnDuplicate skip` hace que volver a ejecutar con un archivo sin cambios no haga nada la
segunda vez: toda línea cuyo correo ya existe se cuenta como omitida y no se escribe nada. `update` también se puede
volver a ejecutar con seguridad (siempre vuelve a aplicar los mismos campos); `fail` es para una importación única
que nunca debe colisionar en silencio con cuentas existentes.

### Qué NO se importa {#what-is-not-imported}

- **Roles, ámbitos, clientes OAuth, configuración de federación.** Esta fuente es solo de usuarios: consulte el
  importador de Duende descrito arriba si también los necesita.
- **Credenciales de MFA, inicios de sesión externos.** No forman parte del esquema; añádalos mediante los flujos
  estándar de configuración inicial de MFA y de SSO después de la importación.
