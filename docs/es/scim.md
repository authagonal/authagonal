---
layout: default
title: Aprovisionamiento SCIM 2.0
nav_order: 13
locale: es
---

# Aprovisionamiento SCIM 2.0

Authagonal admite SCIM 2.0 (System for Cross-domain Identity Management) para el aprovisionamiento automatizado de usuarios desde proveedores de identidad empresariales como Microsoft Entra ID, Okta y OneLogin.

## Descripción general {#overview}

SCIM es un protocolo de aprovisionamiento entrante: su proveedor de identidad envía a Authagonal los cambios de usuarios y grupos. Esto complementa el aprovisionamiento saliente TCC (Try-Confirm-Cancel) ya existente, que envía usuarios a las aplicaciones descendentes.

**Operaciones admitidas:**
- CRUD de usuarios (creación, lectura, actualización y eliminación mediante desactivación lógica)
- CRUD de grupos con gestión de miembros
- Filtrado (operadores `eq` y `co` sobre `userName`, `externalId`, `displayName`)
- Paginación: basada en cursor (`cursor`/`nextCursor`) tanto en usuarios como en grupos; `startIndex` se sigue aceptando en grupos para los clientes existentes, pero no se anuncia
- PATCH para actualizaciones parciales (incluida la desactivación con `active=false`)
- Asignación de grupos a roles resuelta al emitir el token

**No admitido:** operaciones masivas, ordenación, ETags, gestión de contraseñas mediante SCIM.

Todos los recursos están limitados al cliente SCIM que los aprovisionó: un usuario o grupo creado por el cliente de un token SCIM es invisible (404) para cualquier otro cliente SCIM.

## Generar un token SCIM {#generating-a-scim-token}

Los endpoints de SCIM se autentican con tokens Bearer estáticos. Genere los tokens mediante la API de administración:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

La respuesta incluye el token en bruto **una sola vez**. Se almacena como hash SHA-256 y no se puede recuperar después, así que guárdelo de forma segura:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Omita `expiresInDays` (o pase `0`) para obtener un token que no caduca.

### Etiquetar con una organización a los usuarios de un conector {#tagging-a-connectors-users-with-an-organization}

`organizationId` es opcional. Cuando se establece, cada usuario aprovisionado mediante ese token se escribe con ese
`OrganizationId`, que se emite como el claim `org_id` en sus tokens. SCIM no ofrece a un conector ninguna forma
de indicar cuál de sus clientes está sincronizando: el SCIM básico no define ningún atributo de organización, y la
extensión empresarial no está implementada (consulte *Compatibilidad de esquemas* más abajo). Vincularlo a la credencial
resuelve la cuestión sin necesidad de un cliente OAuth por cada cliente.

Si lo omite, los usuarios quedan sin etiquetar, que es como se comportaban todos los tokens antes de que esto existiera. Nunca se
deriva nada del id del cliente.

Dos reglas:

- **Solo en la creación.** Una sincronización posterior mediante un token con otra etiqueta no vuelve a etiquetar una cuenta existente.
- **Va antes del aprovisionamiento.** Una respuesta `/try` de TCC solo rellena una organización que todavía esté vacía
  (consulte [Aprovisionamiento](provisioning.md)), por lo que prevalece una vinculación explícita de la credencial, y la carga de `/try`
  lleva el valor vinculado para que una aplicación descendente pueda ver de qué cliente procede la sincronización.

Cuando `organizationId` designa una [organización](organizations) existente, la creación también escribe una pertenencia `active` a ella (sin roles) y registra en la auditoría `scim.organization_member_added`, de modo que después la barrera de pertenencia de la organización no le niega un token al usuario. Un id que no designa ninguna organización se queda en una simple etiqueta `org_id`, que es lo que hacen los tokens emitidos antes de que existieran las organizaciones. Al igual que la etiqueta, la pertenencia solo se escribe en la creación.

