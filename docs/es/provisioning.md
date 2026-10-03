---
layout: default
title: Aprovisionamiento
locale: es
---

# Aprovisionamiento TCC

Authagonal aprovisiona usuarios en las aplicaciones descendentes mediante el patrón **Try-Confirm-Cancel (TCC)**. Esto garantiza que todas las aplicaciones estén de acuerdo antes de que un usuario obtenga acceso, con una reversión limpia si alguna aplicación lo rechaza.

## Cuándo se ejecuta el aprovisionamiento {#when-provisioning-runs}

El aprovisionamiento se ejecuta automáticamente cada vez que se crea un usuario, sea cual sea la vía de creación:

| Endpoint | Desencadenante |
|---|---|
| `POST /api/v1/profile/` | Creación de un usuario por un administrador |
| `POST /api/auth/register` | Registro de autoservicio |
| SAML ACS (`POST /saml/{id}/acs`) | Primer inicio de sesión SSO (usuario nuevo) |
| Callback de OIDC (`GET /oidc/callback`) | Primer inicio de sesión SSO (usuario nuevo) |
| SCIM (`POST /scim/v2/Users`) | Aprovisionamiento desde el proveedor de identidad |
| `GET /connect/authorize` | Primera autorización a través de un cliente con `ProvisioningApps` |

Las combinaciones de aplicación y usuario ya aprovisionadas se omiten (se registran en la tabla `UserProvisions`).

Las vías de creación de usuarios aprovisionan en **todas las aplicaciones configuradas**. El endpoint de autorización aprovisiona solo en la lista `ProvisioningApps` del cliente.

**En caso de rechazo:** si alguna aplicación de aprovisionamiento rechaza al usuario en la fase Try (o falla un callback), el usuario recién creado se elimina. Así se evitan usuarios creados a medias. Lo que ve quien llama depende de la vía:

| Vía | Respuesta |
|---|---|
| Creación por un administrador (`POST /api/v1/profile/`), registro de autoservicio | `422 Unprocessable Entity` con el motivo del rechazo |
| SAML ACS, callback de OIDC | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| Creación por SCIM | `400` de SCIM, `scimType: invalidValue`, con un mensaje fijo (el texto de la aplicación descendente no se devuelve al proveedor de identidad) |
| Confirmación de la toma de posesión de una cuenta sin contraseña | `400 provisioning_rejected` en JSON, o una redirección a `/login?error=provisioning_rejected&error_description=...` para un clic en el navegador (consulte [Actualizar un usuario](user-upgrade)) |
| `GET /connect/authorize` | Redirección de vuelta al cliente con `error=access_denied` |

La solicitud de creación por un administrador acepta `skipProvisioning: true`, para quien llama siendo una parte propia que es en sí misma el destino del aprovisionamiento y no quiere que se vuelva a entrar en su propio callback mientras está a mitad de configurar al usuario. Para ese usuario no se aprovisiona nada ni se llama a ninguna aplicación.

## Configuración {#configuration}

### 1. Definir las aplicaciones de aprovisionamiento {#1-define-provisioning-apps}

En `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` es opcional (60 por defecto). Auméntelo cuando la aplicación descendente realice trabajo real durante Try. Confirm, Cancel y Deprovision usan siempre un tiempo de espera fijo y corto (10 segundos) que no se puede ajustar; siempre deberían ser operaciones ligeras.

