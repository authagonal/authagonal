---
layout: default
title: SSO de autoservicio
locale: es
---

# Incorporación con SSO de autoservicio

Una vez que ha [federado una conexión](oidc-federation) con el IdP de un cliente, la siguiente
pregunta es: **¿qué ocurre cuando aparece alguien que nunca ha iniciado sesión?** Authagonal le ofrece
tres posturas ante ese usuario desconocido, de la más estricta a la más abierta (rechazar a todo usuario desconocido, exigir contexto
de invitación o aprovisionar automáticamente desde un dominio permitido), además de los controles para evitar que un IdP *externo* se convierta
en un arma contra usted mismo. Las dos primeras se tratan juntas en la postura 1, y la tercera en la postura 2. Esta guía trata de elegir y configurar la postura que quiera.

Todo ello es configuración por conexión: las secciones de configuración `OidcProviders` y `SamlProviders`, los
`OidcProviderConfig` / `SamlProviderConfig` almacenados y la API de administración. Los ajustes relevantes:

| Ajuste | Efecto | Protocolos |
|---|---|---|
| `JitProvisioningEnabled` | ¿Se puede crear un usuario desconocido? | OIDC, SAML |
| `ProvisioningAttributeParams` | Exige *contexto de invitación* en la solicitud antes de crearlo. | OIDC, SAML |
| `AllowUninvitedJit` | Permite la creación de autoservicio **sin** invitación (etiquetada con la conexión). | OIDC, SAML |
| `IsExternalConnection` | Marca un IdP de terceros para que no se puedan aplicar los indicadores reservados a conexiones propias. | Solo OIDC |
| `InteractionPath` | Muestra una página de la aplicación de inicio de sesión (nombre, condiciones) *antes* de federar. | Solo OIDC |

Importa dónde se puede establecer cada uno, porque la API de administración no los expone todos:

- **Carga desde la configuración (`OidcProviders`, `SamlProviders`):** todos los ajustes anteriores que existen para el protocolo. Las
  conexiones cargadas desde la configuración se vuelven a aplicar desde la configuración en cada arranque, así que en una conexión cargada desde la configuración
  `JitProvisioningEnabled` y `AllowUninvitedJit` proceden de la configuración, no de lo último que se haya almacenado.
- **API de administración de SAML** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` y `AllowUninvitedJit`.
- **API de administración de OIDC** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` e `InteractionPath`
  (que debe empezar por `/`). En OIDC, `ProvisioningAttributeParams`, `AllowUninvitedJit` e `IsExternalConnection`
  solo se pueden cargar desde la configuración, y no hay ruta de actualización para una conexión OIDC. Consulte
  [API de administración](admin-api) y [Federación OIDC](oidc-federation).

## Postura 1: solo por invitación (rechazar a quien no está invitado) {#posture-1-invite-only-reject-the-uninvited}

Es el valor por defecto. Con `JitProvisioningEnabled: false`, un usuario de SSO desconocido se rechaza directamente
(`access_denied`, "contact your administrator"), que es lo que quiere cuando todo usuario debe crearlo antes
un administrador o SCIM.

Si quiere JIT pero *solo* cuando hay una invitación, active JIT **y** declare
`ProvisioningAttributeParams`. Estos nombran los parámetros de consulta de `/authorize` de la lista blanca que llevan el contexto
de invitación (p. ej., `acceptKind`, `acceptToken`). Un usuario desconocido solo se aprovisiona cuando al menos uno de esos
parámetros ha llegado realmente con un valor; un inicio de sesión SSO sin invitación se rechaza con `access_denied`
("This login requires an invitation"), de modo que un inicio de sesión fortuito no puede autoaprovisionar sin aviso una cuenta u organización nueva.
Los parámetros se leen de la consulta de la URL `/authorize` a la que vuelve el usuario (el `RelayState` en
SAML); OIDC recurre además a la consulta de la propia solicitud de callback.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

No hay que establecer ningún `RedirectUrl`: el `redirect_uri` del callback se deriva en cada solicitud como
`{issuer}/oidc/callback`, así que registre ese URI en el IdP de origen. Un `RedirectUrl` indicado en la configuración se ignora.

