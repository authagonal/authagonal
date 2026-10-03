---
layout: default
title: Ámbitos de OAuth
locale: es
---

# Ámbitos de OAuth

Authagonal admite tanto ámbitos de OAuth/OIDC **integrados** como ámbitos **personalizados** gestionados en tiempo de ejecución. Los ámbitos personalizados se persisten, se anuncian en el documento de descubrimiento y aparecen en la pantalla de consentimiento junto a los integrados.

## Ámbitos integrados {#built-in-scopes}

Estos ámbitos están siempre disponibles y no es necesario registrarlos:

| Ámbito | Finalidad |
|---|---|
| `openid` | Obligatorio para iniciar un flujo OIDC. Emite un token de ID. |
| `profile` | Claims de perfil estándar (name, family_name, given_name, etc.) |
| `email` | Claims de dirección de correo y `email_verified` |
| `phone` | Claims `phone_number` y `phone_number_verified` (OIDC Core 5.4) |
| `roles` | El claim `roles`. No es un ámbito estándar de OIDC: la pertenencia a roles es un claim cuya divulgación consiente el usuario final |
| `groups` | El claim `groups` (pertenencia a grupos SCIM). No es un ámbito estándar de OIDC; se controla igual que `roles` |
| `offline_access` | Emite un token de actualización junto con el token de acceso |

Un cliente solo puede solicitar los ámbitos que figuran en sus propios `AllowedScopes`. `/connect/authorize` rechaza un ámbito ausente de esa lista con `invalid_scope` en lugar de filtrarlo, así que añadir `roles` a la solicitud de una aplicación sin añadirlo al cliente hace fallar todos los inicios de sesión.

## Ámbitos personalizados {#custom-scopes}

Los ámbitos personalizados se gestionan mediante la API de administración en `/api/v1/scopes`. Requieren un token de acceso JWT con el ámbito `authagonal-admin` (configurable mediante `AdminApi:Scope`).

