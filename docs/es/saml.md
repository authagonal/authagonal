---
layout: default
title: SAML
locale: es
---

# SP de SAML 2.0

Authagonal incluye una implementación propia de proveedor de servicios (SP) de SAML 2.0. No usa ninguna biblioteca SAML de terceros: está construida sobre `System.Security.Cryptography.Xml.SignedXml` (parte de .NET).

## Alcance {#scope}

- **SSO iniciado por el SP** (el usuario empieza en Authagonal y se le redirige al IdP)
- **Binding HTTP-Redirect** para AuthnRequest (opcionalmente firmado; consulte más abajo)
- **Binding HTTP-POST** para la respuesta (ACS)
- **Aserciones cifradas** (`EncryptedAssertion`), descifradas con un par de claves del SP propio de cada conexión
- **Single Logout** (iniciado por el SP y por el IdP, bindings Redirect y POST)
- Azure AD / Entra ID es el objetivo principal, pero funciona cualquier IdP conforme (se reconocen los nombres de atributo de Okta, OneLogin, Ping, Google Workspace, ADFS y Shibboleth)

### No admitido {#not-supported}

- Binding Artifact
- Cifrado de aserciones con AES-GCM (limitación de `EncryptedXml` de .NET; configure AES-CBC en el IdP, consulte más abajo)

**El inicio de sesión iniciado por el IdP funciona y no hay que reconfigurar el mosaico**, pero no es la aserción no solicitada la que inicia la sesión del usuario. Una respuesta sin `InResponseTo` se descarta, y el ACS redirige el navegador a `/saml/{connectionId}/login`, que emite un AuthnRequest nuevo ligado a ese navegador. El usuario ya está autenticado en el IdP, así que este responde de inmediato y el recorrido es invisible; el `RelayState` del IdP se transmite como URL de retorno, de modo que el usuario sigue llegando al enlace profundo con el que se configuró el mosaico.

La aserción tiene que descartarse porque aceptar una no solicitada permite que cualquiera con una cuenta en ese IdP introduzca una sesión firmada en cualquier agente de usuario (todas las reglas del §4.1.4.3 se cumplen con una aserción que el atacante obtuvo legítimamente para su propia cuenta), y porque exigir la cookie de la solicitud en la vía iniciada por el SP no sirve de nada mientras la misma aserción pueda reproducirse quitándole `InResponseTo`. Reiniciar el flujo mantiene el mosaico funcionando sin aceptar nada de eso: quien acaba con la sesión iniciada es quien el IdP nombra en el intercambio *nuevo*.

El reinicio ocurre una sola vez por navegador. Un IdP que responde al AuthnRequest con otra respuesta no solicitada se rechaza con `error=saml_unsolicited` en lugar de volver a redirigirse, de modo que un IdP mal configurado no puede provocar un bucle de redirecciones.

Para aceptar la aserción no solicitada tal cual, establezca `allowUnsolicitedResponses: true` en la conexión (**desactivado por defecto**). Con esta opción activada, se omite la comprobación del ID de solicitud en las respuestas no solicitadas, pero se sigue exigiendo el uso único del ID de aserción (consulte Seguridad).

## Configuración inicial de Azure AD {#azure-ad-setup}

### 1. Crear un proveedor SAML {#1-create-a-saml-provider}

**Opción A: configuración (recomendada para configuraciones estáticas)**