> **Etiquetar no es aislar.** La propiedad se aplica por **cliente**, no por token. Dos tokens emitidos
> para el mismo cliente son una identidad con dos secretos, y cada uno puede leer, renombrar, desactivar y
> eliminar lo que haya creado el otro. Eso no es un problema cuando una sola parte los tiene todos. Si conectores
> que no confían entre sí tienen cada uno el suyo, asigne a cada uno su propio cliente.

### Limitar qué identidades puede crear un conector {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` es el único control sobre **qué** usuarios puede aprovisionar una credencial SCIM. Establézcalo.

Si lo omite, obtiene un token sin restricciones, y sin restricciones es más amplio de lo que parece. Un usuario creado por SCIM se
escribe con `EmailConfirmed = true` (la dirección se considera demostrada desde ese momento), de modo que un
conector sin restricciones puede crear `ceo@some-other-company.example` como una cuenta preverificada. Cuando el
propietario real inicia sesión más tarde mediante federación, un registro sin inicios de sesión externos existentes se adopta en lugar de
rechazarse, por lo que su inicio de sesión queda vinculado a esa cuenta; y como `ScimProvisionedByClientId` sigue designando al
conector que la creó, ese conector conserva la propiedad completa del objeto: puede leer el perfil, renombrar
el `userName`, desactivarlo (lo que revoca todas las concesiones) o eliminarlo, lo que purga las passkeys del usuario y sus
pertenencias a grupos y marca la fila con un tombstone, de modo que el conector legítimo de ese dominio recibe 404 en todas las
operaciones.

Un token que omite el campo registra una advertencia en el momento de emitirse, con el id del token.

Proporcione dominios sin más (`acme.example`, no `@acme.example`, y no una dirección). Un valor que nunca podría coincidir se
rechaza en lugar de almacenarse, porque un límite que no permite nada se ve exactamente igual que un conector mal configurado.

Los operadores también pueden establecer un límite en la configuración:

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

Los dos se **intersecan**, y una lista vacía en cualquiera de las dos fuentes significa "esta fuente no impone ningún límite". Así, si
ambas están vacías, no hay restricción; si solo hay una, se aplica por sí sola; y cuando ambas están establecidas, solo se
permiten los dominios presentes en las dos: emitir un token puede reducir el límite configurado por un operador, pero nunca ampliarlo.

Se aplica por igual en la creación, en `PUT` y en `PATCH`, de modo que un cambio de nombre no puede trasladar una cuenta a un dominio que la credencial
no tiene permitido aprovisionar.

### Listar tokens {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Revocar un token {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurar su proveedor de identidad {#configuring-your-identity-provider}

### URL del inquilino {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### Autenticación {#authentication}

Use **OAuth Bearer Token** con el token generado anteriormente.

### Microsoft Entra ID {#microsoft-entra-id}

1. En Azure portal, vaya a **Enterprise Applications** > su aplicación > **Provisioning**
2. Establezca Provisioning Mode en **Automatic**
3. Introduzca Tenant URL: `https://your-instance/scim/v2`
4. Introduzca Secret Token: el token en bruto del paso de generación
5. Haga clic en **Test Connection** para comprobar la conexión
6. Configure las asignaciones de atributos (consulte más abajo)

### Okta {#okta}

1. En la consola de administración de Okta, vaya a **Applications** > su aplicación > **Provisioning**
2. Habilite **SCIM connector**
3. Establezca Base URL: `https://your-instance/scim/v2`
4. Establezca Authentication Mode: **HTTP Header**
5. Introduzca el token Bearer

### OneLogin {#onelogin}

1. En la administración de OneLogin, vaya a **Applications** > su aplicación > **Provisioning**
2. Habilite el aprovisionamiento
3. Establezca SCIM Base URL: `https://your-instance/scim/v2`
4. Establezca SCIM Bearer Token