### Modelo de ámbito {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Campo | Descripción |
|---|---|
| `Name` | El identificador del ámbito que se envía en las solicitudes de token (p. ej., `billing.read`) |
| `DisplayName` | Nombre legible que se muestra en la pantalla de consentimiento |
| `Description` | Descripción más extensa que se muestra en la pantalla de consentimiento |
| `Emphasize` | Si es `true`, la pantalla de consentimiento destaca este ámbito como sensible |
| `Group` | Encabezado de la pantalla de consentimiento bajo el que se agrupa este ámbito. Solo afecta a la presentación: nunca cambia lo que se concede |
| `Required` | Si es `true`, el usuario no puede desmarcar este ámbito al dar su consentimiento |
| `ShowInDiscoveryDocument` | Si es `true`, el ámbito aparece en `/.well-known/openid-configuration` dentro de `scopes_supported` |
| `AllowedRoles` | Roles que un usuario debe tener para que se le conceda este ámbito. Vacío (el valor por defecto) lo deja sin restricción; consulte [Ámbitos restringidos por rol](#role-gated-scopes) |
| `UserClaims` | Lista blanca de nombres de claims de atributos personalizados que se incluyen en los tokens cuando se concede este ámbito. Los claims reservados del protocolo (como `org_id`) nunca se incluyen por esta vía, de modo que un atributo almacenado no puede falsificarlos |

### Ámbitos restringidos por rol {#role-gated-scopes}

Los `AllowedScopes` de un cliente responden a *si esta aplicación puede pedir este ámbito*, una cuestión que se decide
antes de que nadie haya iniciado sesión. `AllowedRoles` responde a la otra mitad: *si esta persona puede tenerlo*. Se aplican
ambos controles, y ninguno sustituye al otro.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

A un usuario que no tiene ninguno de los roles indicados se le **retira el ámbito de la concesión**, sin rechazar la solicitud: el
cliente pidió su conjunto completo y se le informa, mediante el `scope` que se devuelve en la respuesta de token (RFC 6749
§3.3), de que ha recibido menos. Esto es lo que permite que una misma aplicación sirva tanto al personal como a todos los demás: la
superficie del personal es un ámbito entre varios, y solo lo reciben quienes tienen derecho a él.

Una solicitud en la que se retiran *todos* los ámbitos solicitados falla con `access_denied`, porque no queda
nada para lo que emitir un token.

El control se aplica en todos los puntos en los que se emite un token para una persona:

| Flujo | Dónde se aplica |
|---|---|
| Código de autorización | En `/connect/authorize`, una vez conocido el usuario y **antes** del consentimiento, de modo que la pantalla nunca ofrece un permiso que no se puede conceder |
| Código de dispositivo | En `/api/auth/device/approve`, el primer punto de ese flujo en el que se conoce al sujeto |
| Actualización | En cada rotación, frente a los roles recién resueltos. Es aquí donde la revocación de un rol surte efecto realmente, ya que la concesión sigue registrando lo que se aprobó al iniciar sesión |
| Intercambio de tokens | Sin control propio: un intercambio solo puede reducir el ámbito dentro de los ámbitos del propio token del sujeto, así que nunca puede alcanzar uno que no se le haya concedido al sujeto |

Las concesiones de credenciales de cliente no tienen sujeto y quedan intactas de forma deliberada: la autoridad de un
cliente de máquina es su registro.

Cargar un ámbito desde la configuración puede añadir o cambiar `AllowedRoles`, pero no vaciarlo (igual que con
`UserClaims`, un campo omitido conserva el valor almacenado). Para quitar un control, haga un `PUT` del ámbito con
un arreglo vacío explícito.

## Carga desde la configuración {#seeding-from-configuration}

Los ámbitos se pueden declarar en la sección de configuración `Scopes`. Se escriben en el almacén de ámbitos al arrancar, junto con la [carga de clientes](configuration#clients).

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

| Campo | Descripción |
|---|---|
| `Name` | Obligatorio. Una entrada sin nombre se omite con una advertencia |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | Como en el [modelo de ámbito](#scope-model) |

La carga desde la configuración es una inserción o actualización (upsert) por `Name`. Un campo que establezca prevalece sobre el valor almacenado en cada arranque, así que una modificación hecha mediante la API de administración en un campo que también se carga desde la configuración se sobrescribe en el siguiente arranque. Un campo que omita conserva lo que haya almacenado (o el valor por defecto del modelo para un ámbito nuevo). Como omitir significa «conservar», la configuración puede añadir o cambiar `UserClaims` y `AllowedRoles`, pero no vaciarlos: para ello, use `PUT /api/v1/scopes/{name}` con un arreglo vacío explícito.

## Endpoints de administración {#admin-endpoints}

### Listar ámbitos {#list-scopes}

```
GET /api/v1/scopes
```

Devuelve `{ "scopes": [ ... ] }`.

### Obtener un ámbito {#get-scope}

```
GET /api/v1/scopes/{name}
```

Devuelve el ámbito, o `404` si no se encuentra.

### Crear un ámbito {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Devuelve `201 Created` con el ámbito. Devuelve `400` (`invalid_request`) si falta `name` o contiene espacios en blanco, y `409` (`scope_exists`) si ya existe un ámbito con el mismo nombre.

### Actualizar un ámbito {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Solo se actualizan los campos proporcionados; los omitidos conservan su valor actual.

### Eliminar un ámbito {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

Devuelve `204 No Content` (`404` si el ámbito no existe). Los tokens ya emitidos que incluyen este ámbito siguen siendo válidos hasta que caducan; revóquelos explícitamente mediante `/connect/revocation` si es necesario.

## Documento de descubrimiento {#discovery-document}

Los ámbitos con `ShowInDiscoveryDocument = true` aparecen dentro de `scopes_supported` en `/.well-known/openid-configuration`. Los siete ámbitos integrados se anuncian siempre.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Pantalla de consentimiento {#consent-screen}

Cuando un cliente solicita un ámbito que no está en su lista de omisión del consentimiento, la página de consentimiento muestra cada ámbito solicitado por su `DisplayName` (o, en su defecto, por su `Name`) con la `Description` debajo. Los ámbitos con `Emphasize = true` reciben un tratamiento visual diferenciado. Los ámbitos `Required` no se pueden desmarcar.

Consulte [Pantalla de consentimiento de OAuth](index#key-features) para ver el flujo desde el punto de vista del usuario.

## Registro dinámico de clientes {#dynamic-client-registration}

Los clientes registrados mediante el [registro dinámico de clientes](client-registration) solo pueden declarar los ámbitos integrados de OIDC (`openid`, `profile`, `email`, `phone`, `offline_access`) y cualquier ámbito indicado en `Auth:DynamicClientRegistrationScopes`. Que un ámbito exista en el almacén no autoriza a un cliente autorregistrado a declararlo, y los ámbitos restringidos por rol (los que tienen `AllowedRoles`) nunca se pueden registrar. Cualquier otro se rechaza con `invalid_scope`.
