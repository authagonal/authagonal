---
layout: default
title: Solicitudes de autorización enviadas (PAR)
locale: es
---

# Solicitudes de autorización enviadas (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permite que un cliente envíe por POST los parámetros de su solicitud de autorización directamente al servidor, con la autenticación de cliente estándar, y reciba un `request_uri` opaco de vida corta para entregárselo al navegador. Después, el navegador visita `/connect/authorize?request_uri=...&client_id=...` en lugar de llevar todos los parámetros en la URL.

Por qué usarlo:

- Los parámetros de autorización nunca aparecen en el historial del navegador, en los logs del servidor ni en las cabeceras `Referer`.
- El servidor autentica al cliente en el momento del envío, de modo que la integridad de los parámetros se comprueba antes de que se produzca cualquier redirección.
- Los conjuntos de parámetros largos (solicitudes `claims` grandes, flujos con varios recursos) no superan los límites de longitud de la URL.

## Endpoint {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

La autenticación es la misma que en `/connect/token`: HTTP Basic con `client_id`/`client_secret`, o credenciales codificadas en el formulario. Los clientes confidenciales deben autenticarse; los clientes públicos envían sin secreto. Los fallos de autenticación del cliente devuelven `401` (según RFC 9126, a diferencia del endpoint de token, donde solo `invalid_client` es un 401).

El cuerpo del formulario lleva los mismos parámetros que normalmente irían en `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). El propio `request_uri` se rechaza: el apartado 2.1 de la especificación prohíbe encadenar una PAR. Si el cuerpo lleva un `client_id`, debe coincidir con el cliente autenticado. Al igual que el endpoint de token, la ruta rechaza una solicitud `http` en texto plano salvo que esté establecido `AuthagonalProtocolOptions.AllowInsecureHttp`.

La solicitud se valida en el momento del envío, del mismo modo en que la validaría `/connect/authorize` (`redirect_uri` registrado, ámbitos permitidos, PKCE, valores de `prompt`, etc.). Una solicitud no válida se rechaza de inmediato con `400 invalid_request` y no se emite ningún `request_uri`, de modo que el error aflora al cliente en lugar de al usuario final a mitad del flujo. `authorization_details` se rechaza con `invalid_authorization_details` (las solicitudes de autorización enriquecidas corresponden al endpoint de token, no a este).

### Límites {#limits}

- El cuerpo está limitado a 32 KB, con un máximo de 64 campos de formulario, nombres de 256 caracteres y 8 KB por valor. Todo lo que supere esos tamaños se rechaza con `413 invalid_request`.
- Las solicitudes tienen un límite de frecuencia de 60 por minuto por cliente y dirección de origen, y de 300 por minuto por cliente en total, con respuesta `429 temporarily_unavailable`.

### Respuesta {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

El `request_uri` es de un solo uso. Se elimina del almacén cuando se emite el código de autorización correspondiente. Si nunca se canjea, caduca a los 90 segundos.

### Paso de autorización {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Cuando `request_uri` está presente, todos los demás parámetros se toman de la carga enviada y se ignora cualquier otra cosa que haya en la URL (salvo `client_id`, que debe coincidir con el cliente que envió la carga, y el parámetro `error` que añade un recorrido de federación fallido). Un `request_uri` desconocido, caducado, ya consumido o enviado por otro cliente se rechaza con `invalid_request`. Solo se aceptan los URN opacos emitidos por el propio endpoint PAR de este servidor: cualquier otro valor de `request_uri` se rechaza con `request_uri_not_supported`, y el parámetro `request` de RFC 9101 con `request_not_supported`.

Se respetan los valores de `prompt` y `max_age` enviados. Una solicitud PAR que lleva `prompt=login` (o un `max_age` que la sesión ha superado) solo se satisface con una sesión cuyo `auth_time` sea igual o posterior al momento en que se envió la solicitud, de modo que se cierra una sesión ya existente y se vuelve a autenticar una sola vez, y el regreso desde el inicio de sesión emite un código en lugar de entrar en un bucle.

## Exigir PAR por cliente {#requiring-par-per-client}

Establezca `RequirePushedAuthorizationRequests = true` en un cliente para rechazar sus solicitudes `/connect/authorize` simples. Cualquier intento de autorización sin PAR devuelve `invalid_request` con la descripción "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Es la postura recomendada para los clientes que manejan ámbitos sensibles: combinada con PKCE, elimina la barra de direcciones como superficie de ataque.

## Vida útil y almacenamiento {#lifetime-and-storage}

El `expires_in` que devuelve el envío es de 90 segundos, y esa ventana cubre el salto desde el envío hasta la primera solicitud `/connect/authorize`. En cuanto se recoge el registro por primera vez, se amplía (una sola vez) hasta un plazo absoluto de 15 minutos desde el envío, para que el usuario pueda completar el inicio de sesión, la MFA y el consentimiento. Los valores de 90 segundos y 15 minutos son constantes, no configuración. Las cargas enviadas se guardan mediante el mismo `IGrantStore` que los códigos de autorización y los tokens de actualización, así que heredan automáticamente la estrategia de persistencia y replicación del host.

## Descubrimiento {#discovery}

El endpoint PAR se anuncia en `.well-known/openid-configuration` como:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
