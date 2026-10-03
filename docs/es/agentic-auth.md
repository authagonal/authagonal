---
layout: default
title: Autenticación agéntica
locale: es
---

# Autenticación agéntica

Authagonal incluye los componentes necesarios para delegar de forma segura la autoridad de un usuario en agentes de IA (o
en cualquier carga de trabajo no humana): agentes registrados, concesiones de autoridad de grano fino, tokens de
delegación compuestos, consentimiento permanente del usuario, aprobaciones justo a tiempo, tickets de capacidad y una
superficie de auditoría consciente de la delegación. La biblioteca es dueña de las primitivas y del invariante; la
aplicación host las ensambla en un producto (las implementaciones de conectores, la experiencia de aprobación, el envío
de notificaciones y la política de negocio quedan del lado del host).

## El invariante {#the-invariant}

Todo token delegado cumple:

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Nada aguas abajo puede ampliarla; cada salto de delegación adicional vuelve a intersecar, de modo que la autoridad solo
puede estrecharse. La intersección se implementa una sola vez (`AuthoritySet.Intersect`) y se usa en todas partes.

## Entidades {#entities}

| Entidad | Tipo | Notas |
|---|---|---|
| Agente | `AgentProfile` sobre un `OAuthClient` confidencial | Registrar un perfil es lo que convierte a un cliente en agente; eliminarlo devuelve el cliente a OAuth simple. |
| Autoridad | `AuthoritySet` / `AuthorityGrant` | Forma `authorization_details` de RFC 9396: `type` de conector, `actions`, `locations`, restricciones y políticas `auto`/`ask`/`deny` por acción. |
| Techo | `AgentProfile.Ceiling` | La autoridad más amplia que puede portar cualquier delegación a través del agente. Gestionado por el administrador (`/api/v1/agents`). |
| Consentimiento (suelo) | `PersistedGrant` de tipo `agent_consent` | Por (usuario, agente), gestionado en `/consent/agents`. Se almacena ya intersecado con el techo y se vuelve a intersecar en cada emisión. |
| Delegación | Intercambio de tokens RFC 8693 | Identidad compuesta: `sub` = usuario, `act` = agente (anidado por salto), `authorization_details` = la intersección efectiva. De vida corta, nunca renovable. |
| Aprobación | `PersistedGrant` de tipo `approval` | Compuerta justo a tiempo para acciones con política `ask`; semántica de sondeo del flujo de dispositivo; de un solo uso y ligada a la forma de la solicitud. |
| Ticket de capacidad | `ICapabilityTicketService` | Identificador opaco de un solo uso ligado a un token: el ws-ticket del BFF generalizado, atómico sobre el almacén de concesiones. |
| Auditoría | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync`, más la compuerta previa a la emisión `OnTokenIssuingAsync`. |

## Registro de un agente {#registering-an-agent}

1. Cree un cliente confidencial que permita `urn:ietf:params:oauth:grant-type:token-exchange`
   (modo delegado) y/o `client_credentials` (modo de servicio).
2. `PUT /api/v1/agents/{clientId}`:

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` es `delegated`, `service` o `both` (si se omite `mode` en una actualización, se conserva el valor existente). `maxDelegationDepth` debe estar entre 0 y 8 (por defecto 0), `maxTokenLifetimeSeconds` entre 30 y 86400 (por defecto 300), y `highRiskDefault` debe ser `auto`, `ask` o `deny`; cualquier otro valor produce un 400.

Los miembros de las restricciones se tipan según su forma JSON: cadena o arreglo de cadenas → lista de permitidos
(combinación por intersección de conjuntos; las entradas admiten coincidencia exacta, comodín `*.host` y coincidencia por
`@suffix`), número → tope (combinación por mínimo), booleano → compuerta (combinación por AND). Los miembros que no se
pueden interpretar se conservan literalmente y fallan en modo cerrado al evaluarse.
`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` muestra una vista previa de techo ∩ consentimiento para la
interfaz de administración.

## Consentimiento del usuario (el suelo) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`: el techo presentado frente al catálogo de conectores
  (registre un `IConnectorCatalog` para nombres visibles, descripciones de acciones e indicadores de alto riesgo;
  sus tipos se anuncian en el documento de descubrimiento como `authorization_details_types_supported`).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`: concede el suelo (omita
  `authority` para consentir el techo completo). Un usuario puede endurecer una política (`auto` → `ask`)
  pero nunca relajarla ni ampliarla: el almacén interseca previamente con el techo vigente.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`: listar y revocar. La revocación
  detiene la siguiente emisión; las delegaciones en circulación no tienen renovación y caducan dentro de su
  (corta) vida útil. Sin consentimiento → el intercambio falla con `invalid_grant` /
  `consent_required`; el techo por sí solo no concede nada.

## Emisión de una delegación {#minting-a-delegation}

El agente se autentica como sí mismo e intercambia el token del usuario:

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

La emisión aplica, en este orden: el modo del agente, el consentimiento permanente, la profundidad de subdelegación (cada
actor ya presente en la cadena `act` necesita presupuesto de `maxDelegationDepth` para un salto más), la intersección,
las denegaciones de solicitudes explícitas (`invalid_target`: un agente no debe creer que posee una autoridad de la que
carece), la compuerta de aprobación y los límites de vida útil (vida útil del cliente ∩ tiempo restante del token
sujeto ∩ `maxTokenLifetimeSeconds`). El token lleva `act` (RFC 8693; anidado por salto) y `authorization_details`
(RFC 9396); la respuesta devuelve los detalles concedidos; la introspección emite ambos. Volver a intercambiar un token
delegado lo atenúa automáticamente, porque el propio claim del token sujeto se suma a la intersección.

