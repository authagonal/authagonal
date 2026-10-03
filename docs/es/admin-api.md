---
layout: default
title: API de administración
locale: es
---

# API de administración

Los endpoints de administración requieren un token de acceso JWT con el ámbito `authagonal-admin` (configurable mediante `AdminApi:Scope`).

Todos los endpoints están bajo `/api/v1/`.

## Obtener el primer token de administración {#bootstrapping-the-first-admin-token}

Todos los endpoints `/api/v1/*` exigen un token de portador que lleve el ámbito de administración, pero la propia API de administración (y el [registro dinámico de clientes](client-registration)) **se niega a crear o actualizar cualquier cliente que tenga ese ámbito** (`403 forbidden_scope`), de modo que un cliente creado en tiempo de ejecución nunca puede escalar a administrador. La única forma de emitir un token de administración es un **cliente cargado desde la configuración**: `ClientSeedService` inserta o actualiza al iniciar las entradas de la sección de configuración `Clients:`, y la configuración es de confianza; la protección de ámbito prohibido solo se aplica a las API de tiempo de ejecución.

Cargue desde la configuración un cliente `client_credentials` con el ámbito de administración en `appsettings.json` (o en las variables de entorno / el almacén de secretos equivalentes):

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` se convierte en hash al iniciar; proporcione `SecretHashes` en su lugar si prefiere guardar en la configuración solo un valor ya convertido en hash. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` se aceptan como alias de `Id`/`Name`/`GrantTypes`/`Scopes`.)

Después, canjee las credenciales por un token en el endpoint de token estándar:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

La concesión `client_credentials` valida el ámbito solicitado contra los `AllowedScopes` del cliente; como el cliente cargado desde la configuración tiene `authagonal-admin`, se emite el token. Úselo como `Authorization: Bearer {access_token}` en cada llamada de administración:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Guarde el secreto del cliente cargado desde la configuración en el almacén de secretos de su despliegue; rotarlo consiste en un cambio de configuración + un reinicio.

## Usuarios {#users}

### Obtener un usuario {#get-user}

```
GET /api/v1/profile/{userId}
```

Devuelve el perfil y lo que una consola de soporte necesita para diagnosticar un problema de inicio de sesión:
`emailConfirmed`, `isActive`, `lockoutEnd`, `accessFailedCount`, `roles`, los
`externalLogins` vinculados y `hasPassword` (solo si existe, nunca el hash). Este último marca la diferencia
entre "ha olvidado su contraseña" y "nunca ha tenido una, inicia sesión con SSO",
que son consejos opuestos.

Devuelve los detalles del usuario, incluidos los vínculos con inicios de sesión externos.

### Existencia de un usuario {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

Devuelve `204` si el usuario existe y `404` en caso contrario (una comprobación de existencia barata, sin cuerpo).

### Registrar un usuario {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crea un usuario y envía un correo de verificación. Devuelve `409 user_exists` si el correo electrónico ya está en uso.

Campos opcionales exclusivos de administración: `userId` (id proporcionado por quien llama; `409 user_id_in_use` en caso de colisión), `emailConfirmed` (crea el usuario ya verificado, sin enviar el correo de verificación), `companyName`, `organizationId`, `phone`, `locale` y `customAttributes` (un mapa de cadenas que se persiste en el usuario y se reenvía a los destinos de aprovisionamiento).

`skipProvisioning: true` crea la identidad sin ejecutar el aprovisionamiento. Está pensado para una aplicación
propia que es ELLA MISMA un destino de aprovisionamiento y ya está a mitad de configurar a este usuario: llama
aquí para emitir la identidad, no para recibir una llamada de vuelta sobre un usuario que está en plena
creación. Sin esta opción, esa aplicación recibe su propio Try para un usuario a medio construir, que solo lleva los
atributos que sobrevivieron al viaje de ida y vuelta y, si se recupera, acaba aprovisionando al usuario dos veces.

### Actualizar un usuario {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` es obligatorio; todos los demás campos son opcionales y solo se actualizan los que se proporcionan.

`isActive` desactiva o reactiva la cuenta. `emailConfirmed` (que también se acepta como `emailVerified`)
marca la dirección como confirmada sin enviar un correo de verificación, para cuando la posesión se ha
demostrado de otro modo.

Cambiar `organizationId`, o desactivar la cuenta, provoca:
- La rotación del SecurityStamp (invalida todas las sesiones de cookie en un plazo de 30 minutos)
- La revocación de todos los tokens de actualización

Un bloqueo que solo surte efecto en el siguiente inicio de sesión no es un bloqueo; por eso la desactivación revoca
en lugar de esperar a la caducidad.

### Buscar usuarios {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

Búsqueda por prefijo sobre los índices de correo electrónico y de nombre. Devuelve `{ "users": [ ... ] }`.

### Obtener un usuario por correo electrónico {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

Búsqueda exacta, distinta de la búsqueda general, que es por prefijo y puede devolver varias personas. Quien
traduce "esta dirección" a "esta cuenta" quiere una respuesta o ninguna. `404` si no existe ese usuario.

