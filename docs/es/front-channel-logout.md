---
layout: default
title: Cierre de sesión por front-channel
locale: es
---

# Cierre de sesión por front-channel

Authagonal implementa **OpenID Connect Front-Channel Logout 1.0**, un mecanismo de cierre de sesión conducido por el navegador que complementa el [cierre de sesión por back-channel (canal de retorno)](index#key-features). Mientras que el cierre de sesión por back-channel es un POST de servidor a servidor, el cierre de sesión por front-channel carga la URL de cierre de sesión de cada parte de confianza (RP) en un iframe oculto, de modo que la sesión de navegador de cada aplicación (cookies, almacenamiento local) se limpia desde dentro del navegador del usuario.

## Cuándo usar cada uno {#when-to-use-which}

| Aspecto | Back-channel | Front-channel |
|---|---|---|
| Sesiones del lado del servidor | ✅ | ❌ |
| Cookies / almacenamiento local del navegador | ❌ | ✅ |
| Funciona cuando el navegador del usuario está desconectado | ✅ | ❌ |
| Resiste errores de red (reintento) | ✅ | ❌ (un único intento, en la medida de lo posible) |

A la mayoría de las aplicaciones les conviene configurar **ambos**. El back-channel garantiza que se avisa al servidor; el front-channel limpia el navegador.

## Configuración del cliente {#client-configuration}

Añada un URI de cierre de sesión por front-channel al registro `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Campo | Descripción |
|---|---|
| `FrontChannelLogoutUri` | El endpoint de cierre de sesión del cliente visible para el navegador |
| `FrontChannelLogoutSessionRequired` | Si es `true` (por defecto), la URL se llama con los parámetros de consulta `iss` y `sid` para que el cliente pueda asociar el cierre de sesión con la sesión concreta |

## Cómo funciona {#how-it-works}

Cuando el navegador visita `/connect/endsession` (GET o POST):

1. **Confirmación (protección CSRF).** Si el navegador tiene una sesión iniciada y la solicitud no lleva un `id_token_hint` cuyo `sub` coincida con esa sesión, el servidor muestra primero una página de «¿cerrar sesión?» con un botón de confirmación en lugar de cerrar la sesión del usuario. El botón envía un POST de vuelta con un token de vida corta (15 minutos) ligado a esa sesión. Esto es lo que impide que una página de terceros termine la sesión de un usuario navegando hasta el endpoint (la cookie de sesión es `SameSite=Lax`, así que acompaña a un GET de nivel superior entre sitios). Un `id_token_hint` coincidente sustituye a la confirmación.
2. El servidor localiza todos los clientes con los que el usuario tiene concesiones actualmente.
3. Para cada cliente con un `FrontChannelLogoutUri` que supera la comprobación de URL salientes (se permite el loopback porque la solicitud la hace el propio navegador del usuario, pero no las direcciones de rangos privados ni las link-local), el servidor construye una URL y añade `iss=<issuer>` (y `sid=<session_id>`, cuando la sesión tiene uno) si `FrontChannelLogoutSessionRequired` es `true`.
4. El servidor revoca las concesiones emitidas para la sesión, cierra la sesión del usuario en la cookie del servidor de autorización, lanza en segundo plano las notificaciones de cierre de sesión por back-channel y, cuando se ha construido al menos una URL de front-channel, devuelve una página HTML que contiene un `<iframe>` oculto para cada una:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   La página lleva una `Content-Security-Policy` cuyo `frame-src` se limita a los orígenes de esas URL, y ningún script.
5. El destino posterior al cierre de sesión se resuelve de la misma manera haya o no iframes de por medio. El `post_logout_redirect_uri` solo se respeta cuando la solicitud identifica al cliente (mediante la audiencia del `id_token_hint` o el parámetro `client_id`) y el URI figura en los `PostLogoutRedirectUris` registrados de ese cliente (se añade un parámetro `state`, si se proporciona). Con iframes, la página espera 2 segundos (un `meta refresh`) y después redirige, o muestra un mensaje de «sesión cerrada» cuando no hay ningún destino válido. Sin URL de front-channel, el servidor redirige de inmediato (`302`), o responde `200` con un `message` JSON cuando no hay ningún destino válido.

`id_token_hint` solo se acepta si es un token de ID que este servidor firmó (ES256, `typ: JWT`) con una única audiencia. Se aceptan los tokens caducados. Los tokens de acceso y los tokens de cierre de sesión se rechazan como sugerencias. Si se envían `client_id` e `id_token_hint` y nombran clientes distintos, la solicitud falla con `400 invalid_request`.

El endpoint JSON `POST /api/auth/logout` (que usa el botón de cierre de sesión de la aplicación de inicio de sesión) ejecuta los mismos pasos de revocación y notificación. No genera iframes: devuelve las URL en `frontchannel_logout_uris` para que quien llama las cargue (consulte la [API de autenticación](auth-api#logout)).

## Manejador de cierre de sesión en el cliente {#client-side-logout-handler}

Cada parte de confianza debe implementar la URL a la que hace referencia `FrontChannelLogoutUri`. Un manejador mínimo:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verifique que `iss` coincide con el servidor de autorización esperado.
2. Si se proporciona `sid`, confirme que coincide con el ID de sesión de la cookie de sesión.
3. Limpie la sesión local (cookies, sesión del lado del servidor, almacenamiento de la SPA).
4. Responda con `200 OK` y un cuerpo vacío (o una página mínima); el usuario nunca ve la respuesta.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Documento de descubrimiento {#discovery-document}

El cierre de sesión por front-channel se anuncia en `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Registro dinámico de clientes {#dynamic-client-registration}

Los clientes registrados mediante el [registro dinámico de clientes](client-registration) pueden incluir:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

El registro rechaza un URI de cierre de sesión que no sea una dirección externa (los nombres de loopback, link-local, de rangos privados y `.localhost`/`.local`/`.internal` se rechazan con `invalid_client_metadata`).

## Limitaciones {#limitations}

- **En la medida de lo posible**: los iframes se cargan una sola vez. Si un error de red o una extensión del navegador los bloquea, no hay reintento. Combínelo con el cierre de sesión por back-channel para obtener fiabilidad.
- **Cookies de terceros**: algunos navegadores bloquean por defecto las cookies en iframes entre sitios. Si su RP depende de cookies de origen propio, confirme que el manejador de cierre de sesión no depende de que se envíen cookies.
- **Tiempo de espera**: la página espera unos 2 segundos antes de redirigir. Es posible que los manejadores de cierre de sesión de RP pesados no terminen a tiempo.

## Relacionado {#related}

- [Registro dinámico de clientes](client-registration): parámetros de front-channel en la solicitud de registro
- [Ámbitos de OAuth](scopes): el consentimiento basado en ámbitos complementa el flujo de cierre de sesión