Los parámetros capturados se guardan en los `CustomAttributes` del usuario JIT y llegan a su
[manejador `Try` de aprovisionamiento](provisioning), que es el verdadero control sobre los *valores* (p. ej.,
«¿corresponde este token de invitación a este correo?»). Authagonal captura las claves de la lista blanca; su aprovisionador decide
si son válidas. Si `Try` responde `approved: false`, el usuario recién creado se elimina y el navegador recibe
`400 provisioning_rejected`.

## Postura 2: autoservicio (aprovisionar automáticamente a un usuario de un dominio permitido) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

Para que «cualquier empleado de un cliente pueda simplemente iniciar sesión y obtener una cuenta», establezca `AllowUninvitedJit: true`. Ahora un
usuario desconocido de un **dominio permitido** se aprovisiona incluso sin contexto de invitación, y Authagonal lo etiqueta
con la conexión por la que llegó, para que su aprovisionador pueda ubicarlo en el inquilino correcto en lugar de
crear uno nuevo. La comprobación del dominio solo se aplica cuando `AllowedDomains` no está vacío: una conexión que
no indica ningún dominio acepta cualquier dominio que afirme su IdP, así que indíquelos en todas las conexiones de autoservicio.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

La etiqueta llega como atributo personalizado `federated_connection`. Su valor es el `ConnectionName` de la conexión
(no su `ConnectionId`), y solo se escribe cuando el usuario se creó sin contexto de invitación, de modo que un
usuario invitado lleva en su lugar los parámetros capturados. Su manejador `Try` se bifurca según ella:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` es opcional por conexión: una conexión que declara `ProvisioningAttributeParams` pero
**no** lo establece sigue siendo solo por invitación.

Antes de crear cualquier usuario desconocido se ejecutan dos comprobaciones más, sea cual sea la postura elegida:

- **El dominio no debe pertenecer a otra conexión.** Si el índice de dominios SSO encamina el dominio del correo del usuario
  a otra conexión, el inicio de sesión se rechaza con `access_denied` ("This email domain is
  managed by a different identity provider").
- **Solo OIDC: el IdP de origen debe haber verificado el correo.** Cuando el IdP de origen no indica `email_verified` como verdadero (leído del id_token o de la
  respuesta de userinfo cuando el correo procede de ahí), el inicio de sesión se
  rechaza con `access_denied`. Una aserción SAML no tiene ese indicador, así que SAML se apoya en `AllowedDomains`
  en su lugar.

`federated_connection` es un nombre de atributo reservado. Nunca se emite en un token, el claim del id_token con ese nombre que envíe un IdP de origen OIDC
se descarta, y el registro anónimo de autoservicio no puede establecerlo, de modo que solo los callbacks de
SSO pueden afirmar por qué conexión llegó una cuenta.

## Evitar que los IdP externos se vuelvan contra usted {#keep-external-idps-from-becoming-foot-guns}

Algunos indicadores de conexión OIDC son seguros en una conexión que controla **usted**, pero peligrosos en un IdP de terceros
cualquiera:

- **`UseUpstreamSubjectAsUserId`**: el IdP de origen elige el id del usuario local. En su propio proveedor de enlaces
  compartidos, eso mantiene alineados los ids; en el IdP de un cliente, permite que sea *él* quien elija sus ids de usuario.
- **`AutoLinkExistingByEmail`**: vincula un inicio de sesión federado a una cuenta local preexistente por correo,
  omitiendo la comprobación de titularidad del dominio. Con un buzón verificado y una conexión propia, no hay problema; en un IdP externo es una
  palanca para apropiarse de cuentas.

Marque las conexiones de terceros como **externas** y esos indicadores quedan neutralizados aunque estén establecidos:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` vale `false` por defecto (conexión propia), de modo que las conexiones existentes no se ven afectadas. Establézcalo en
todas las conexiones OIDC que apunten al IdP de otra parte; así, una configuración errónea posterior no podrá entregar a ese IdP
el control sobre las identidades locales. Las conexiones SAML no tienen ninguno de estos indicadores, así que en ellas no hay nada que neutralizar.
(Vincular una identidad federada a una cuenta preexistente sigue exigiendo además que el
`AllowedDomains` de la conexión respalde el dominio del correo: consulte
[Federación OIDC: Seguridad](oidc-federation)).

## Recopilar algo antes de federar {#collect-something-before-federating}

A veces necesita mostrar al usuario una página **antes** de enviarlo al IdP: el nombre visible de un invitado, una
casilla de condiciones, un selector de plan. `InteractionPath` (solo en conexiones OIDC) nombra una ruta de la aplicación de inicio de sesión que se muestra
primero:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

