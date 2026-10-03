---
layout: default
title: Federación OIDC
locale: es
---

# Federación OIDC

Authagonal puede federar la autenticación con proveedores de identidad OIDC externos (Google, Apple, Azure AD, etc.). Esto permite flujos del tipo «Iniciar sesión con Google» mientras Authagonal sigue siendo el servidor de autenticación central.

## Cómo funciona {#how-it-works}

Hay dos vías de entrada a la federación:

**Basada en el dominio (inicio de sesión interactivo):**

1. El usuario introduce su correo electrónico en la página de inicio de sesión
2. La SPA llama a `/api/auth/sso-check`; si el dominio del correo está vinculado a un proveedor OIDC, el SSO es obligatorio
3. El usuario hace clic en «Continuar con SSO» y se le redirige al IdP externo (cuando el correo es el `login_hint` de una solicitud de autorización y su dominio está encaminado a una conexión, el usuario va directamente al IdP con el `login_hint` reenviado)
4. Tras autenticarse, el IdP redirige de vuelta a `/oidc/callback`
5. Authagonal valida el id_token, vincula al usuario (o lo crea si la conexión permite el aprovisionamiento JIT) y establece una cookie de sesión

**Indicada por la RP (`idp_hint`):**

La parte de confianza (RP) descendente puede encaminar directamente a un IdP de origen concreto sin pasar por el paso del correo y el dominio SSO. Añada `idp_hint={connectionId}` a `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Cuando la solicitud no está autenticada, Authagonal redirige a `/oidc/{connectionId}/login` y conserva la URL `/authorize` original como `returnUrl`. Una vez completada la federación, el usuario vuelve a `/authorize` con una cookie de sesión y el flujo continúa con normalidad. Si la conexión establece `InteractionPath`, el usuario se envía primero a esa página de la aplicación de inicio de sesión (consulte [Recopilar algo antes de federar](self-service-sso#collect-something-before-federating)). Una conexión con `ShowOnLogin: false` nunca se ofrece como botón de inicio de sesión y solo es accesible por esta vía.

## Configuración inicial {#setup}

### 1. Crear un proveedor OIDC {#1-create-an-oidc-provider}

**Opción A, configuración (recomendada para configuraciones estáticas):**

Añada a `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Los proveedores se cargan desde la configuración al arrancar. `ConnectionId`, `MetadataLocation`, `ClientId` y `ClientSecret` son obligatorios (sin ellos el arranque falla). `RedirectUrl` se acepta por compatibilidad y se ignora: el URI de redirección se deriva en cada solicitud como `{Issuer}/oidc/callback`, ya que tiene que estar en el origen en el que se encuentra el navegador, y ese es el URI que hay que registrar en el IdP (un valor distinto en la configuración se registra en el log como ignorado). El `ClientSecret` se protege mediante `ISecretProvider` (Key Vault cuando está configurado; texto plano en caso contrario). Las asignaciones de dominios SSO se registran automáticamente a partir de `AllowedDomains`, salvo en una conexión limitada a una organización, cuyos dominios solo se comparan dentro de su organización.

La configuración también puede establecer todos los indicadores de comportamiento de la tabla siguiente. **Una entrada cargada desde la configuración sustituye a la conexión almacenada en cada arranque**: un indicador que omita vuelve a su valor por defecto, así que indique en la configuración todos los indicadores que quiera conservar (`ConnectionName`, `IconUrl` y `OrganizationId` son los únicos valores que sobreviven a una omisión, y `CreatedAt` se conserva).