## Endpoints de SCIM {#scim-endpoints}

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Listar/filtrar usuarios |
| GET | `/scim/v2/Users/{id}` | Obtener un usuario |
| POST | `/scim/v2/Users` | Crear un usuario |
| PUT | `/scim/v2/Users/{id}` | Reemplazar un usuario |
| PATCH | `/scim/v2/Users/{id}` | Actualización parcial |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (desactiva; un GET posterior devuelve 404) |
| GET | `/scim/v2/Groups` | Listar/filtrar grupos |
| GET | `/scim/v2/Groups/{id}` | Obtener un grupo |
| POST | `/scim/v2/Groups` | Crear un grupo |
| PUT | `/scim/v2/Groups/{id}` | Reemplazar un grupo |
| PATCH | `/scim/v2/Groups/{id}` | Añadir/quitar miembros |
| DELETE | `/scim/v2/Groups/{id}` | Eliminar un grupo |
| GET | `/scim/v2/ServiceProviderConfig` | Capacidades |
| GET | `/scim/v2/Schemas` | Definiciones de esquema |
| GET | `/scim/v2/ResourceTypes` | Tipos de recurso |

Cada endpoint también está asignado sin el segmento `/v2` (por ejemplo, `/scim/Users`) para los proveedores de identidad que añaden su propia ruta. Los endpoints de descubrimiento (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, y las URL base `/scim/` y `/scim/v2/` sin más, que devuelven el ServiceProviderConfig) son anónimos; todo lo demás requiere un token Bearer de SCIM.

Los endpoints de usuarios y grupos tienen un límite de 200 solicitudes por minuto por cliente SCIM; las solicitudes que lo superan reciben un error SCIM con el estado `429`.

## Asignación de atributos {#attribute-mapping}

### Atributos de usuario {#user-attributes}

| Atributo SCIM | Campo de Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (o, en su defecto, `locale`) | `Locale` |

### Atributos de grupo {#group-attributes}

| Atributo SCIM | Campo de Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### Compatibilidad de esquemas {#schema-support}

Solo `User` y `Group` del núcleo de SCIM 2.0 (RFC 7643). Las tablas anteriores son el conjunto completo admitido.

La **extensión de usuario empresarial no está implementada**, por lo que `employeeNumber`, `costCenter`, `organization`,
`division`, `department` y `manager` se aceptan y se ignoran en lugar de almacenarse, por igual en la creación, el reemplazo y
PATCH. Entra y Okta asignan varios de ellos en sus asignaciones de atributos predeterminadas, por lo que no es necesario quitarlos
de un conector estándar. (Antes de 0.27.0, un PATCH que llevara uno se rechazaba ENTERO con
`400 invalidPath`, lo que hacía fallar todas las sincronizaciones incrementales mientras las creaciones funcionaban, y podía dejar bloqueado un
desaprovisionamiento `active: false` detrás de un atributo sin relación.)

La flexibilización es limitada: una ruta BÁSICA mal escrita, como `name.givenNam`, sigue respondiendo `400`, y
los atributos de solo lectura (`id`, `meta`, `groups`) mantienen su rechazo por `mutability`.

Tenga en cuenta que el atributo empresarial `organization` **no** se convierte en el `org_id` del usuario. Ese valor lo
afirma el propio proveedor de identidad del cliente, mientras que la vinculación de la credencial descrita arriba la establece el
operador; use en su lugar `organizationId` en el token.

## Detalles de comportamiento {#behavior-details}

### Creación de usuarios {#user-creation}
- Los usuarios aprovisionados por SCIM se crean con `EmailConfirmed = true` (solo SSO, sin contraseña).
- El campo `ScimProvisionedByClientId` registra qué cliente SCIM creó al usuario.
- Si el cliente tiene `ProvisioningApps` configurado, el aprovisionamiento TCC se desencadena automáticamente. Si el aprovisionamiento rechaza al usuario, la creación SCIM se revierte y la respuesta es un `400` de SCIM con `scimType: invalidValue` y un mensaje fijo (el texto propio de la aplicación descendente no se reenvía deliberadamente al cliente SCIM).
- Crear un usuario cuyo `userName` o `externalId` ya existe devuelve un conflicto `409` de SCIM. Los cambios de correo electrónico mediante PUT o PATCH se comprueban del mismo modo.