Cuando una solicitud `idp_hint={ConnectionId}` no autenticada llega a `/connect/authorize`, Authagonal redirige a
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` en lugar de ir directamente al IdP
(`LoginAppUrl` vale `/login` por defecto, y la ruta debe empezar por `/`). La misma redirección se produce cuando
se desafía automáticamente la conexión única, o la que coincide por dominio, de una [organización](#organisation-scoped-connections),
y cuando `prompt=login` fuerza una nueva autenticación a través de un `idp_hint`. Su página recopila lo que necesita,
añade los valores a la consulta de la `returnUrl` (de donde los leen `PassthroughParams` /
`ProvisioningAttributeParams`) y continúa ella misma hacia `/oidc/{id}/login`. Una página que
decide que no hace falta ninguna interacción puede continuar de inmediato.

## Conexiones limitadas a una organización {#organisation-scoped-connections}

Todo lo anterior describe una conexión **de nivel de inquilino**: una que comparte todo el inquilino y cuyo
`AllowedDomains` reclama un dominio de correo para todas las pantallas de inicio de sesión que sirve el inquilino. Es la forma
correcta cuando federa con un cliente por inquilino. Es la forma incorrecta cuando un inquilino sirve a muchas
[organizaciones](organizations) cliente y cada una trae su propio IdP: dos clientes no pueden reclamar ambos
`contoso.com`, y el botón «Continuar con Contoso Entra» de un cliente no tiene nada que hacer en la
pantalla de inicio de sesión de otro.

Establezca `OrganizationId` en una conexión y pasará a pertenecer a esa organización (al crearla y, en SAML,
al actualizarla, mediante la API de administración; una organización inexistente da `400 unknown_organization`):

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Cambian tres cosas, y nada más.

**Solo se ofrece cuando esa organización está seleccionada.** Una conexión limitada a una organización nunca aparece en
la pantalla de inicio de sesión del propio inquilino ni se llega a ella desde una solicitud que no se ha resuelto a ninguna organización,
ni siquiera una cuyo `login_hint` coincida exactamente con sus dominios.

**Sus dominios solo se comparan dentro de esa organización.** Una conexión limitada a una organización deliberadamente
*no* se escribe en el índice de dominios SSO de todo el inquilino, de modo que un dominio se puede reclamar una vez a nivel de inquilino y
una vez por organización. Un segundo intento de reclamarlo dentro de una misma organización se sigue rechazando con `domain_claimed`,
en ambos protocolos, para que una dirección no pueda encaminarse a dos IdP de una organización. Trasladar una conexión
a una organización elimina sus filas del índice; devolverla (enviando `"organizationId": ""` al endpoint de actualización
de SAML) las vuelve a registrar. Las conexiones OIDC no tienen ruta de actualización, así que su ámbito se establece al crearlas o
en la sección de configuración `OidcProviders`.

**Todo el que inicia sesión a través de ella es miembro de ella.** El ACS de SAML y el callback de OIDC asignan la
organización de la conexión como `org_id`, sustituyendo al `AuthUser.OrganizationId` propio de la cuenta (que
es un artefacto del aprovisionamiento descendente y no una afirmación sobre este inicio de sesión), y crean una
pertenencia activa si el usuario no tiene ninguna. Una pertenencia **invitada** se acepta: pasa a `active` y
conserva sus roles, quién invitó y cuándo se invitó, porque el propio IdP de la organización ya ha respaldado a la
persona. Cualquier otra pertenencia existente se deja tal cual: una fila `suspended` sigue suspendida, de modo que volver a iniciar sesión
no puede restablecer un acceso que un administrador revocó.

### A qué organización corresponde una solicitud, antes de que nadie inicie sesión {#which-organization-a-request-is-for-before-anyone-signs-in}

El descubrimiento del dominio de origen (home-realm discovery) tiene que responder a esto antes de que haya un usuario, así que se resuelve por separado del
[selector posterior a la autenticación](organizations#precedence) (aunque en el mismo orden):

1. El **parámetro `organization`** de la solicitud (un slug o un id).
2. **`OAuthClient.RestrictedToOrganizationIds`**, cuando contiene exactamente una entrada. Dos o más no son una
   selección: el cliente sirve a varias y la solicitud no nombró ninguna.
3. **`ITenantContext.OrganizationId`**: un host que fija una por solicitud, p. ej., un dominio personalizado
   por organización. `null` en todo despliegue de inquilino único.

La organización debe existir y estar habilitada, y la restricción del cliente debe permitirla. Cualquier otra cosa
se resuelve como *ninguna organización* y la solicitud continúa por la vía de todo el inquilino exactamente igual que antes. En
particular, un parámetro que nombra una organización a la que el cliente tiene vetado el acceso no se rechaza aquí:
el rechazo ya existe después de la autenticación (`access_denied`), y adelantarlo a la pantalla de inicio de sesión
cambiaría qué solicitudes puede distinguir quien llama sin autenticar.

### Qué hace `/connect/authorize` con ello {#what-connectauthorize-does-with-it}

Con una organización resuelta, y antes de cualquier regla de todo el inquilino:

- Un `idp_hint` que nombra una de **sus** conexiones va directamente a esa conexión. Eso incluye SAML, al que
  la vía de indicación de todo el inquilino (solo OIDC) no puede llegar. Una indicación que nombra cualquier otra cosa sigue de largo.
- **Exactamente una conexión y ningún `login_hint` que la contradiga** → directamente a ella. Una conexión que no indica
  dominios reclama toda la organización; una que indica dominios se sigue desafiando automáticamente, salvo que el
  dominio de la dirección indicada no figure entre ellos.
- **Varias conexiones** → el dominio del correo indicado elige entre ellas.
- **Ninguna coincidencia** → el comportamiento de `login_hint` y de la tarjeta de inicio de sesión de todo el inquilino, sin cambios.

Una federación que falló y volvió con `error=` en la consulta devuelve ese error a la parte
de confianza en lugar de volver a federar, de modo que un desafío automático no puede entrar en bucle.

### Qué ve la aplicación de inicio de sesión {#what-the-login-app-sees}

`/api/auth/providers` y `/api/auth/sso-check` aceptan ambos un parámetro de consulta `organization` (y recurren
a `ITenantContext.OrganizationId`), resuelto con las mismas reglas. Con una organización:

- `providers` muestra primero las conexiones de botón de **esa organización** y después las del propio inquilino. Una conexión
  solo es un botón cuando no indica `AllowedDomains` (y, en OIDC, tiene `ShowOnLogin` activado); a las encaminadas por dominio
  se llega primero por el correo, a través de `sso-check`. Las conexiones limitadas a una organización se excluyen por completo de la lista
  cuando no se ha resuelto ninguna organización, y las conexiones de otras organizaciones nunca se muestran.
- `providers` incorpora **`autoChallenge`** cuando la organización tiene exactamente una conexión: un registro de
  proveedor completo (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) de la conexión a la que la aplicación
  debe ir directamente, sin pasar por la tarjeta. Lleva el registro completo y no un simple id porque
  esa conexión puede estar encaminada por dominio u oculta y, por tanto, ausente de `providers`. En los demás casos el campo
  se omite, y es **orientativo**: `/connect/authorize` realiza el mismo desafío automático por su cuenta,
  así que una aplicación que lo ignore sigue llegando al mismo IdP.
- `sso-check` compara los dominios de las conexiones de la organización **antes** que el índice de todo el inquilino, y
  pasa a este cuando la organización no reclama nada para esa dirección. Una conexión única que no indica
  dominios reclama todas las direcciones.

### En la emisión de tokens {#at-token-issuance}

Una sesión establecida a través de una conexión limitada a una organización lleva esa organización como la fuente de
mayor prioridad después del valor que arrastra una actualización (por encima del parámetro `organization` y de la
restricción del cliente), porque es la única que se ha *demostrado*: el usuario se autenticó en un IdP que pertenece
exactamente a esa organización. Una solicitud que nombra otra se rechaza con `access_denied` en lugar de
emitirse discretamente para la otra. El `RequireMembershipForTokens` de la organización sigue aplicándose, y por eso
el callback crea la pertenencia.

## Relacionado {#related}

- [Organizaciones](organizations): los registros, las pertenencias, los claims y las reglas de selección.
- [Federación OIDC](oidc-federation): configurar la conexión y el modelo de seguridad.
- [Aprovisionamiento TCC](provisioning): el manejador `Try` al que llaman estos flujos.
- [Mantener sincronizadas las sesiones federadas](federated-sessions): revocar las sesiones locales cuando el IdP de origen lo hace.