### Listar usuarios {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

Listado del directorio paginado por cursor; devuelva el `continuationToken` recibido para obtener la página siguiente, y
deténgase cuando sea null. Se usan cursores en lugar de desplazamientos porque el almacén pagina por token: un desplazamiento
volvería a recorrer desde el principio en cada página.

### Qué usuarios existen {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Devuelve el subconjunto que existe, además de `truncated: true` cuando la solicitud superó el límite de 500 ids, de modo que
se informa a quien llama de que su lote se recortó, en lugar de responderle sin aviso sobre 500 de 600. Sirve para
conciliar un conjunto de ids con el de otro sistema.

### Estado de MFA de muchos usuarios {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Devuelve `{ "statuses": { "a": true, "b": false }, "truncated": false }`: `true` significa que el usuario tiene al menos una credencial de MFA. Limitado a 500 ids; `truncated: true` indica que la solicitud se recortó. Sirve para las insignias de "usa MFA" en una vista de directorio.

### Establecer una contraseña {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

La vía de soporte para alguien que ha perdido el acceso a una cuenta cuya dirección ya no le llega. Está sujeta
a la política de contraseñas. Revoca todos los tokens de actualización y rota el security stamp: un cambio de
contraseña que deja en marcha las sesiones antiguas no ha cambiado quién puede actuar como esa persona.

### Desbloquear un usuario {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

Elimina el bloqueo y su recuento de intentos fallidos, de modo que la persona puede volver a entrar ahora y no cuando
el bloqueo caduque.

### Eliminar un usuario {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

Elimina el usuario, revoca todas las concesiones y lo desaprovisiona de todas las aplicaciones descendentes (en la medida de lo posible).

### Confirmar el correo electrónico {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar el correo de verificación {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Vincular una identidad externa {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Desvincular una identidad externa {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestión de MFA {#mfa-management}

### Obtener el estado de MFA {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

Devuelve el estado de MFA y los métodos registrados de un usuario.

### Restablecer toda la MFA {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

Elimina todas las credenciales de MFA y establece `MfaEnabled=false`. El usuario tendrá que volver a registrarlas si es necesario.

### Eliminar una credencial de MFA concreta {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Elimina una credencial de MFA concreta (por ejemplo, un autenticador perdido). Si se elimina el último método principal, la MFA se desactiva.

## Proveedores de SSO {#sso-providers}

### Proveedores SAML {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

