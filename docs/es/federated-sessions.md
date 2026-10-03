---
layout: default
title: Sesiones federadas
locale: es
---

# Mantener las sesiones federadas sincronizadas con el IdP de origen

Cuando un usuario inicia sesión a través de un [IdP externo](oidc-federation), Authagonal emite su
*propia* sesión y sus propios tokens. Por defecto, esa sesión local sigue después su propio curso: si el cliente
deshabilita al usuario en su directorio, o se revoca en origen el enlace compartido del invitado, la sesión local de
Authagonal sigue funcionando hasta que caduca su cookie.

Para que las bajas y las revocaciones surtan efecto rápidamente, active **`RevalidateOnRefresh`**. Así, en cada
renovación de token local, Authagonal canjea el token de actualización del IdP de origen ante ese IdP y, si el IdP de
origen indica que la credencial ya no existe, se rechaza la renovación local y la sesión deja de recibir tokens nuevos
en el plazo de una vida útil del token de acceso.

`RevalidateOnRefresh` es una opción **exclusiva de las conexiones OIDC**. SAML no tiene ningún token de actualización
que canjear, así que una conexión SAML no puede revalidar; en su lugar, acote esas sesiones con la caducidad de sesión
de la propia aserción (consulte [SAML](saml)).

## Habilitarlo {#enable-it}

Por conexión (cargada desde la configuración como se muestra abajo, o establecida al crear la conexión mediante la
[API de administración](admin-api)), y el IdP de origen debe emitir realmente un token de actualización:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

Esa es toda la configuración. Con la opción activada, Authagonal añade `offline_access` al ámbito que solicita al IdP
de origen (si la solicitud descendente no lo llevaba ya), guarda el token de actualización que devuelve el IdP de
origen y lo canjea de servidor a servidor en cada renovación local. El token de actualización del IdP de origen
**nunca** se emite a un cliente. Se guarda cifrado en un almacén duradero por sesión, inicializado en el inicio de
sesión con una caducidad fija de siete días (el tope absoluto de la sesión), y solo se usa para la revalidación.

El IdP de origen tiene que colaborar: si el registro de su aplicación nunca recibe `offline_access` (por ejemplo,
porque no se concedió el consentimiento), no se devuelve ningún token de actualización y no hay nada que canjear.
Authagonal registra una advertencia
(`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`) en cada renovación en
ese estado, y el IdP de origen **no** se vuelve a consultar.

## Qué ocurre en la renovación {#what-happens-on-refresh}

1. La RP renueva un token de Authagonal como de costumbre (`grant_type=refresh_token` en `/connect/token`).
2. Authagonal canjea el token de actualización del IdP de origen en el endpoint de token del IdP:
   - **Éxito** → la renovación local continúa; si el IdP de origen rotó su token, el nuevo se almacena y lo
     comparten todas las concesiones de RP de la sesión.
   - **`invalid_grant`** → la credencial federada ya no existe (usuario deshabilitado, sesión revocada, token
     caducado). La renovación local se **rechaza**: la RP recibe `invalid_grant` de `/connect/token` y el token del
     IdP de origen almacenado se elimina. El rechazo solo hace fallar esa solicitud; no revoca la concesión de
     Authagonal, de modo que el token de actualización de la RP queda sin consumir y se sigue rechazando mientras el
     IdP de origen siga revocado.
   - **Cualquier otro 4xx** (p. ej., `invalid_client` por un secreto rotado o mal configurado, o un 429), un 5xx,
     un cuerpo de error que no se puede analizar, un fallo de transporte o una conexión que no se puede cargar
     (eliminada, fallo de descubrimiento o del secreto) → se trata como **transitorio**: la sesión sobrevive para
     que un error del operador no cierre en masa la sesión de todos los usuarios federados. Corrija la
     configuración; no se pierde nada. La sesión sigue acotada por el tope absoluto de la sesión.

Como hay **un solo** token del IdP de origen por sesión de navegador (con clave usuario + conexión + sesión), una
segunda RP que el usuario abra lee y rota el *mismo* token: así, la renovación de una aplicación no puede dejar a otra
con una copia inservible.

## Nada que implementar {#nothing-to-implement}

Aquí no hay ninguna interfaz que escribir. El almacén duradero
(`IUpstreamRefreshTokenStore`) lo registran automáticamente los proveedores de almacenamiento de Azure, AWS y SQL, y
el canje es interno. Usted solo activa `RevalidateOnRefresh` en las conexiones cuyo IdP de origen gestiona una
credencial revocable.

El token almacenado se elimina cuando la sesión termina, sea cual sea la vía que llegue a ella: el cierre de sesión,
la revocación de una sesión desde la página de la cuenta, «cerrar sesión en todas partes» y la limpieza por
caducidad. Si un host no registra ningún almacén, se recurre a la copia que lleva la cookie de sesión.

Toda sesión federada por OIDC también registra qué conexión es su propietaria (el claim `upstream_connection_id`),
tanto si esa conexión revalida como si no. Es solo un registro contable: no se canjea nada para una conexión que no
tenga la opción activada.

> **Alcance:** esta función está activa allí donde se registra el almacén (los proveedores de Azure Table, DynamoDB y
> SQL). Habilítela en conexiones a un IdP de origen **de confianza**, en particular uno que rote sus tokens de
> actualización de un solo uso (Entra, Auth0), y combínela con `IsExternalConnection` para los IdP de terceros
> (consulte [SSO de autoservicio](self-service-sso)).

## Complementario: un tope estricto de sesión {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` mantiene la sesión fiel a las revocaciones en el IdP de origen. Si, en cambio (o además), desea
que la sesión local nunca *sobreviva* a la sesión afirmada por el IdP de origen, establezca `SessionExpClaim` con el
nombre de un claim del id_token que lleve una caducidad (segundos Unix). Authagonal limita a ese tope la sesión local
y todos los tokens emitidos a partir de ella (incluidas las rotaciones de renovación y una concesión de código de
dispositivo aprobada mediante esa sesión). Consulte [Federación OIDC → Tope de vida útil de la sesión](oidc-federation).

Merece la pena mencionar el flujo de dispositivo porque hasta hace poco era la excepción: el registro del código de
dispositivo no tenía dónde guardar el tope de la sesión que lo aprobaba, así que un dispositivo aprobado mediante una
sesión federada seguía emitiendo tokens durante toda la vida útil absoluta de renovación del cliente después de que
esa sesión hubiera terminado, y `RevalidateOnRefresh` tampoco volvía a consultar al IdP de origen por él. Ahora ambos
siguen a la sesión que lo aprobó. Un dispositivo aprobado mediante una sesión *no federada* no tiene ningún tope que
heredar, que es el mismo resultado que da el flujo de autorización.