La sección de configuración `ProvisioningApps` solo se lee cuando no hay ningún `IProvisioningAppStore` registrado. Los proveedores de Azure Table, AWS y SQL registran uno cada uno, y entonces la biblioteca resuelve las aplicaciones desde el almacén (consulte [Resolución personalizada de aplicaciones](#custom-app-resolution)), así que con un proveedor persistente defina las aplicaciones mediante la API de administración en lugar de en `appsettings.json`.

### 2. Asignar aplicaciones a clientes {#2-assign-apps-to-clients}

Cada cliente declara en qué aplicaciones deben aprovisionarse sus usuarios mediante el campo `provisioningApps` del registro del cliente. Establézcalo a través de la API de administración de clientes (la sección de configuración `Clients` no incluye este campo). Al crear un cliente se vincula el registro completo, y `PUT /api/v1/clients/{clientId}` fusiona los campos que envíe con el cliente almacenado, así que una solicitud que solo lleve `provisioningApps` deja el resto del cliente sin cambios:

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

Cuando un usuario se autoriza a través de `web-app`, se le aprovisiona en `my-backend` si todavía no lo estaba.

## Protocolo TCC {#tcc-protocol}

Authagonal hace tres tipos de llamadas HTTP a su endpoint de aprovisionamiento. Todas usan `POST` con cuerpos JSON y `Authorization: Bearer {ApiKey}`.

### Fase 1: Try {#phase-1-try}

**Solicitud:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Los campos nulos (incluido `customAttributes` cuando el usuario no tiene ninguno) se omiten de la carga.

**Respuestas esperadas:**

| Estado | Cuerpo | Significado |
|---|---|---|
| `200` | `{ "approved": true }` | El usuario se puede aprovisionar. La aplicación crea un registro **pendiente**. |
| `200` | `{ "approved": false, "reason": "..." }` | El usuario se rechaza. No se crea ningún registro. |
| `2xx` | Cuerpo vacío o no interpretable | Se trata como aprobado. |
| Distinto de 2xx | Cualquiera | Se trata como fallo. |

Devuelva un valor `approved` explícito. Una respuesta cuyo cuerpo no se puede leer como JSON se aprueba, así que un endpoint mal configurado que responde `200` con una página HTML aprueba a todos los usuarios.

El `transactionId` identifica este intento de aprovisionamiento. Su aplicación debería guardarlo junto al registro pendiente.

Una respuesta de aprobación también puede devolver `organizationId`, `customAttributes` y `emailVerified`. Authagonal los fusiona en el usuario: `organizationId` solo se aplica si el usuario todavía no tiene uno (las aplicaciones posteriores de la misma transacción ven la asignación anterior), las entradas de `customAttributes` se fusionan clave por clave, y `emailVerified: true` marca como confirmado el correo del usuario (úselo cuando la aplicación descendente ya haya verificado la dirección; el registro de autoservicio omite entonces el correo de verificación). Tanto `organizationId` como los atributos pasan a los tokens (claim `org_id`; los atributos personalizados, mediante la configuración `UserClaims` del ámbito). Los valores fusionados se guardan en el usuario una vez que todas las aplicaciones han confirmado.

### Fase 2: Confirm {#phase-2-confirm}

Solo se llama si **todas** las aplicaciones devolvieron `approved: true` en la fase Try.

**Solicitud:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `2xx` (con cualquier cuerpo). Su aplicación promueve el registro pendiente a confirmado. Una respuesta distinta de 2xx o un tiempo de espera agotado (10 segundos) cuenta como un Confirm fallido.

### Fase 3: Cancel {#phase-3-cancel}

Se llama si el Try de **alguna** aplicación se rechazó o falló, para limpiar las aplicaciones cuyo Try sí tuvo éxito.

**Solicitud:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Respuesta esperada:** `200` (con cualquier cuerpo). Su aplicación elimina el registro pendiente.

Cancel se hace en la medida de lo posible: si falla, Authagonal registra el error y continúa. Como red de seguridad, su aplicación debería **eliminar los registros no confirmados tras un TTL** (p. ej., 1 hora).

## Diagrama de flujo {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### En caso de fallo {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### En caso de fallo parcial de Confirm {#on-partial-confirm-failure}

Si un Confirm falla, Authagonal revierte toda la transacción:

1. Las aplicaciones que aún no se han confirmado reciben `POST {CallbackUrl}/cancel`.
2. Las aplicaciones que ya confirmaron **en esta transacción** se compensan con `DELETE {CallbackUrl}/users/{userId}` (la misma llamada que el [desaprovisionamiento](#deprovisioning)), y se eliminan sus registros de aprovisionamiento. Las aplicaciones en las que el usuario se aprovisionó en una transacción anterior no se tocan.
3. Se genera un error de aprovisionamiento, y la vía que hizo la llamada elimina al usuario recién creado (o, en el caso del endpoint de autorización, responde con un error).

Los registros de aprovisionamiento solo se guardan después de que todos los Confirm hayan tenido éxito, así que un reintento vuelve a intentarlo con todas las aplicaciones. La compensación se hace en la medida de lo posible: un `DELETE` fallido se registra y puede que haya que eliminar a mano la cuenta en la aplicación.

## Resolución personalizada de aplicaciones {#custom-app-resolution}

La biblioteca elige por usted el origen de las aplicaciones:

- Cuando hay un `IProvisioningAppStore` registrado, cosa que hacen todos los proveedores de Azure Table, AWS y SQL, las aplicaciones proceden del almacén (`StoreProvisioningAppProvider`) y se gestionan mediante la API de administración que se describe más abajo.
- En caso contrario, se leen de la sección de configuración `ProvisioningApps` (`ConfigProvisioningAppProvider`).

Registre su propio `IProvisioningAppProvider` antes de `AddAuthagonal` para resolver las aplicaciones de otra manera, por ejemplo por inquilino; el valor por defecto de la biblioteca solo se añade si no hay ninguno registrado:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

El proveedor devuelve una lista de aplicaciones y sus URL de callback. El `TccProvisioningOrchestrator` llama a Try/Confirm/Cancel en cada una.

> **Por defecto, `CallbackUrl` debe ser enrutable públicamente.** Authagonal la valida al escribirla y de nuevo en cada solicitud que hace, y rechaza los destinos de loopback, RFC1918, link-local y `.internal`/`.local` (un callback de aprovisionamiento es una URL que obtiene el servidor). Una aplicación de aprovisionamiento que se ejecuta dentro de su propia red es un despliegue admitido: indíquela en [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard).

### API de administración {#admin-api}

Las aplicaciones guardadas en el almacén se gestionan en `/api/v1/provisioning/apps` (política `IdentityAdmin`; todos los cambios se auditan):

| Ruta | Comportamiento |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. La clave de API nunca se devuelve, solo `hasApiKey`. `limit` es la cuota de aplicaciones, nulo cuando no hay ninguna. |
| `POST /` | Crear. `name` y `callbackUrl` son obligatorios; `apiKey` y `tryTimeoutSeconds` son opcionales. Se genera un `appId` de 12 caracteres. Si se supera la cuota, `400 provisioning_app_limit`. |
| `PUT /{appId}` | Sustituye `name`, `callbackUrl` y `tryTimeoutSeconds` (`name` y `callbackUrl` vuelven a ser obligatorios). Si `apiKey` se omite o es nulo, la clave no cambia; una cadena vacía la borra. `404 app_not_found` para una aplicación desconocida. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Envía a la aplicación un Try con un usuario de prueba fijo (`test-user`, `test@example.com`), con un tiempo de espera de 10 segundos. Devuelve `{ "success", "statusCode", "body" }` (cuerpo truncado a 1000 caracteres). Los fallos de conexión devuelven `success: false, statusCode: 0` en lugar de un estado de error. |

`callbackUrl` debe ser una URL `http` o `https` absoluta en un host externo, como se ha descrito antes. `tryTimeoutSeconds` se limita a un intervalo de 5 a 300 segundos. El `appId` es lo que un cliente indica en `provisioningApps`.

## Desaprovisionamiento {#deprovisioning}

Cuando se elimina un usuario mediante la API de administración (`DELETE /api/v1/profile/{userId}`) o se desaprovisiona mediante SCIM (`DELETE /scim/v2/Users/{id}`, una eliminación lógica que desactiva al usuario), Authagonal llama a `DELETE {CallbackUrl}/users/{userId}` en cada aplicación en la que estaba aprovisionado el usuario, con un tiempo de espera de 10 segundos, y elimina el registro de aprovisionamiento. Se hace en la medida de lo posible: los fallos se registran, pero no bloquean la eliminación. Una aplicación que ya no está configurada se omite con una advertencia.

`ReprovisionAsync` de `IProvisioningOrchestrator` vuelve a ejecutar Try y Confirm en todas las aplicaciones, incluso donde el usuario ya está aprovisionado. La biblioteca lo usa cuando alguien toma posesión de una cuenta sin contraseña (consulte [Actualizar un usuario](user-upgrade)); un simple nuevo inicio de sesión nunca lo hace.

## Implementar los endpoints en su aplicación {#implementing-the-upstream-endpoints}

### Ejemplo mínimo (Node.js/Express) {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