La creación requiere `connectionName`, `entityId` y **exactamente uno de** `metadataLocation` (una URL de metadatos) o `metadataXml` (metadatos del IdP pegados, para IdP sin URL de metadatos; se validan sintácticamente y se condensan al guardar). Opcionales: `nameIdFormat` (omítalo para el valor predeterminado emailAddress, `"none"` para omitir NameIDPolicy, recomendado para ADFS, o una URN de formato NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`, `organizationId`. Cada conexión recibe un par de claves de SP generado por el servidor, que la API nunca devuelve. Consulte [SAML](saml) para más detalles.

`organizationId` limita la conexión a una [organización](organizations): solo se ofrece cuando esa organización está seleccionada, sus `allowedDomains` solo se comparan dentro de ella (y *no* se escriben en el índice de dominios de SSO de todo el inquilino), y todo el que inicia sesión a través de ella se convierte en miembro de ella. Omitido o `null` = una conexión de nivel de inquilino. Una organización que no existe produce `400 unknown_organization`. En una actualización, `null` (campo ausente) deja el alcance como está, `""` devuelve la conexión al nivel de inquilino, y cualquiera de los dos sentidos reescribe el índice de dominios en consecuencia. Consulte [Conexiones limitadas a una organización](self-service-sso#organisation-scoped-connections).

### Proveedores OIDC {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

La creación requiere `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Opcionales: `iconUrl`, `allowedDomains`, `passthroughParams`, `organizationId` (con el mismo significado que en una conexión SAML, arriba). El secreto del cliente está protegido en reposo y nunca se devuelve. Consulte [Federación OIDC](oidc-federation).

### Dominios de SSO {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## Clientes {#clients}

Gestione clientes OAuth en tiempo de ejecución. Todas las rutas requieren la política `IdentityAdmin` (el ámbito de administración).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Crear / actualizar un cliente {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` devuelve `409` si el cliente ya existe. `PUT` actualiza un cliente existente (`404` si no se encuentra); en una actualización, solo se comprueba la escalada de privilegios de los ámbitos recién añadidos.

Notas:

- **Los hashes de los secretos nunca se devuelven.** `clientSecretHashes` se elimina de todas las respuestas (listado, obtención, creación, actualización). En una actualización, omitir `clientSecretHashes` conserva el secreto almacenado; proporcionar hashes nuevos lo rota.
- **El ámbito de administración no se puede conceder a un cliente.** Solicitar `AdminApi:Scope` (por defecto `authagonal-admin`) en `allowedScopes` devuelve `403 forbidden_scope`; ningún cliente puede tener el ámbito de administración, porque de lo contrario un cliente `client_credentials` podría emitir tokens de administración indefinidamente.
- Añadir ámbitos que quien llama no tiene permiso para conceder devuelve `403`.

## Ámbitos {#scopes}

Gestione ámbitos de OAuth personalizados en tiempo de ejecución. Consulte [Ámbitos de OAuth](scopes) para ver el modelo de ámbitos completo.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Devuelve `201` al crear (`409` si el ámbito ya existe), el JSON del ámbito al obtener o actualizar, y `204` al eliminar.

## Aplicaciones de aprovisionamiento {#provisioning-apps}

Gestione en tiempo de ejecución las aplicaciones descendentes que reciben el aprovisionamiento. Todas las rutas requieren la política `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Crear / actualizar una aplicación de aprovisionamiento {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` y `callbackUrl` son obligatorios; `callbackUrl` debe ser una URL `http(s)` absoluta.
- `tryTimeoutSeconds` se limita al rango 5–300.
- **La clave de API nunca se devuelve.** Las respuestas exponen `hasApiKey` (un booleano) en lugar de la propia clave. En una actualización, omitir `apiKey` la deja sin cambios, una cadena vacía la borra y un valor la sustituye.
- La creación está sujeta a una cuota configurable por despliegue (`IProvisioningAppQuota`); superarla devuelve `400 provisioning_app_limit`. La respuesta del listado incluye el `limit` actual.

### Probar una aplicación de aprovisionamiento {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envía un `POST {callbackUrl}/try` sintético con una carga de ejemplo (y la clave de API de la aplicación como token de portador, si está establecida) y devuelve `{ success, statusCode, body }` para que pueda verificar la conectividad desde la interfaz de administración.

## Roles {#roles}

### Listar roles {#list-roles}

```
GET /api/v1/roles
```

### Obtener un rol {#get-role}

```
GET /api/v1/roles/{roleId}
```

### Crear un rol {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Actualizar un rol {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Eliminar un rol {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### Asignar un rol a un usuario {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

La asignación se hace por **nombre de rol**, no por id de rol. Devuelve la lista de roles actualizada del usuario.

### Quitar un rol a un usuario {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Obtener los roles de un usuario {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### Usuarios de un rol {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

La operación inversa de la anterior (quién tiene este rol), que se responde a partir de un índice de pertenencia a roles en lugar
de leer todos los usuarios. Devuelve `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`; cada miembro lleva su conjunto completo de roles, porque una consola que lista un
rol casi siempre quiere mostrar qué otros roles tienen sus miembros.

`404 role_not_found` para un rol que no existe, en lugar de una lista vacía: "nadie tiene este rol"
y "ha escrito mal el rol" son problemas distintos. `501 not_supported` si el almacén configurado
no indexa la pertenencia a roles, por el mismo motivo: una lista de miembros vacía se leería como
"nadie administra esto".

Las cuentas escritas antes de que existiera el índice son invisibles para él hasta que se reindexan
(`IUserStore.ReindexUserAsync`, que inserta o actualiza las pertenencias de un usuario sin eliminar ninguna).

## Tokens de SCIM {#scim-tokens}

### Generar un token {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` y `expiresInDays` son opcionales (omita `expiresInDays` para un token que no caduca). Devuelve el token en bruto una sola vez. Guárdelo de forma segura: no se puede volver a recuperar.

### Listar tokens {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

Devuelve los metadatos del token (ID, fecha de creación) sin el valor del token en bruto.

### Revocar un token {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens {#tokens}

### Suplantar a un usuario {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emite tokens (de acceso, de actualización y, cuando se solicita `openid`, id token) en nombre de un usuario sin necesidad de sus credenciales. Útil para pruebas y soporte. Los parámetros se pasan como cadenas de consulta.

| Parámetro de consulta | Obligatorio | Descripción |
|---|---|---|
| `clientId` | Sí | El cliente para el que se emiten los tokens. La duración de los tokens procede de la configuración de este cliente. |
| `userId` | Sí | El usuario que se suplanta. |
| `scopes` | No | Lista de ámbitos **separados por espacios** (codifique los espacios en la URL). Si se omite, se usan los `AllowedScopes` del cliente. |

Restricciones:

- Los ámbitos se limitan a los `AllowedScopes` del cliente; solicitar cualquier ámbito que el propio cliente no podría solicitar devuelve `400 invalid_scope`.
- El ámbito de administración (`AdminApi:Scope`, por defecto `authagonal-admin`) **no** puede emitirse a través de este endpoint; solicitarlo devuelve `403 forbidden_scope`. Esto impide que un token de administración (posiblemente de duración limitada) emita un token de acceso o de actualización de administración de larga duración.

La respuesta es una respuesta de token estándar con `access_token`, `refresh_token`, `id_token` opcional, `expires_in` y el `scope` concedido (separado por espacios).
