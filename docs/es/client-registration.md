---
layout: default
title: Registro dinámico de clientes
locale: es
---

# Registro dinámico de clientes

Authagonal implementa el **registro dinámico de clientes de OAuth 2.0** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), que permite a las aplicaciones cliente registrarse por sí mismas en tiempo de ejecución sin intervención de un administrador.

## Habilitar el endpoint {#enabling-the-endpoint}

El registro dinámico está **deshabilitado por defecto**. Actívelo mediante la configuración:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

O establezca `Auth__DynamicClientRegistrationEnabled=true` como variable de entorno. Un host multiinquilino puede sustituir la opción por inquilino mediante `ITenantContext.DynamicClientRegistrationEnabled`: la respuesta propia del inquilino prevalece y `null` recurre a la opción de todo el host.

Cuando está habilitado, el documento de descubrimiento anuncia el endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Registrar un cliente {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Respuesta {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

El `client_secret` se devuelve **una sola vez** y no se puede recuperar después. Guárdelo de forma segura. La respuesta se envía con `Cache-Control: no-store`. `client_id` tiene 32 caracteres hexadecimales en minúscula, y `client_secret_expires_at` es siempre `0` (los secretos no caducan). Los clientes públicos (`none`) y los clientes `private_key_jwt` no reciben `client_secret` en la respuesta. La respuesta solo repite los campos mostrados: `audiences`, `jwks`, `jwks_uri`, `allowed_cors_origins` y los campos de cierre de sesión se almacenan pero no se devuelven.

## Parámetros de la solicitud {#request-parameters}

| Parámetro | Obligatorio | Notas |
|---|---|---|
| `client_name` | no | Si se omite, toma el `client_id` generado |
| `redirect_uris` | condicional | Obligatorio cuando `grant_types` contiene `authorization_code`. Deben ser URI absolutos; se rechazan los esquemas `javascript:`/`data:`/`vbscript:`/`file:` (los esquemas personalizados nativos para enlaces profundos móviles se aceptan). Se rechaza un fragmento (RFC 6749 §3.1.2), y `http` sin cifrar solo se acepta para hosts de loopback (RFC 8252 §7.3). Como máximo 20 entradas, cada una de 2048 caracteres como máximo. |
| `post_logout_redirect_uris` | no | Destinos de redirección válidos tras el cierre de sesión. Mismos límites de 20 entradas y 2048 caracteres que `redirect_uris`. |
| `grant_types` | no | Por defecto `["authorization_code"]`. **Solo se pueden registrar `authorization_code` y `refresh_token`**: `client_credentials`, `implicit`, el de dispositivo y cualquier otro tipo de concesión se rechazan con `invalid_client_metadata`, de modo que el registro abierto nunca puede crear un cliente de máquina a máquina. `refresh_token` se añade automáticamente si se solicita `offline_access`. |
| `token_endpoint_auth_method` | no | `client_secret_basic` (por defecto), `client_secret_post`, `private_key_jwt` o `none` para clientes públicos. Cualquier otro valor se rechaza con `invalid_client_metadata`. |
| `jwks` / `jwks_uri` | con `private_key_jwt` | Las claves públicas del cliente. Uno de los dos es obligatorio para `private_key_jwt` (de lo contrario, `invalid_client_metadata`); `jwks_uri` debe superar la protección de URL salientes (una dirección externa). A un cliente `private_key_jwt` no se le emite ningún secreto. |
| `scope` | no | Ámbitos separados por espacios. Solo se pueden registrar los cinco integrados de OIDC (`openid`, `profile`, `email`, `phone`, `offline_access`) más los que indique `Auth:DynamicClientRegistrationScopes`: existir en el almacén de ámbitos **no** basta (consulte [Ámbitos](scopes)). Los ámbitos restringidos por rol y el ámbito administrativo (`AdminApi:Scope`, por defecto `authagonal-admin`) nunca se pueden registrar. |
| `audiences` | no | Valores `aud` de JWT que se añaden a los tokens de acceso. Como máximo 20 entradas de 512 caracteres como máximo, cada una un URI absoluto sin fragmento; un valor incorrecto produce `invalid_client_metadata`. |
| `allowed_cors_origins` | no | Cada entrada debe ser un origen válido (de lo contrario, `invalid_client_metadata`), pero el valor **no se almacena tal como se envía**: los orígenes permitidos del cliente se derivan de los orígenes de sus propios `redirect_uris` `https`, de modo que quien registra solo puede llegar a orígenes para los que ya demostró un URI de redirección. |
| `backchannel_logout_uri` | no | Habilita el [cierre de sesión por back-channel (canal de retorno)](index#key-features) |
| `frontchannel_logout_uri` | no | Habilita el [cierre de sesión por front-channel](front-channel-logout) |
| `frontchannel_logout_session_required` | no | Por defecto `true`; cuando es `true`, la URL de cierre de sesión lleva los parámetros `iss` y `sid` |

## Valores por defecto e invariantes {#defaults--invariants}

- **PKCE obligatorio**: `RequirePkce` es siempre `true` para los clientes registrados dinámicamente.
- **Consentimiento obligatorio**: `RequireConsent` es siempre `true`, de modo que un usuario ve la pantalla de consentimiento para un cliente autorregistrado incluso cuando un cliente cargado estáticamente desde la configuración la omitiría.
- **Clientes públicos**: `token_endpoint_auth_method: "none"` produce un cliente sin secreto. PKCE sigue siendo obligatorio.
- **Acceso sin conexión**: solicitar el ámbito `offline_access` añade implícitamente `refresh_token` a `grant_types`.

## Respuestas de error {#error-responses}

| HTTP | `error` | Causa |
|---|---|---|
| `400` | `invalid_redirect_uri` | Uno de los `redirect_uris` no es un URI absoluto válido, usa un pseudoesquema script/data/file, lleva un fragmento, es `http` sin cifrar hacia un host que no es de loopback o (en cualquiera de las dos listas de URI) supera los 2048 caracteres |
| `400` | `invalid_client_metadata` | Se solicitó un tipo de concesión no registrable, falta `redirect_uris` para un tipo de concesión que lo exige, `token_endpoint_auth_method` no es compatible, `private_key_jwt` no tiene `jwks`/`jwks_uri` (o tiene un `jwks_uri` inseguro), `audiences` no es válido, una entrada de `allowed_cors_origins` no es un origen o un URI de cierre de sesión no es una dirección externa |
| `400` | `invalid_scope` | Un ámbito solicitado no es integrado ni está registrado |
| `400` | `invalid_client_metadata` | Más de 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | Un ámbito solicitado no es registrable: no figura en `Auth:DynamicClientRegistrationScopes` o está restringido por rol |
| `403` | `invalid_scope` | Se solicitó el ámbito administrativo, que nunca se puede conceder mediante el registro |
| `403` | `invalid_scope` | Un `IClientScopeGuard` registrado rechazó un ámbito solicitado (se le pasa el llamante anónimo) |
| `403` | `not_supported` | El registro dinámico de clientes no está habilitado |
| `429` | `rate_limited` | Demasiados registros desde esta IP (10 por hora) |

## Consideraciones de seguridad {#security-considerations}

El endpoint de registro **no requiere autenticación**, pero está restringido por diseño:

- **Limitación de frecuencia**: 10 registros por dirección de origen en una hora móvil (`429 rate_limited`), de modo que el almacén de clientes no se puede inundar. La dirección es la que el llamante no puede elegir (no se confía ciegamente en el valor reenviado).
- **Tipos de concesión restringidos**: solo `authorization_code` + `refresh_token`; un cliente registrado siempre exige un flujo mediado por el usuario y nunca puede actuar como cliente de máquina a máquina.
- **Ámbitos en lista de permitidos, no heredados**: quien registra puede declarar los cinco integrados de OIDC y nada más, salvo que un operador incluya un ámbito en `Auth:DynamicClientRegistrationScopes`. Existir en el almacén de ámbitos no equivale a permiso: un ámbito existe porque algún cliente lo necesita, no porque cualquier registrante anónimo pueda reclamarlo.
- **Ámbito de administración reservado**: el ámbito `authagonal-admin` (o el valor que tenga `AdminApi:Scope`) se rechaza, de modo que el registro nunca puede producir un cliente que llegue a la [API de administración](admin-api).
- **URI de cierre de sesión validados**: el servidor accede a `backchannel_logout_uri` y `frontchannel_logout_uri`, por lo que deben ser endpoints http(s) externos: se rechazan los hosts de loopback, RFC1918, link-local (incluida la dirección de metadatos de la nube) y `.internal`/`.local`.
- **Registros acotados**: como máximo 20 URI de redirección de 2048 caracteres como máximo cada uno, de modo que un registro no puede usarse para inflar el almacén de clientes.
- **Orígenes CORS derivados, no de confianza**: los orígenes almacenados provienen de los propios URI de redirección `https` del cliente, nunca del cuerpo de la solicitud.
- **PKCE siempre obligatorio** y **consentimiento siempre obligatorio** en los clientes registrados.

Lo que **no** restringe, salvo que quien registra lo solicite, es la audiencia. RFC 7591 no tiene ningún campo para ella, así que un registro estándar omite por completo `audiences` (una extensión de Authagonal): al cliente nunca se le preguntó, su lista está «sin establecer» y puede nombrar cualquier URI absoluto como su `resource` en el endpoint de autorización y recibir un token que lleve ese valor como `aud`. Es deliberado (la especificación de autorización de MCP exige que los clientes nombren el servidor MCP como recurso, y un cliente MCP es un cliente DCR), y hace que el servidor de recursos sea responsable de autorizar en función de `scope` y no de `iss` + `aud` + `sub`. **Enviar** `audiences`, aunque sea como lista vacía, es una respuesta y fija el cliente a ella: una lista no vacía es la lista de permitidos para `resource`, y un `[]` explícito significa que el cliente no puede nombrar ningún recurso. El intercambio de tokens es la excepción: allí un `Audiences` sin establecer deniega directamente, de modo que un cliente registrado no puede dirigir un token intercambiado a ninguna parte. Consulte [Audiencias e indicadores de recurso](configuration#audiences-and-resource-indicators-rfc-8707).

Para una restricción más fuerte (tokens de acceso iniciales, mTLS, declaraciones de software), anteponga al endpoint su propio middleware o un `IAuthHook`. Considere deshabilitar por completo el registro dinámico y gestionar los clientes mediante la API de administración en los entornos donde el registro de autoservicio no sea un requisito.