Añada a `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Los proveedores se cargan desde la configuración al arrancar. `ConnectionId`, `EntityId` y `MetadataLocation` son obligatorios para una conexión nueva (sin ellos el arranque falla). Las asignaciones de dominios SSO se registran automáticamente a partir de `AllowedDomains`, salvo en una conexión limitada a una organización, cuyos dominios solo se comparan dentro de su organización. Un proveedor recién cargado desde la configuración no recibe par de claves del SP (por tanto, no hay AuthnRequest firmados, aserciones cifradas ni mensajes de cierre de sesión firmados); para esas funciones, use la API de administración.

La configuración también puede establecer `OrganizationId`, `JitProvisioningEnabled` (por defecto `false`), `ChallengeMfaAfterLogin` (por defecto `true`), `ProvisioningAttributeParams`, `AllowUninvitedJit` y `AllowUnsolicitedResponses`. La carga lee la conexión almacenada y fusiona, de modo que una conexión existente conserva su par de claves del SP, los metadatos pegados, el formato de NameID, `signAuthnRequests` y el icono, para los que la configuración no tiene campo. Los indicadores de comportamiento anteriores se escriben desde la configuración en cada arranque, así que un indicador que omita vuelve a su valor por defecto.

`EntityId` es **el entity ID de su SP** (el identificador que registra en el IdP), no el entity ID del IdP.

> **Un IdP en su propia red privada.** `MetadataLocation` debe ser https y, por defecto, debe resolverse a una dirección enrutable públicamente: el documento de metadatos contiene los certificados con los que se valida cada aserción, y Authagonal rechaza los destinos internos en todas las URL que descarga. Para federar con un IdP local, indíquelo en [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Si el IdP no publica ningún endpoint de metadatos https, pegue el documento en `MetadataXml` mediante la API de administración.

**Opción B: API de administración (para la gestión en tiempo de ejecución)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

La API genera el `connectionId` (un GUID) y lo devuelve en la cabecera `Location` y en el cuerpo de la respuesta. Campos opcionales adicionales: `metadataXml` (metadatos pegados, consulte más abajo), `nameIdFormat` (consulte más abajo), `signAuthnRequests` (fuerza AuthnRequest firmados), `iconUrl` (icono del botón de inicio de sesión), `jitProvisioningEnabled` (crea automáticamente los usuarios desconocidos en su primer inicio de sesión; **desactivado por defecto**, así que un usuario desconocido se rechaza hasta que lo establezca), `challengeMfaAfterLogin` (por defecto `true`; `false` confía en la MFA propia del IdP), `provisioningAttributeParams` y `allowUninvitedJit` (consulte [SSO de autoservicio](self-service-sso)), `organizationId` (limita la conexión a una organización; consulte [SSO de autoservicio](self-service-sso#organisation-scoped-connections)) y `allowUnsolicitedResponses` (acepta tal cual una aserción iniciada por el IdP en lugar de reiniciar el flujo; desactivado por defecto, consulte más arriba). Las conexiones creadas mediante la API también reciben un par de claves del SP generado automáticamente (consulte Par de claves del SP más abajo).

Las conexiones se gestionan mediante `POST` / `GET` / `PUT` / `DELETE` sobre `/api/v1/saml/connections[/{connectionId}]`. `PUT` es una actualización parcial: solo se modifican los campos enviados.

### 2. Configurar Azure AD {#2-configure-azure-ad}

1. En Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. Enrutamiento de dominios SSO {#3-sso-domain-routing}

Cuando se especifica `AllowedDomains` (en la configuración o mediante la API de creación), las asignaciones de dominios SSO se registran automáticamente. Cuando un usuario introduce `user@acme.com` en la página de inicio de sesión, la SPA detecta que el SSO es obligatorio y muestra «Continuar con SSO». Un dominio solo puede asignarse a una conexión; la API rechaza un dominio que ya haya reclamado otra conexión.

También puede gestionar los dominios en tiempo de ejecución mediante la API de administración; consulte [API de administración](admin-api).

## XML de metadatos pegado {#pasted-metadata-xml}

Algunos IdP no publican ninguna URL de metadatos (Google Workspace), o su endpoint de metadatos no es accesible desde el SP (ADFS en una red privada). En esos casos, pegue el documento de metadatos: proporcione `metadataXml` al crear o actualizar. Debe proporcionarse exactamente uno de `metadataLocation` o `metadataXml`; proporcionar uno en una actualización borra el otro.

Los metadatos pegados se validan al guardar y se **condensan** (`SamlMetadataParser.Condense`) en un `EntityDescriptor` mínimo canónico que contiene exactamente lo que consume el SP: el entityID, los certificados de firma, el endpoint de SSO, el endpoint de SLO si existe y el indicador `WantAuthnRequestsSigned`. Los documentos de los fabricantes pueden superar los 100 KB (`FederationMetadata.xml` de ADFS), por encima del límite de 64 KB por propiedad de Azure Table, mientras que las partes que usa el SP ocupan unos pocos KB. Los documentos pegados que no se pueden interpretar se rechazan con un 400; el documento debe contener un `IDPSSODescriptor` con un certificado de firma y un `SingleSignOnService`.

## Formato de NameID {#nameid-format}

El campo `nameIdFormat` controla el Format de `NameIDPolicy` que se solicita en el AuthnRequest:

| Valor | Comportamiento |
|---|---|
| omitido / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (el valor por defecto histórico) |
| `"none"` | Omite por completo el elemento `NameIDPolicy`. Es el ajuste seguro para ADFS: ADFS hace fallar todo el inicio de sesión (MSIS7070) cuando sus reglas de claims no emiten el formato solicitado. |
| cualquier otro valor | Se envía literalmente como URN de Format (debe empezar por `urn:`) |

En una actualización, `""` restablece el valor por defecto emailAddress. Los metadatos del SP anuncian el formato solicitado por la conexión (y omiten `NameIDFormat` cuando está establecido en `"none"`).

## Endpoints {#endpoints}

| Endpoint | Descripción |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Inicia el SSO iniciado por el SP. Construye un AuthnRequest (firmado cuando corresponde) y redirige al IdP. `loginHint` se pasa como `login_hint` a los IdP que lo respetan (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Recibe la respuesta SAML, la valida y crea al usuario o inicia su sesión. |
| `GET /saml/{connectionId}/metadata` | XML de metadatos del SP para configurar el IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout iniciado por el SP. Termina la sesión local y, después, envía un LogoutRequest al IdP cuando este admite SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint de Single Logout. Recibe los LogoutRequest iniciados por el IdP (binding Redirect o POST) y el tramo LogoutResponse del SLO iniciado por el SP. |

La URL de retorno tras el inicio de sesión se guarda en el servidor junto con el AuthnRequest almacenado (indexado por ID de solicitud), no en RelayState: la especificación SAML limita RelayState a 80 bytes y algunos IdP lo truncan. RelayState solo se consulta en los flujos iniciados por el IdP.

## Par de claves del SP y aserciones cifradas {#sp-keypair--encrypted-assertions}

Cada conexión creada mediante la API recibe un par de claves del SP generado automáticamente: un certificado RSA autofirmado de 2048 bits (con validez de 10 años), guardado como PKCS#12 y protegido en reposo por el proveedor de secretos del host. Solo existe en el servidor y la API nunca lo devuelve. El par de claves permite:

- **AuthnRequest firmados** (firma de consulta `SigAlg`/`Signature` en el binding Redirect). La firma se activa automáticamente cuando los metadatos del IdP declaran `WantAuthnRequestsSigned`, o siempre que la conexión establezca `signAuthnRequests: true`.
- **Descifrado de aserciones cifradas.** Cuando los metadatos del SP anuncian un certificado de cifrado, ADFS empieza a cifrar las aserciones por defecto; el ACS las descifra con la clave privada del SP y hace pasar la aserción descifrada por la misma cadena de comprobación de firma y condiciones que una en texto plano. Se admiten: transporte de claves RSA-OAEP (SHA-1/SHA-256); cifrado de datos AES-128/192/256-CBC y 3DES. **El transporte de claves RSA-1.5 se rechaza** (el desenvolvimiento PKCS#1 v1.5 es un oráculo de Bleichenbacher/ROBOT) y **AES-GCM no se admite** (limitación de `EncryptedXml` de .NET). Configure el IdP para RSA-OAEP y AES-CBC. Ambos fallos devuelven deliberadamente el mismo mensaje constante ("Could not decrypt the assertion."): nombrar el algoritmo o la etapa que falló es precisamente lo que construye el oráculo, así que haga el diagnóstico a partir de la configuración del IdP y no del error.
- **Mensajes de cierre de sesión firmados** (LogoutRequest/LogoutResponse en el binding Redirect).

Los metadatos del SP publican el certificado como `KeyDescriptor` tanto de `signing` como de `encryption`, y establecen `AuthnRequestsSigned="true"` cuando la conexión fuerza la firma.

## Single Logout {#single-logout}

El ACS registra la sesión SAML en la cookie de autenticación (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) para que el cierre de sesión pueda asociarse a la sesión del IdP.

- **Iniciado por el SP:** `GET /saml/{connectionId}/logout` siempre termina primero la sesión de la cookie local (el usuario pidió cerrar sesión; el SLO en el IdP se hace en la medida de lo posible). Si la sesión del navegador procede de esta conexión y los metadatos del IdP anuncian un `SingleLogoutService`, se envía un LogoutRequest (NameID + SessionIndex, firmado cuando el SP tiene clave) mediante el binding Redirect; el LogoutResponse del IdP vuelve a `/slo`, que lleva al usuario a la `returnUrl` almacenada. Con los IdP sin endpoint de SLO (Google), solo se cierra la sesión local.
- **Iniciado por el IdP:** el IdP envía un LogoutRequest a `/saml/{connectionId}/slo` (binding Redirect GET o POST). Las solicitudes firmadas se validan con los certificados de los metadatos del IdP. **Un LogoutRequest sin firmar o que no se puede verificar se rechaza con un 400** antes de consultar ninguna sesión. No hay recurso limitado a la sesión: una página de terceros que lleva hasta aquí el navegador de la *víctima* aporta la sesión de la víctima, no la del atacante, así que limitar ese recurso a la sesión actual no habría restringido a quién se podía desconectar. De todos modos, el §4.4.3.1 de Profiles exige que el IdP firme los LogoutRequest en los bindings Redirect y POST, y los metadatos de la conexión ya aportan los certificados, así que rechazar uno sin firmar no le cuesta nada a ningún IdP conforme. Se devuelve un LogoutResponse firmado cuando el IdP tiene un endpoint de SLO. Solo por front-channel: el mensaje llega al navegador del usuario, así que terminar la sesión de la cookie cierra la sesión exactamente de ese navegador.

## Caché de metadatos y renovación de certificados {#metadata-caching--cert-rollover}

- Los metadatos del IdP obtenidos de `MetadataLocation` se guardan en memoria durante 60 minutos (configurable mediante `Cache:SamlMetadataCacheMinutes`), indexados por la URL de los metadatos (no por el ID de conexión, así que no puede haber confusión de caché entre inquilinos).
- Los metadatos pegados se guardan en caché por contenido (hash del XML) y nunca se vuelven a descargar.
- **Nueva descarga ante un fallo de firma:** un fallo de validación de firma justo después de una renovación del certificado del IdP indica que los metadatos en caché están desactualizados. Ante ese fallo concreto, se expulsa la entrada de la caché y se vuelven a descargar los metadatos una vez, y después se reintenta la validación, con un periodo de espera de 5 minutos por ubicación de metadatos para que no se pueda usar una aserción basura para saturar el endpoint de metadatos del IdP. Sin esto, una renovación de certificado haría fallar los inicios de sesión hasta que caducara el TTL de la caché. (Solo para metadatos obtenidos por URL; en los pegados no hay nada que volver a descargar).

## Compatibilidad con Azure AD {#azure-ad-compatibility}

| Comportamiento de Azure AD | Tratamiento |
|---|---|
| Firma solo la aserción (por defecto) | Valida la firma del elemento Assertion |
| Firma solo la respuesta | Valida la firma del elemento Response |
| Firma ambas | Valida ambas firmas |
| SHA-256 (por defecto) | Admite SHA-256 y SHA-1 |
| NameID: emailAddress | Extracción directa del correo |
| NameID: persistent (opaco) | Recurre al claim de correo de los atributos |
| NameID: unspecified | Recurre al claim de correo de los atributos |
| NameID: transient | Cambia en cada inicio de sesión, así que nunca se usa como clave federada. En su lugar se usa el atributo de object-id estable del IdP; si no se afirma ninguno, el inicio de sesión se rechaza con un error que indica cómo resolverlo (configure un NameID persistent o emailAddress, o afirme un atributo de object-id). |

## Asignación de atributos {#attribute-mapping}

Los atributos se indexan sin distinguir mayúsculas y minúsculas tanto por su `Name` como por su `FriendlyName` (Okta y Shibboleth emiten Names de tipo OID con FriendlyNames legibles; aceptar cualquiera de los dos es lo que hace funcionar la asignación de cada fabricante). Cada campo prueba una lista de alias en orden; el primer alias es el URI de claim de Microsoft, de modo que el comportamiento con Entra/ADFS no cambia, y el resto cubre los nombres legibles y OID que Okta, OneLogin, Ping, Google y Shibboleth emiten por defecto:

| Campo | Nombres de atributo aceptados |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrevia el URI completo `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` o `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`).

Prioridad para resolver el correo: atributo de correo explícito (cualquier alias) → NameID cuando su formato es emailAddress → el claim `name` si contiene `@` → rechazo (el correo es obligatorio).

**Los grupos tienen varios valores:** se captura cada elemento `AttributeValue` (uno por cada pertenencia a un grupo), no solo el primero.

## Aprovisionamiento JIT {#jit-provisioning}

El aprovisionamiento JIT está **desactivado por defecto**. Una conexión con `jitProvisioningEnabled: true` crea automáticamente los usuarios desconocidos en su primer inicio de sesión (correo, nombre y apellidos tomados de la aserción, con el correo marcado como confirmado) y los vincula a la conexión mediante su identidad federada estable (`saml:{connectionId}` + NameID, o el object-id en el caso de NameID transient). Sin ello, un usuario desconocido se rechaza. Una conexión que declara `provisioningAttributeParams` exige además ese contexto de invitación en el inicio de sesión, salvo que esté establecido `allowUninvitedJit`; consulte [SSO de autoservicio](self-service-sso). Los usuarios que vuelven se identifican primero por el vínculo federado, nunca solo por el correo; una cuenta local existente solo se vincula por correo cuando el `AllowedDomains` de la conexión cubre el dominio de ese correo (la declaración explícita del administrador de que este IdP es dueño del dominio), lo que impide la apropiación de cuentas mediante un IdP malicioso.

## Vida útil de la sesión {#session-lifetime}

Si el `AuthnStatement` de la aserción lleva un `SessionNotOnOrAfter`, ese es el límite superior que el propio IdP fija para la sesión que acaba de establecer, y Authagonal lo respeta. La cookie de inicio de sesión caduca como muy tarde en ese instante (cuando queda dentro de 30 días), y el mismo límite viaja en la sesión como `session_max_exp`, que limita todos los tokens de acceso, de ID y de actualización emitidos a partir de ella. Una aserción sin `SessionNotOnOrAfter` no impone ningún límite adicional. SAML no tiene token de actualización del IdP de origen, así que esta es la única forma en que un IdP limita una sesión después del inicio de sesión; para las conexiones OIDC, consulte [Sesiones federadas](federated-sessions).

## Seguridad {#security}

- **Prevención de reproducción:** en los flujos iniciados por el SP, `InResponseTo` se valida frente a un ID de solicitud almacenado (de un solo uso). De forma independiente, el ID de cada aserción aceptada se almacena y se exige que sea de un solo uso, lo que también cubre las respuestas iniciadas por el IdP y las respuestas a las que se ha quitado `InResponseTo` (el ID de la aserción está dentro de la aserción firmada, así que no se puede alterar sin romper la firma).
- **Desfase de reloj:** tolerancia de 5 minutos en NotBefore/NotOnOrAfter
- **Antigüedad máxima de la aserción:** una aserción presentada más de una hora (más el desfase) después de su propio `IssueInstant` se rechaza diga lo que diga su `NotOnOrAfter`, y se rechaza un `IssueInstant` en el futuro
- **Emisor, destino y audiencia:** el `Issuer` de la Response y de la Assertion debe ser igual al entity ID del IdP de la conexión, una Response firmada debe llevar un `Destination` que coincida con la URL de este ACS, y la audiencia debe ser el entity ID del SP de esta conexión
- **Validez del certificado del IdP:** un certificado de firma del IdP fijado que esté fuera de su propia ventana `NotBefore`/`NotAfter` (con 5 minutos de desfase) se omite, tanto para las aserciones como para las firmas de cierre de sesión del binding Redirect, así que actualice los metadatos tras una renovación
- **Prevención de ataques de envoltura (wrapping):** el URI de Reference de la firma debe coincidir con el ID del elemento firmado
- **Prevención de redirecciones abiertas:** la URL de retorno tras el inicio de sesión debe ser una ruta relativa a la raíz (que empiece por `/`, sin `//` y sin barras invertidas, ya que los navegadores tratan `\` como `/`)
- **Garantía de dominio:** cuando se configura `AllowedDomains`, se rechazan las aserciones de correos fuera de esos dominios, de modo que una conexión no puede afirmar el dominio de otra ni el correo de un usuario local
- **MFA:** la federación solo demuestra el primer factor. Si la política efectiva del usuario exige MFA, el inicio de sesión pasa por el desafío o la configuración inicial de MFA local en lugar de emitir una sesión completamente autenticada, salvo que la conexión establezca `challengeMfaAfterLogin: false`.