Los clientes **sin** perfil de agente conservan exactamente el comportamiento actual del intercambio, salvo que un
parámetro de solicitud `authorization_details` ahora estrecha (nunca amplía) el token intercambiado.

## Aprobaciones (compuerta de aprobación) {#approvals-ask-gate}

Cuando el segmento efectivo contiene una acción `ask`, el intercambio queda en espera:

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

Se notifica al host mediante `IAuthHook.OnApprovalRequestedAsync` (el envío por correo, push o chat corresponde al
host). El usuario la resuelve (`GET /approvals`, `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }`) mientras el agente reintenta la solicitud idéntica más
`approval_id`, con el vocabulario del flujo de dispositivo en todo momento (`slow_down`, `access_denied`,
`expired_token`). Las aprobaciones son de un solo uso (consumo atómico), caducan tras
`ApprovalLifetimeSeconds` (por defecto 300) y quedan ligadas a la forma exacta de la solicitud *y al estado actual de
la política*: una edición del techo por parte del administrador entre la espera y el sondeo invalida la aprobación en
lugar de emitir una autoridad obsoleta. Una aprobación consumida emite con sus acciones `ask` resueltas como `auto`
(ya se preguntó y se respondió).

El modo de servicio (`client_credentials`) no tiene a ningún usuario en el circuito: el techo se aplica por sí solo y
`ask` se degrada a `deny`.

## Aplicación en el lado del recurso {#resource-side-enforcement}

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` en cualquier servidor de recursos
  (las claves de contexto se comparan con los nombres de las restricciones; pase lo que pueda derivar,
  p. ej. `recipient_domains` al enviar correo). Un token sin el claim se evalúa sin
  restricciones (compatibilidad heredada); un claim corrupto se evalúa como denegación total.
  - `location` es el valor `locations` de RFC 9396 sobre el que está actuando. Una concesión que nombra
    ubicaciones solo se respeta en ellas; una ubicación concedida es una **raíz**, de modo que
    `https://api.example.com/orders` cubre `/orders/17` pero no `/orders-admin`.
  - `strict: true` deniega cuando quien llama no aportó contexto para una restricción, en lugar de
    omitirla. Úselo siempre que pueda enumerar todas las claves que admite:
    `AuthoritySet.UncheckedConstraints(type, context)` indica las que no comprobó.
- Punto de control del BFF: `BffUpstream.RequiredAuthority = ["email:send"]` hace que el proxy compruebe el
  bearer saliente antes de reenviar, con 403 si falla y sin paso anónimo. La ubicación que
  presenta es el upstream al que la solicitud llegará realmente (`AuthorityLocation` sustituye la
  raíz cuando la autoridad se emite frente a un identificador público en lugar de la dirección interna);
  `StrictAuthority` hace que el proxy rechace una restricción que no puede evaluar en lugar de dejarla
  en manos del upstream.

## Tickets de capacidad {#capability-tickets}

`ICapabilityTicketService` (por defecto `GrantStoreCapabilityTicketService`, registrado con `TryAdd`
por `AddAuthagonalCore`, de modo que `AddAuthagonal` también lo obtiene) emite identificadores opacos de un solo uso
ligados a un token, que se canjean de forma atómica mediante el borrado condicional del almacén de concesiones; son
duraderos y seguros frente a la repetición entre pods, a diferencia del patrón «leer y después eliminar» de una caché
simple. El ws-ticket del BFF conserva su contrato existente de caché distribuida
(`WsTicketKey` / `TryRedeemWsTicketAsync`) porque quien lo canjea suele ser un host independiente
que solo comparte Redis; los intermediarios alojados en el mismo host deberían preferir el servicio de tickets de
capacidad.

## private_key_jwt {#private_key_jwt}

Los agentes son cargas de trabajo; los secretos compartidos son el eslabón más débil de la cadena. Establezca
`OAuthClient.JwksJson` (JWKS en línea) o `JwksUri` (descargado y almacenado en caché unos 10 min) y autentíquese
con una aserción de cliente RFC 7523 (`client_assertion_type=…:jwt-bearer`). Se aplican:
la firma frente al JWKS registrado, `iss` = `sub` = `client_id`, audiencia = emisor o
endpoint de token, `exp` acotado (≤ 10 min) y `jti` de un solo uso (caché antirrepetición sobre
`IRevokedTokenStore`). Una aserción presente nunca recurre a la vía del secreto.

## Compatibilidad {#compatibility}

- Sin perfil de agente → ningún cambio de comportamiento en ningún flujo. Todas las tablas y columnas nuevas son
  anulables con valor por defecto y se aprovisionan automáticamente en ambos proveedores de almacenamiento (tabla
  `AgentProfiles`; `JwksJson`/`JwksUri` en los clientes; los consentimientos, aprobaciones y tickets viajan en la tabla
  de concesiones existente).
- Los nuevos miembros de `IAuthHook` son métodos de interfaz por defecto; los hooks existentes compilan sin cambios.
- `ITokenExchangeSubjectTransformer` sigue ejecutándose en cada intercambio y puede rechazar o vincular
  claims de contexto; nunca puede ampliar la delegación (su salida se vuelve a intersecar) ni tocar
  la cadena `act` (claim reservado).