| Campo | Valor por defecto | Efecto |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Crea un usuario federado desconocido en su primer inicio de sesión. Desactivado significa que un usuario desconocido se rechaza con `access_denied` |
| `AllowUninvitedJit` | `false` | Con `ProvisioningAttributeParams` declarado, aprovisiona también a un usuario que llega sin ese contexto. Consulte [SSO de autoservicio](self-service-sso) |
| `ProvisioningAttributeParams` | ninguno | Claves de consulta de la solicitud de autorización que se capturan en un usuario aprovisionado por JIT como atributos de aprovisionamiento (el reflejo hacia dentro de `PassthroughParams`) |
| `PassthroughParams` | ninguno | Claves de consulta que se reenvían a la URL de autorización del IdP de origen; consulte [Parámetros de consulta de paso directo](#passthrough-query-parameters) |
| `SessionExpClaim` | ninguno | Consulte [Tope de vida útil de la sesión](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` oculta el botón «Continuar con»; solo se llega a la conexión mediante `idp_hint` |
| `ChallengeMfaAfterLogin` | `true` | `false` confía en la MFA propia del IdP de origen y omite el desafío local |
| `IsExternalConnection` | `false` | Marca un IdP de terceros propiedad del cliente. Neutraliza `UseUpstreamSubjectAsUserId` y `AutoLinkExistingByEmail` aunque estén establecidos |
| `UseUpstreamSubjectAsUserId` | `false` | El id local de un usuario JIT es el `sub` del IdP de origen en lugar de un GUID nuevo. Solo para conexiones propias |
| `AutoLinkExistingByEmail` | `false` | Vincula a una cuenta local existente por correo aunque `AllowedDomains` no cubra el dominio. Solo para conexiones propias |
| `RevalidateOnRefresh` | `false` | Consulte [Sesiones federadas](federated-sessions) |
| `InteractionPath` | ninguno | Ruta de la aplicación de inicio de sesión que se muestra antes de federar una solicitud `idp_hint` (debe empezar por `/`) |
| `OrganizationId` | ninguno | Limita la conexión a una organización; consulte [SSO de autoservicio](self-service-sso#organisation-scoped-connections) |

> **Un IdP en su propia red privada.** `MetadataLocation` debe ser https y, por defecto, debe resolverse a una dirección enrutable públicamente: Authagonal rechaza los destinos internos en todas las URL que descarga, tanto en la URL como de nuevo en el socket. Para federar con un IdP local, indíquelo en [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Eso cubre todo el intercambio, incluidos el `token_endpoint`, el `userinfo_endpoint` y el `jwks_uri` que nombra el documento de descubrimiento. https sigue siendo obligatorio: este documento proporciona las claves con las que se valida cada `id_token` del IdP de origen, y una red privada no es un canal seguro.

**Opción B, API de administración (para la gestión en tiempo de ejecución):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

El cuerpo de creación acepta `connectionName`, `metadataLocation`, `clientId` y `clientSecret` (todos obligatorios), además de `iconUrl`, `redirectUrl` (ignorado, opcional), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (por defecto `false`), `challengeMfaAfterLogin` (por defecto `true`) e `interactionPath`. El id de la conexión lo genera el servidor y se devuelve en el cuerpo del `201` (el secreto del cliente nunca se devuelve). `metadataLocation` debe ser https y se comprueba frente a la protección de descargas salientes en el momento de la creación. Los demás indicadores de la tabla anterior (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` y el resto) no se pueden establecer mediante la ruta de creación: cárguelos desde la configuración o escríbalos mediante `IOidcProviderStore` desde el código del host. No hay ruta de actualización para una conexión OIDC; para cambiar una, elimínela y vuelva a crearla (o edite la configuración). `GET /api/v1/oidc/connections/{connectionId}` y `DELETE` completan el conjunto.

### 2. Enrutamiento de dominios SSO {#2-sso-domain-routing}

Cuando se especifica `AllowedDomains` (en la configuración o mediante la API de creación), las asignaciones de dominios SSO se registran automáticamente. Sin enrutamiento por dominio, los usuarios pueden seguir dirigiéndose al inicio de sesión OIDC mediante `/oidc/{connectionId}/login`.

## Endpoints {#endpoints}

| Endpoint | Descripción |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia el inicio de sesión OIDC. Genera PKCE + state + nonce, deriva el ámbito para el IdP de origen y los parámetros de paso directo a partir de `returnUrl` y redirige al endpoint de autorización del IdP (`loginHint`, si está presente, se envía al IdP de origen como `login_hint`). `404` para una conexión desconocida. |
| `GET /oidc/callback` | Gestiona el callback del IdP. Canjea el código por tokens, valida el id_token, captura en la cookie cada claim que no sea del protocolo como `federated:*` y crea al usuario o inicia su sesión. |

## Paso de ámbitos y claims {#scope-and-claim-flow-through}

El conjunto de ámbitos que solicita la RP descendente en `/connect/authorize` se reenvía al IdP de origen, **filtrado al conjunto estándar de OIDC**: `openid`, `profile`, `email`, `address`, `phone`, con `openid` siempre incluido. Cualquier otra cosa que haya solicitado la RP (ámbitos de API personalizados, `offline_access`, …) se descarta antes de la llamada al IdP de origen (la única excepción es una conexión con `RevalidateOnRefresh`, que vuelve a añadir `offline_access` para poder obtener un token de actualización del IdP de origen): un IdP estricto como Google devuelve `invalid_scope` ante valores desconocidos, y el IdP de origen solo necesita identificar al usuario; los ámbitos propios de la RP se respetan en los tokens emitidos por Authagonal, no en los del IdP de origen. Los claims que el IdP de origen incluya en el id_token según los ámbitos vuelven a Authagonal, se guardan en el ticket de la cookie como claims `federated:<name>` y llegan a `OidcSubject.FederationClaims` en el siguiente paso por `/connect/authorize`. A partir de ahí, `ProtocolTokenService` los vuelve a emitir en los tokens emitidos por Authagonal, filtrados por la misma lista blanca `Scope.UserClaims` que filtra `CustomAttributes`. En caso de colisión de claves, prevalece el valor del propio almacén de usuarios de Authagonal: estos claims llegan literalmente desde el IdP de origen, así que dejar que sobrescribieran permitiría a un IdP controlado por el cliente reafirmar cualquier claim liberado por ámbito sobre su propio usuario e imponerse al registro que este servidor tiene de él. Un claim del IdP de origen sin contrapartida almacenada sigue pasando.

Efecto neto: no hay una lista de permitidos por conexión de los claims que hay que conservar. Se captura cada claim ajeno al protocolo que el IdP de origen pone en el id_token; cuáles llegan a los tokens descendentes lo controlan los `UserClaims` del ámbito descendente: declare allí el claim y el valor pasa.

`FederationClaims` sobrevive a las rotaciones de renovación de forma independiente de `CustomAttributes`, de modo que el contexto de federación por sesión (p. ej., un token de enlace compartido capturado en la autorización original) se mantiene intacto mientras los atributos por usuario se siguen releyendo actualizados desde el almacén de usuarios.

## Parámetros de consulta de paso directo {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` es una lista blanca por conexión de claves de consulta que pasan de la solicitud `/authorize` original a la URL de autorización del IdP de origen. El conjunto estándar (`scope`, `state`, `nonce`, PKCE) siempre se reenvía; esto es para valores adicionales especificados por la RP, como una credencial de un solo uso que el IdP de origen necesita para autenticar (p. ej., `link_token` para IdP de enlaces compartidos).

Cuando una clave está en la lista blanca, Authagonal toma su valor de la consulta `/authorize` original (transportada mediante `returnUrl`) y lo añade a la URL del IdP de origen. Todo lo que no está en la lista blanca se descarta sin aviso.

## Tope de vida útil de la sesión {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` es el nombre opcional de un claim del id_token (segundos Unix) cuyo valor limita la vida útil de la sesión local. Si está presente, el valor del IdP de origen viaja como `session_max_exp` en el ticket de la cookie y en el código de autorización emitido; los tokens de acceso, de ID y de actualización se limitan para que ningún token, incluidos los emitidos a partir de rotaciones, sobreviva a la sesión del IdP de origen. Resulta útil cuando el IdP de origen impone límites de sesión más cortos que los que Authagonal aplicaría por defecto.

## Funciones de seguridad {#security-features}

- **PKCE**: code_challenge con S256 en cada solicitud de autorización
- **Validación del nonce**: el nonce se guarda junto con el state, debe estar presente en el id_token y coincidir
- **Validación del state**: de un solo uso (consumido de forma atómica mediante `IOidcStateStore`, persistido con caducidad) **y ligado al navegador**: al iniciar sesión se establece una cookie `SameSite=Lax` limitada a `/oidc` que debe coincidir con el `state` del callback, de modo que un atacante no puede completar un flujo de federación que él mismo inició y entregar la URL del callback a una víctima (CSRF de inicio de sesión)
- **Validación de la firma del id_token**: claves obtenidas del endpoint JWKS del IdP; se validan el emisor, la audiencia y la vida útil
- **Recurso a userinfo**: si el id_token no contiene un correo, se prueba el endpoint userinfo. El `sub` de userinfo debe coincidir con el `sub` del id_token (OIDC Core 5.3.2); de lo contrario, la respuesta se ignora
- **Vinculación de identidad estable**: un usuario que vuelve se resuelve por proveedor + `sub`, nunca solo por el correo. Vincular una identidad federada a una cuenta local **ya existente** por correo exige que el `AllowedDomains` de la conexión cubra el dominio de ese correo (la garantía explícita del administrador de que el IdP es su dueño) o `AutoLinkExistingByEmail` en una conexión propia, y se rechaza cuando el dominio está encaminado a otra conexión. Una cuenta ya vinculada a la identidad federada de otra conexión solo se adopta cuando esta conexión es la autoridad para el dominio, en cuyo caso se elimina la vinculación anterior. Un `email_verified` afirmado por el IdP de origen *no* basta para apropiarse de una cuenta existente
- **Restricción de dominios**: cuando `AllowedDomains` está establecido, la conexión solo puede afirmar identidades dentro de esos dominios (en caso contrario, `access_denied`)
- **JIT es opcional**: salvo que la conexión establezca `JitProvisioningEnabled`, un usuario desconocido se rechaza con `access_denied`. Cuando JIT sí se aplica, un IdP de origen que no afirma `email_verified` no puede crear una cuenta, y tampoco puede hacerlo una conexión cuyo dominio de correo está encaminado a otra conexión
- **Protección contra redirecciones abiertas**: `returnUrl` debe ser una ruta relativa del mismo sitio; se rechazan las formas relativas al protocolo (`//`) y con barra invertida
- **La MFA local sigue aplicándose por defecto**: la federación solo demuestra el primer factor. Un usuario inscrito en MFA (o cuya política de cliente exige MFA) pasa por las páginas locales de desafío o de configuración inicial de MFA tras el callback en lugar de iniciar sesión directamente; solo entonces la sesión lleva el marcador de MFA. Una conexión con `ChallengeMfaAfterLogin: false` omite esto e inicia la sesión del usuario como autenticado con MFA basándose únicamente en la federación
- **Confianza acotada en los metadatos**: el documento de descubrimiento debe ser https y su URL debe estar ligada al emisor que nombra, y los id_token del IdP de origen solo se aceptan con algoritmos de firma asimétricos (RS/PS/ES 256, 384, 512)
- **Vinculación a la organización**: un usuario que inicia sesión a través de una conexión limitada a una organización pasa a ser miembro de esa organización y la sesión lleva su `org_id`

## Particularidades de Azure AD {#azure-ad-specifics}

Azure AD a veces devuelve los correos como un arreglo JSON en el claim `emails` (sobre todo en B2C). Authagonal lo gestiona comprobando tanto el claim `email` como el arreglo `emails` (un arreglo JSON o una sola cadena).

## Proveedores admitidos {#supported-providers}

Cualquier proveedor compatible con OIDC que admita:
- El flujo de código de autorización
- PKCE (S256)
- El documento de descubrimiento (`.well-known/openid-configuration`)

Probado con:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Guías relacionadas {#related-guides}

- [SSO de autoservicio](self-service-sso): modalidades de aprovisionamiento JIT (solo por invitación frente a autoservicio), el nivel de confianza de la conexión e intersticiales previos a la federación.
- [Sesiones federadas](federated-sessions): trasladar la revocación en el IdP de origen a la sesión local con `RevalidateOnRefresh`.
- [Actualizar un usuario](user-upgrade): permitir que una cuenta federada o de invitado obtenga una contraseña propia.
