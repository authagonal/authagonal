---
layout: default
title: Backend-for-Frontend (BFF)
locale: es
---

# Backend-for-Frontend (BFF)

Una SPA de navegador que guarda un token de acceso o de actualización en un almacenamiento accesible desde JavaScript expone ambos a XSS. El BFF es un **cliente OIDC confidencial que usted aloja en su propio backend**. Ejecuta el flujo de código de autorización + PKCE en el servidor, guarda los tokens en una sesión del lado del servidor y no entrega al navegador nada más que una cookie de sesión httpOnly. Las llamadas de la SPA a sus API pasan por el proxy del BFF, que adjunta el token de acceso de la sesión a la salida.

Se distribuye en dos paquetes que hablan el mismo protocolo:

| Paquete | Para | Código fuente |
|---|---|---|
| `Authagonal.Bff` (NuGet) | Hosts de ASP.NET Core | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express y Next.js (App Router) | `bff-lib/` |

El BFF es un cliente confidencial corriente del host de autenticación: usa el descubrimiento OIDC y los endpoints de autorización, token, revocación y fin de sesión, de modo que lo único que necesita del host de autenticación es un cliente registrado.

## 1. Registrar un cliente BFF {#1-register-a-bff-client}

El cliente debe ser **confidencial** (tiene un secreto), exigir PKCE y tener permitido `offline_access` si desea la renovación en el servidor. Registre:

- el URI de redirección `https://app.example.com/bff/callback`
- el URI de redirección posterior al cierre de sesión `https://app.example.com/` (y `https://app.example.com/bff/logout-callback` si usa `returnUrl` al cerrar sesión; consulte [Cierre de sesión](#logout))

Para un «cerrar sesión en todas partes» a nivel de sujeto mediante [cierre de sesión por back-channel (canal de retorno)](index#key-features), registre el cliente con `BackChannelLogoutSessionRequired = false`. El BFF acepta tokens de cierre de sesión que llevan `sid` o solo `sub`.

## 2. Conectarlo (.NET) {#2-wire-it-up-net}

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` es importante: detrás de un proxy que termina TLS, el BFF ve http sin cifrar, así que sin él la cookie de sesión `__Host-` se escribe sin `Secure` y los navegadores la descartan. Consulte [Instalación](installation#production-security-checklist) para declarar el proxy.

### Node (Express) {#node-express}

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

Para Next.js, use `createBffRoute` de `@authagonal/bff/next` en `app/bff/[...bff]/route.ts`. Consulte `bff-lib/README.md` para ambos.

## Endpoints {#endpoints}

Montados bajo `BasePath` (por defecto `/bff`).

| Ruta | Propósito |
|---|---|
| `GET /bff/login?returnUrl=/` | Inicia el inicio de sesión: establece una cookie de correlación por inicio de sesión y redirige a `/connect/authorize` con PKCE (`S256`), `state` y `nonce`. |
| `GET /bff/callback` | El URI de redirección OIDC (`CallbackPath`). Canjea el código y crea la sesión. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Requiere el encabezado antifalsificación. Siempre `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Finaliza la sesión localmente y en el host de autenticación. Un `POST` requiere el encabezado antifalsificación; un `GET` es una navegación simple. |
| `GET /bff/logout-callback` | Página de llegada del viaje de ida y vuelta de fin de sesión cuando el cierre de sesión recibió un `returnUrl`. |
| `POST /bff/backchannel-logout` | Consumidor de servidor a servidor del cierre de sesión OIDC por back-channel. Se autentica mediante el token de cierre de sesión firmado, por lo que no lleva encabezado CSRF. |
| `GET /bff/ws-ticket` | Opcional (`WsTicketsEnabled`), solo .NET. Consulte [Autenticación de WebSocket](websocket-auth). |
| `GET /bff/token?resource=...` | Opcional (`TokenEndpointEnabled`), solo .NET. Consulte [Tokens intercambiados para otro origen](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | El proxy que inyecta tokens. Solo se asigna cuando `Upstreams` no está vacío. |

`claims` en `/bff/user` es un mapa plano de cadenas con los claims del id_token, sin la maquinaria del protocolo (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). Los claims de tipo arreglo, como `roles` y `groups`, se unen con espacios. Los claims se vuelven a leer de cada id_token renovado, de modo que un rol concedido después del inicio de sesión llega a la SPA en la siguiente renovación y no en el siguiente inicio de sesión.

## Desde el navegador {#from-the-browser}

Toda llamada que no sea una navegación lleva un encabezado estático, que protege contra CSRF junto con `SameSite=Lax`. Se acepta cualquier valor; solo se comprueba su presencia.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Inicie y cierre sesión **navegando** (`location.href = '/bff/login'`), no con `fetch`. El nombre del encabezado es `AntiForgeryHeader`.

## El proxy {#the-proxy}

Configure los upstreams y la SPA llama a `/bff/api/<prefix>/...`:

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

El proxy exige el encabezado antifalsificación y una sesión activa, renueva el token de acceso si le quedan menos de `RefreshThresholdSeconds` para caducar, reenvía la solicitud con `Authorization: Bearer` y devuelve la respuesta en streaming. La cookie de sesión nunca se reenvía. Los encabezados entrantes `X-Forwarded-*`, `Forwarded` y `X-Real-IP` se eliminan y se vuelven a establecer a partir del estado propio del BFF, de modo que un script de la SPA no puede avalar una IP de cliente ni un esquema. Las redirecciones del upstream se transmiten al navegador en lugar de seguirse.

Por upstream (`BffUpstream`):

| Propiedad | Significado |
|---|---|
| `Prefix` | Ruta después de `/bff/api` que selecciona este upstream. |
| `TargetBaseUrl` | Adónde se reenvían las solicitudes que coinciden. |
| `StripPrefix` | Elimina el prefijo coincidente antes de añadir la ruta al destino. Permite que un BFF reparta el tráfico entre varios backends que comparten un espacio de nombres de rutas. |
| `RequiredAuthority` | Pares `"type:action"`. El proxy comprueba el `authorization_details` de RFC 9396 del token saliente y devuelve 403 a menos que se permitan todos los pares. Consulte [Autenticación agéntica](agentic-auth). |
| `AuthorityLocation` | La raíz de `locations` con la que se conoce este upstream, cuando difiere de `TargetBaseUrl`. |
| `StrictAuthority` | Rechaza la llamada cuando una concesión lleva una restricción que el proxy no puede evaluar, en lugar de dejarla pasar. |

Opciones relacionadas: `AllowAnonymousProxyRequests` reenvía una solicitud sin sesión (o con una que no se puede renovar) sin encabezado `Authorization` en lugar de responder 401, para las API que deciden por sí mismas. Una ruta protegida por `RequiredAuthority` nunca es anónima. `ExchangeRoutes` vincula rutas del proxy a un [intercambio RFC 8693](agentic-auth), de modo que el upstream recibe un token de alcance reducido y ligado al contexto en lugar del token principal de la sesión: cada ruta tiene un `PathPattern` con exactamente un marcador de posición (la única restricción admitida es `:guid`), el segmento capturado se envía como parámetro del intercambio y un intercambio denegado produce un 403. Una restricción desconocida falla al arrancar en lugar de reenviar silenciosamente el token más amplio.

## Cierre de sesión {#logout}

`/bff/logout` revoca el token de actualización de la sesión (en la medida de lo posible), elimina la sesión, borra la cookie y redirige al endpoint de fin de sesión del host de autenticación con el `id_token_hint` de la sesión. Sin sesión no hay nada que finalizar en el host de autenticación, así que redirige directamente a `PostLogoutRedirectUri`. Con un `returnUrl`, el host de autenticación redirige de vuelta a `/bff/logout-callback`, que vuelve a validar el destino frente a `ReturnUrlAllowlist` y redirige allí. Registre ese callback como URI de redirección posterior al cierre de sesión del cliente.

El cierre de sesión por back-channel elimina sesiones en el servidor: por `sid` cuando el token de cierre de sesión lo incluye y, si no, todas las sesiones del `sub`. Las eliminaciones se limitan al inquilino cuyo emisor firmó el token, porque `sub` solo es único dentro de un emisor. El token de cierre de sesión debe llevar `iat` y ser reciente.

## Referencia de opciones (.NET) {#options-reference-net}

| Opción | Valor por defecto | Notas |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | obligatorio | No es obligatorio cuando se establece `TenantQueryParam`. |
| `Scope` | `openid profile offline_access` | `offline_access` habilita la renovación. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Debe coincidir con el URI de redirección registrado. |
| `CookieName` | `__Host-agbff` | El prefijo `__Host-` impone Secure, `Path=/` y ningún Domain, por lo que requiere https. Sustitúyalo para el desarrollo local con http. |
| `SessionLifetime` | 8 horas | Tope absoluto con independencia de las renovaciones. |
| `PersistentCookie` | `false` | Si es true, la cookie recibe un `Max-Age` acotado a `SessionLifetime` y sobrevive a un reinicio del navegador («mantener la sesión iniciada»). El cierre de sesión por back-channel sigue finalizando la sesión. |
| `CorrelationLifetime` | 30 minutos | Cuánto puede durar un inicio de sesión entre `/bff/login` y el callback. Cubre el registro, el correo de verificación y el inicio de sesión. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | vacío | Orígenes a los que puede apuntar un `returnUrl` no relativo. Las rutas relativas siempre se permiten; cualquier otra cosa se convierte en `/`. |
| `LoginPassthroughParams` | vacío | Nombres de parámetros de consulta que se copian de `/bff/login` a `/connect/authorize` (por ejemplo `idp_hint`). Los parámetros estándar siempre prevalecen. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | ninguno | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | desactivado, 30 s, vacío | Consulte [Autenticación de WebSocket](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | desactivado, vacío, vacío | Habilitarlo sin recursos falla al arrancar. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | vacío, vacío, `false` | Consulte [El proxy](#the-proxy). |
| `TenantQueryParam` | ninguno | Modo multiinquilino, más abajo. |

`SessionMode` solo tiene implementado `Store`; `Stateless` está reservado y falla al arrancar.

El paquete de Node acepta los equivalentes en camelCase de `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` y `tenantQueryParam`, además de `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` y `clientIp`. No incluye los endpoints de tickets de WebSocket ni de token.

## Sesiones y ejecución de más de una instancia {#sessions-and-running-more-than-one-instance}

Las sesiones se almacenan mediante `IBffSessionStore`. El valor por defecto es `IDistributedCache`, en memoria a menos que registre una caché real (Redis, por ejemplo) **antes** de `AddAuthagonalBff`.

Una caché compartida no basta por sí sola. La renovación de vuelo único es por proceso, mientras que la sesión y su token de actualización rotatorio residen en la caché compartida. Dos réplicas pueden leer la misma sesión, ver ambas que necesita renovarse y canjear ambas el mismo token de actualización. El host de autenticación interpreta el segundo canje como la reutilización de un token robado y revoca toda la familia de concesiones, lo que cierra la sesión del usuario en todas partes. Proporcione un bloqueo entre réplicas de una de estas dos maneras:

- **Registre un `ILeaseProvider`** (cualquier backend). Los proveedores de Azure, AWS y SQL incluyen uno mediante `AddAuthagonalClustering`. Consulte [Escalado](scaling).
- **Implemente `IBffRefreshLockStore` en su almacén de sesiones** (`TryAcquireRefreshLockAsync(sessionId, ttl)` y `ReleaseRefreshLockAsync`). Es una escritura condicional con TTL, por ejemplo `SET NX PX` en Redis. El almacén por defecto no puede ofrecerlo porque `IDistributedCache` no tiene una operación de escritura solo si no existe. El almacén de sesiones de Node incluye los equivalentes `acquireRefreshLock` / `releaseRefreshLock`.

Sin ninguno de los dos, el despliegue depende de `Auth:RefreshTokenReuseGraceSeconds` del host de autenticación, cuyo valor por defecto en el host Server es 0 (estricto). El BFF registra una advertencia al arrancar cuando el almacén de sesiones parece compartido y no hay ningún bloqueo.

Un `IBffSessionStore` personalizado debe respetar el argumento `tenantKey` en `RemoveBySidAsync` y `RemoveBySubjectAsync`. Los demás puntos de extensión son `ICookieProtector` (por defecto: ASP.NET Data Protection) y `ITokenClient`.

## Muchos inquilinos desde un solo BFF {#many-tenants-from-one-bff}

Establezca `TenantQueryParam` (por ejemplo `"slug"`) y registre un `IBffTenantResolver`. `/bff/login?slug=acme` resuelve el `BffTenantConfig` del inquilino (autoridad, id de cliente, secreto, ámbito), la clave se guarda en la sesión para que las solicitudes posteriores la vuelvan a resolver, y el cierre de sesión por back-channel resuelve el inquilino a partir del `iss` del token mediante `ResolveByIssuerAsync`. Si `TenantQueryParam` no está establecido, el BFF es de un solo inquilino y se usan las opciones estáticas.

## Tokens intercambiados para otro origen {#exchanged-tokens-for-another-origin}

El modelo de cookies no puede llegar a un servidor de recursos en otro origen (por ejemplo, una aplicación en iframe que la SPA incrusta). `TokenEndpointEnabled` añade `GET /bff/token?resource=<audience>`, que devuelve `{ accessToken, expiresInSeconds }` para un token **intercambiado** según RFC 8693: dirigido a un recurso de `TokenEndpointResources` (cualquier otro produce un 400 `resource_not_allowed`), ligado a los valores de `TokenEndpointExchangeParams` presentes en la consulta y de vida corta. El navegador nunca recibe el token principal de la sesión y debe guardar el intercambiado solo en memoria. El cliente del inquilino necesita la concesión de intercambio de tokens y debe declarar los recursos como sus audiencias. Un intercambio denegado produce un 403.