### Desactivación de usuarios {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` marca el recurso con un **tombstone**: desactiva al usuario, conserva el registro local y establece `ScimDeletedAt`. Un `GET /scim/v2/Users/{id}` posterior devuelve **404**, como exige RFC 7644 §3.6 ("the service provider MUST return a 404 for all operations associated with the previously deleted resource"). No confirme un desaprovisionamiento volviendo a leer el recurso y esperando `active: false`. La lectura devuelve 404, y eso es el éxito.
- El registro se conserva en lugar de borrarse para que se pueda volver a crear a una persona recontratada: el tombstone libera el `userName`/`externalId` que necesita un recurso nuevo, mientras que la cuenta local, su historial de auditoría y sus pertenencias a grupos se conservan.
- `PATCH` con `active = false` también desactiva al usuario.
- Los usuarios desactivados no pueden iniciar sesión mediante contraseña, SAML ni OIDC.
- Todas las concesiones (tokens de actualización, sesiones) se revocan al desactivar.
- El desaprovisionamiento de las aplicaciones descendentes solo lo desencadena `DELETE`; una desactivación mediante `PATCH` revoca las concesiones pero no toca las aplicaciones descendentes.

### Filtrado {#filtering}
Se admite la gramática de filtros completa de RFC 7644 §3.4.2.2.

**Operadores:** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le` y `pr` (presencia).
**Lógicos:** `and`, `or`, `not (...)`, con agrupación entre paréntesis. `and` tiene mayor precedencia que `or`.
**Rutas:** subatributos (`name.givenName`), atributos multivalor (`emails.value`), rutas de valor (`emails[type eq "work"].value`) y nombres con prefijo URN (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

La semántica sigue la RFC: la comparación de cadenas no distingue mayúsculas de minúsculas, un atributo multivalor coincide cuando coincide cualquiera de sus elementos, y un atributo ausente hace falsa cualquier comparación excepto `ne`. Una entrada que no es un filtro SCIM válido se rechaza con `400` y `scimType: invalidFilter`, indicando el problema.

**Rendimiento.** `userName eq` y `externalId eq` (las búsquedas que Entra y Okta emiten antes de cada creación o actualización) se resuelven mediante búsquedas puntuales indexadas en lugar de un recorrido del listado, por lo que siguen siendo rápidas con cualquier número de usuarios. Cualquier otro filtro se evalúa, de forma acotada, mientras se paginan los usuarios del cliente: los datos personales de los usuarios están cifrados en reposo y solo se pueden buscar mediante índices ciegos, por lo que los predicados más complejos no se pueden delegar al almacenamiento. Con la paginación por cursor, `totalResults` se **omite** mientras `nextCursor` está presente, y es el total exacto cuando `nextCursor` está ausente. Consulte Paginación.

### Paginación {#pagination}
Los listados de usuarios usan **paginación por cursor**. Cada página de `GET /scim/v2/Users` devuelve una propiedad `nextCursor` en la respuesta del listado; devuélvala como `?cursor=` para obtener la página siguiente. Cuando `nextCursor` está ausente, el listado está completo. El tamaño de página se controla con `count` (100 por defecto, 200 como máximo).

Solicitar un `startIndex` mayor que 1 en el endpoint Users devuelve un error `400` que le remite a la paginación por cursor; no se ofrece la paginación por desplazamiento más allá de la primera página. `totalResults` se **omite por completo** mientras `nextCursor` está presente, y solo lleva el total exacto en la última página. Deliberadamente no informa del tamaño de la página devuelta: un cliente de sincronización que leía `totalResults`, veía que era igual al número de recursos que acababa de recibir y concluía que tenía todo el directorio, leía de menos el inquilino sin darse cuenta. Controle el bucle con `nextCursor`, nunca con `totalResults`, y trate un `totalResults` ausente como "todavía desconocido", no como cero.

**Los listados de grupos también se paginan por cursor.** `GET /scim/v2/Groups` devuelve un `nextCursor` tanto en su forma filtrada
como en la no filtrada; sígalo del mismo modo. `startIndex` se sigue aceptando en Groups para los clientes que ya lo
usan, pero **no se anuncia** en `ServiceProviderConfig` y no se debe depender de él: `pagination.index` es una
afirmación sobre el proveedor, no sobre una colección concreta, y `/Users` no lo admite, de modo que el único valor
cierto en todas partes es `false`. Use cursores, que funcionan en ambas.

Un listado de grupos filtrado recorre ventanas acotadas en lugar de materializar todo el inquilino, por lo que puede devolver
una página vacía aunque todavía haya coincidencias más adelante. Cuando eso ocurre, devuelve un `nextCursor` y **omite**
`totalResults`: una página vacía con cursor significa "siga", y una página vacía sin cursor significa que el
conjunto filtrado está realmente vacío. No trate la primera página vacía como el final de la colección.

`count=0` devuelve `totalResults` sin recursos (RFC 7644 §3.4.2.4) en ambas colecciones, y un `count`
negativo se rechaza con un `400` en lugar de ajustarse.

### Pertenencia a grupos mediante PATCH {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` acepta las formas de pertenencia que envían realmente los principales proveedores de identidad:

- **Añadir miembros:** `op: "add"` con `path: "members"` y una matriz de valores de objetos `{ "value": "user-id" }`. Los duplicados se ignoran.
- **Reemplazar miembros:** `op: "replace"` con `path: "members"` sustituye toda la pertenencia por la matriz proporcionada.
- **Quitar un miembro concreto (matriz de valores):** `op: "remove"` con `path: "members"` y una matriz de valores con los ids de los miembros que se quitan (la forma que envía Entra ID).
- **Quitar un miembro concreto (filtro de ruta):** `op: "remove"` con `path: 'members[value eq "user-id"]'`, con el id en el filtro de ruta y sin valor (la forma que envía Okta para desaprovisionar).
- **Quitar todos los miembros:** `op: "remove"` con `path: "members"` y sin valor vacía el grupo.

### Asignación de grupos a roles {#group-to-role-mapping}
La pertenencia a un grupo SCIM puede conceder roles de aplicación. Las asignaciones son una fila por cada par (grupo, rol), y un grupo puede conceder varios roles. Se resuelven al **emitir el token**: los roles efectivos de un usuario son sus roles asignados directamente más los roles de cada grupo con asignación de roles al que pertenece, de modo que añadir o quitar un miembro de un grupo surte efecto en el siguiente token sin tocar el registro del usuario. Un almacén de asignaciones vacío no tiene ningún efecto.

Las asignaciones se persisten mediante `IScimGroupRoleMappingStore` (implementado por los proveedores de almacenamiento de Azure y AWS; en caso contrario se registra una implementación predeterminada en memoria) y se gestionan desde la superficie de administración de la aplicación host, no mediante la propia API de SCIM.

Opcionalmente, un cliente con `IncludeGroupsInTokens` habilitado también recibe los nombres para mostrar de los grupos SCIM del usuario como claim `groups` en los tokens emitidos.

## Limitaciones conocidas {#known-limitations}

- **Sin operaciones masivas:** los usuarios y grupos deben aprovisionarse de uno en uno.
- **Sin ordenación:** los listados de usuarios devuelven el orden del almacenamiento con la paginación por cursor; los listados de grupos se ordenan por fecha de creación.
- **Sin gestión de contraseñas:** los usuarios aprovisionados por SCIM solo se autentican mediante SSO.
- **Tombstone, no borrado:** `DELETE` desactiva el recurso y lo marca con un tombstone (un `GET` posterior devuelve 404, según RFC 7644 §3.6) en lugar de eliminar permanentemente el registro local del usuario. Para el borrado, use la API de administración.
