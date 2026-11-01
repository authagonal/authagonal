---
layout: default
title: Organizaciones
nav_order: 14
locale: es
---

# Organizaciones

Una organización es una empresa cliente dentro de su inquilino. Un mismo despliegue puede atender a muchas: cada una tiene su propia identidad, sus propios miembros y su propio `org_id` en los tokens que reciben sus aplicaciones.

## Descripción general {#overview}

Antes de las organizaciones, un registro de usuario llevaba una cadena `OrganizationId` (escrita por el aprovisionamiento TCC o por la vinculación de un token SCIM) y esa cadena se emitía como el claim `org_id`. No había ningún lugar donde indicar qué *era* la organización, quién pertenecía a ella ni si se podía autenticar en su nombre.

Un `Organization` le da un registro: un id opaco inmutable, un slug inmutable y único en el inquilino, un nombre para mostrar, un indicador de habilitación, un conjunto de metadatos y una sustitución de la personalización de marca. Un `OrganizationMembership` registra quién pertenece, y es lo que realmente autoriza la emisión de un token para esa organización.

**Para qué sirve.** Un ISV cuyo producto se despliega por empresa cliente (una instancia de la aplicación y una base de datos por empresa, resueltas por nombre de host) registra un inquilino y una organización por empresa cliente. Su aplicación lee `org_id` del token de acceso y rechaza todo lo que no corresponda a la instancia que está atendiendo. La decisión de enrutamiento que antes tomaba la propia aplicación la toma ahora el servidor de autorización, y la demuestra un claim firmado.

**El inquilino sigue siendo el límite de aislamiento.** Una clave de firma, un emisor, un almacén de usuarios. Una organización divide la identidad *dentro* de ese límite; no crea un segundo límite. Dos organizaciones de un mismo inquilino comparten el directorio de usuarios, y un usuario puede pertenecer a varias.

**Todavía no admitido:**

- **No hay selector de organización.** Un usuario que pertenece a varias organizaciones, en una solicitud que no nombra ninguna, recibe `account_selection_required`, un error al que la parte de confianza (RP) puede responder reintentando con un parámetro. No hay ninguna pantalla alojada que le pida elegir.
- **No hay administración delegada de organizaciones.** No existe ningún permiso que permita al propio administrador de una empresa cliente gestionar a sus miembros.
- **No hay aislamiento de SCIM con alcance de organización.** Un token SCIM vinculado a una organización (`ScimToken.OrganizationId`) etiqueta a los usuarios que crea y, cuando el id designa una organización real, los convierte en miembros activos (consulte [Pertenencia a partir de un token SCIM](#membership-from-a-scim-token)). Las comprobaciones de propiedad siguen basándose en el cliente OAuth, no en la organización, de modo que la vinculación decide el etiquetado, no el acceso.
- **No hay grupos con alcance de organización.** El claim `groups` y la pertenencia a grupos SCIM siguen siendo de todo el inquilino; solo los roles tienen alcance de organización.
- **No hay eventos de webhook de organización ni auditoría con alcance de organización.** `IAuthHook` no tiene eventos del ciclo de vida de las organizaciones (creación, concesión o revocación de pertenencia), las cargas existentes de los hooks no llevan `organizationId`, y el registro de auditoría no tiene ninguna columna ni índice de organización.
- **Los ámbitos restringidos por rol se filtran con los roles del inquilino en la autorización.** `Scope.AllowedRoles` se aplica en `/connect/authorize` contra los roles asignados directamente a la cuenta, antes de que se resuelva la organización, de modo que un ámbito cuyo `AllowedRoles` solo satisface un rol con alcance de organización se descarta en la autorización, o se rechaza con `access_denied` si no sobrevive ningún ámbito solicitado. En la actualización, el mismo control se ejecuta contra los roles del sujeto resuelto, que sí incluyen los de la organización. Hasta que ambos coincidan, restrinja los ámbitos con roles del inquilino.
- **No hay personalización de marca por organización en esta biblioteca.** `Organization.BrandingJson` se almacena para que el host lo combine sobre la personalización de marca del inquilino; esta biblioteca lo lee en un solo lugar, el correo de invitación ([Personalización de marca](branding#organisation-branding-in-invitation-emails)). La aplicación de inicio de sesión muestra el nombre de la organización ("Signing in to {name}") cuando la carga de arranque del host lleva un `organization` (`{ id, slug, name }`); la propia biblioteca no resuelve ninguna organización antes de la autenticación, salvo mediante el parámetro `organization`, una restricción de cliente con una sola entrada, una conexión limitada a una organización o el `ITenantContext.OrganizationId` de un host.

## Crear una organización {#creating-an-organization}

Las organizaciones se almacenan mediante `IOrganizationStore`. Se necesita una implementación persistente antes de poder crear ninguna. La implementación predeterminada integrada está vacía y es de solo lectura, y rechaza las escrituras con un mensaje que indica el registro que falta. Es deliberado: los registros de organización deciden la emisión de tokens, y un diccionario local del proceso seguiría emitiendo tokens en cada nodo que no hubiera visto una revocación.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` debe coincidir con `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`: de 1 a 64 caracteres de letras minúsculas, dígitos y guiones interiores, sin guion inicial ni final. En minúsculas porque el parámetro `organization` se pasa a minúsculas antes de buscar el slug, de modo que un slug con mayúsculas sería un valor que ninguna solicitud podría resolver nunca.

`Slug` debe ser único dentro del inquilino, y **los ids y los slugs comparten un mismo espacio de nombres**: un almacén rechaza una inserción o actualización cuyo slug ya tenga otra organización, e igualmente una cuyo slug sea igual al id de otra organización, o cuyo id sea igual al slug de otra. Dos registros que respondieran a un mismo valor harían que el parámetro `organization` designara una organización mientras todos los ids almacenados designan otra.

`Id` debe coincidir con `^[A-Za-z0-9._~-]{1,200}$`: la misma forma que el parámetro `organization`, de modo que cualquier id siempre se puede enviar como tal. Un id fuera de esa forma es un id que ninguna solicitud puede seleccionar, y un cliente restringido a él rechazaría todas las solicitudes.

`Id` **debería** contener además al menos un carácter que un slug no admite: una letra mayúscula, `.`, `_` o `~`. Los ids y los slugs comparten un mismo espacio de búsqueda, y un valor todo en minúsculas se resuelve primero como slug, de modo que un id que a su vez tiene forma de slug podría rechazarse más adelante en la creación porque alguien ha tomado ese slug, mientras que un id con un carácter no válido para un slug nunca puede. La forma recomendada para los ids nuevos es un valor opaco con el prefijo `org_` (`org_7f3a9c`): `_` no es válido en un slug, así que el prefijo por sí solo lo garantiza. No se impone ninguna convención, y los valores que ya hay en el campo son arbitrarios: proceden de la respuesta `/try` de TCC de una aplicación descendente (`TccProvisioningOrchestrator`) o de la vinculación de un token SCIM por parte de un operador (`ScimToken.OrganizationId`, que `ScimUserEndpoints` estampa en los usuarios nuevos).

En la práctica, tanto `Id` como `Slug` son inmutables. Las partes de confianza los comparan con la instancia que atienden y los dejarán fijados en el código, por lo que cambiar cualquiera de los dos provoca una interrupción del servicio sin ningún mensaje de error. `DisplayName` se puede cambiar libremente y es lo que muestra una pantalla.

## Conceder la pertenencia {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` es `invited`, `active` o `suspended`. **Solo `active` autoriza la emisión de tokens.** Suspender en lugar de eliminar conserva el registro de quién invitó a quién.

### Invitaciones {#invitations}

Una fila `invited` lleva `InvitedByUserId`, `InvitedAt` y los roles que se ofrecieron a la persona invitada. La API de administración escribe una cuando invita a alguien a una organización ([Invitar a personas](#inviting-people)), y un host también puede escribirla por su cuenta. La biblioteca promueve la fila a `active` (conservando los roles, quién invitó y la hora de la invitación, y estableciendo `JoinedAt`) en tres casos:

- **Al aceptar la invitación.** Establecer una contraseña mediante el enlace de invitación confirma la dirección, y el hook de activación de la biblioteca pasa a `active` cada pertenencia `invited` de ese usuario y escribe `organization.member.joined`.
- **Al iniciar sesión mediante una conexión SAML u OIDC limitada a una organización.** Que el propio IdP de la organización responda por la persona acepta la invitación (`FederatedOrganizationBinding`, 0.30.2). Sin esto, la barrera de pertenencia negaría un token a una persona invitada que solo inicia sesión mediante SSO.
- **Pertenencia automática** (más abajo), cuando el usuario cumple los requisitos.

Una fila `suspended` nunca se promueve por ninguna de estas vías y ningún inicio de sesión la modifica.

### Dominios verificados y pertenencia automática {#verified-domains-and-automatic-membership}

`Organization.Domains` contiene los dominios de correo electrónico que la organización ha reclamado, cada uno un `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` se almacena en minúsculas, sin espacios y sin punto final. La API de administración almacena el dominio reclamado, demuestra el control con un registro DNS TXT que lleva `VerificationToken` y establece `VerifiedAt` ([Verificación de dominios](#domain-verification)); un host con su propia comprobación puede establecer `VerifiedAt` por sí mismo.

`Organization.AllowAutoMembership` (desactivado por defecto) permite que un usuario se una sin invitación. Cuando la organización se selecciona **explícitamente** (una concesión de actualización heredada, el parámetro `organization`, una conexión limitada a una organización o un cliente restringido exactamente a esa organización) y el usuario no tiene una pertenencia activa, se le hace miembro si se cumplen **todas** estas condiciones:

- la organización está habilitada y `AllowAutoMembership` está activado,
- `AuthUser.EmailConfirmed` es true,
- la parte del correo electrónico que sigue a la última `@`, en minúsculas, es **exactamente** igual a un dominio con `VerifiedAt` establecido. Un `acme.com` verificado no admite a `user@eu.acme.com`.

Si no hay fila, se crea como `active` sin roles; una fila `invited` se promueve; una fila `suspended` nunca se toca. Se aplica en la autorización y en cada actualización, así que, mientras el indicador esté activado, eliminar la fila de un miembro que cumple los requisitos no lo mantiene fuera (vuelve a unirse con su siguiente token); suspéndalo en su lugar. Una organización heredada solo de `AuthUser.OrganizationId` nunca se une automáticamente. Cada unión automática se registra en el nivel Information.

### Pertenencia a partir de un token SCIM {#membership-from-a-scim-token}

Un token SCIM emitido con `organizationId` estampa ese valor como `org_id` en cada usuario que crea. `POST /api/v1/scim/tokens` rechaza con `400 unknown_organization` un `organizationId` que no designa ninguna organización (con `400 invalid_request` uno mal formado), igual que una conexión SSO, de modo que un token emitido mediante la API de administración nunca lleva una etiqueta que ninguna organización lista. Cuando el id designa una organización, la creación también escribe una pertenencia `active` sin roles y registra en la auditoría `scim.organization_member_added`. Un token que ya lleva un id que no designa nada se queda en una simple etiqueta. Solo en la creación: una sincronización posterior no vuelve a etiquetar ni añade miembros. Consulte [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Listar y eliminar {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` e `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` devuelven una página (`Items` y un `NextCursor` opaco, null en la última página). `limit` se limita al rango 1..200 y un cursor mal formado lanza `ArgumentException`. El cursor está basado en el conjunto de claves, por lo que una fila añadida o eliminada entre lecturas nunca desplaza ni repite una página. Ambos métodos tienen implementaciones predeterminadas sobre los listados sin paginar, de modo que un almacén personalizado sigue compilando; los almacenes de Azure Table los sobrescriben con una consulta de rango en el servidor.

Eliminar un usuario mediante el `DELETE /api/v1/profile/{userId}` de administración, el `DELETE /scim/v2/Users/{id}` de SCIM o la vía de recuperación de SCIM también elimina todas las pertenencias que tiene (`AccountArtefactPurge.PurgeAsync` con un `IOrganizationMembershipStore`; la sobrecarga de tres almacenes no purga ninguna). Un host con su propia vía de eliminación también debe pasar el almacén de pertenencias, o las organizaciones seguirán listando al miembro eliminado.

## Administrar organizaciones por HTTP {#administering-over-http}

La API de administración (`AdminApi:Enabled`, el ámbito de administración) gestiona organizaciones, pertenencias, dominios e invitaciones. La página [API de administración](admin-api#organizations) da cada solicitud, cada estado de éxito y cada código de error; en resumen:

| Rutas | Finalidad |
|---|---|
| `GET /api/v1/organizations`, `POST /api/v1/organizations` | Listar (paginado) y crear |
| `GET /api/v1/organizations/{idOrSlug}`, `PUT /api/v1/organizations/{id}`, `DELETE /api/v1/organizations/{id}` | Leer, actualizar, eliminar (los miembros se eliminan con ella) |
| `GET /api/v1/organizations/{id}/members`, `POST /api/v1/organizations/{id}/members`, `PUT /api/v1/organizations/{id}/members/{userId}`, `DELETE /api/v1/organizations/{id}/members/{userId}` | Listar, añadir, cambiar estado y roles, quitar |
| `POST /api/v1/organizations/{id}/domains`, `POST /api/v1/organizations/{id}/domains/{domain}/verify`, `DELETE /api/v1/organizations/{id}/domains/{domain}` | Reclamar, demostrar y liberar un dominio de correo |
| `POST /api/v1/organizations/backfill` | Convertir los valores heredados de `OrganizationId` en organizaciones |
| `GET /api/v1/profile/{userId}/organizations` | Las organizaciones a las que pertenece un usuario |
| `POST /api/v1/profile/invite`, `POST /api/v1/profile/{userId}/resend-invite` | Invitar a alguien por correo, reenviar la invitación |

Un fallo responde `{ "error": "<code>", "error_description": "..." }` con `400` (entrada no válida), `404` (no encontrado), `409` (conflicto) o `502` (`invite_email_failed`).

## Verificación de dominios {#domain-verification}

`POST /api/v1/organizations/{id}/domains` con `{ "domain": "acme.com" }` reclama el dominio, sin verificar, y responde `201` con el registro que hay que publicar: `recordName` (`_authagonal-org.acme.com`) y `recordValue` (`authagonal-org-verify=<token>`). El token son 256 bits aleatorios, uno por reclamación. Publique un registro TXT con ese nombre y exactamente ese valor, y luego llame a `POST /api/v1/organizations/{id}/domains/acme.com/verify`.

Una coincidencia establece `VerifiedAt` y escribe `organization.domain_verified`. Un fallo es `409 verification_failed`, nunca un `200` con `verified: false`, y su descripción detalla el nombre y el valor del registro que hay que publicar y lo que se encontró. Verificar un dominio ya verificado lo devuelve sin cambios. Un dominio pertenece como máximo a una organización, verificado o no (`domain_taken`). Quitar un dominio detiene las futuras incorporaciones automáticas y no expulsa a nadie.

La lectura de DNS pasa por `ITxtResolver` (`Authagonal.Core.Services`): `ResolveTxtAsync(name, ct)` devuelve los valores TXT publicados en un nombre, una lista vacía cuando no hay ninguno, y lanza una excepción cuando la consulta falla (se informa como `verification_failed`; el texto de la excepción nunca se devuelve). El valor predeterminado lee DNS real mediante DnsClient y une las cadenas de caracteres de un registro en un solo valor. Se registra con `TryAdd`, así que un resolvedor que registre antes de `AddAuthagonal()` (uno interno, DNS sobre HTTPS) prevalece.

## Backfill {#backfill}

Antes de las organizaciones, `AuthUser.OrganizationId` era texto libre. `POST /api/v1/organizations/backfill` agrupa a los usuarios por esa cadena exacta y crea una organización por cada valor distinto, con una pertenencia `active` (sin roles) para cada usuario. El slug es el valor en minúsculas con cada serie de otros caracteres reducida a un guion (`-2`, `-3` y así sucesivamente si hay una colisión), el nombre para mostrar es el propio valor y la organización queda marcada con la clave de metadatos `legacyOrganizationId`. El cuerpo es `{ "dryRun": true }`; **una ejecución en seco es el valor predeterminado**, también sin cuerpo alguno, y no escribe nada.

Ejecutarlo de nuevo es idempotente: una organización ya marcada para un valor se reutiliza, incluso tras un cambio de nombre, y un usuario que ya tiene una pertenencia se cuenta en `usersSkipped`. La respuesta incluye `dryRun`, `organizationsCreated`, `membershipsCreated`, `usersSkipped`, `organizations` (las primeras 50, cada una con `legacyValue`, `slug` y `users`) y `truncated`. Una ejecución examina como máximo 50 000 usuarios; `truncated: true` significa que hay que volver a ejecutarla. Una ejecución real escribe `organization.backfill`.

## Invitar a personas {#inviting-people}

`POST /api/v1/profile/invite` acepta `email` (obligatorio), `firstName`, `lastName`, `locale`, `organizationId` y `organizationRoles`. Crea la cuenta de inmediato, sin confirmar y sin contraseña utilizable, y envía un enlace por correo; la respuesta es `200` con `{ "userId", "email" }`. Con `organizationId` también escribe una pertenencia `invited` con los roles, `InvitedByUserId` (el administrador que llama) e `InvitedAt`, y el correo nombra la organización y lleva su marca ([Personalización de marca](branding#organisation-branding-in-invitation-emails)). Todo lo relativo a la organización se comprueba antes de que exista la cuenta, de modo que una invitación rechazada no deja un usuario huérfano. Solo se pueden invitar cuentas nuevas: una dirección existente es `409 user_already_exists`, y un usuario existente se incorpora mediante `POST /api/v1/organizations/{id}/members`.

El enlace lleva un grant `password_reset` de un solo uso que dura siete días y que canjea el `POST /api/auth/reset-password` existente; no hay un segundo endpoint de canje. Establecer la contraseña confirma la dirección y dispara `IAuthHook.OnEmailConfirmedAsync`, y el hook de activación de la propia biblioteca pasa a `active` cada pertenencia `invited` del usuario (conservando los roles, quién invitó y la hora de la invitación, y estableciendo `JoinedAt`) y escribe `organization.member.joined`, con el nuevo miembro como actor. Una fila `suspended` o `active` no se toca, un fallo del almacén se registra y nunca hace fallar la confirmación, y el hook es idempotente. Se ejecuta allí donde la biblioteca dispara `OnEmailConfirmedAsync`, sean cuales sean los hooks que registre su host.

`POST /api/v1/profile/{userId}/resend-invite` genera un enlace nuevo y lo envía: `404 user_not_found`, `400 already_accepted` una vez confirmada la dirección (use en su lugar un restablecimiento de contraseña) y `502 invite_email_failed` cuando el envío lanza una excepción. La primera invitación es más indulgente: un envío fallido se registra y el usuario y el enlace existen, porque la invitación se puede reenviar. Una invitación reenviada nombra la organización cuando el usuario tiene exactamente una pertenencia `invited`.

El correo pasa por `IEmailService.SendUserInviteEmailAsync(toEmail, tenantName, acceptUrl, locale, ct, organization)`, un miembro de interfaz con implementación predeterminada cuyo cuerpo envía el correo de restablecimiento de contraseña con el enlace de aceptación, de modo que un host con su propio `IEmailService` sigue entregando un enlace que funciona; sobrescríbalo para enviar una invitación propia. El remitente integrado redacta la invitación en el idioma del administrador que invita cuando la biblioteca lo incluye (el primer `Accept-Language` que reconoce, si no el `locale` de la solicitud), si no en el de la persona invitada y, si no, en inglés.

`IInviteLinkBuilder` (`Authagonal.Core.Services`) decide a dónde apunta el enlace: `Task<string> BuildAcceptUrlAsync(string token, Organization? organization, CancellationToken ct = default)`. El valor predeterminado devuelve `{issuer}/accept-invite?p=<token>`, con `&organization=<slug>` para una invitación a una organización, ambos con escape. Registre el suyo antes de `AddAuthagonal()` (se añade con `TryAdd`) para servir la página en otro sitio, como el dominio propio de una organización; la invitación y el reenvío construyen todos los enlaces a través de él.

## La página de aceptación de invitaciones {#the-accept-invite-page}

La aplicación de inicio de sesión sirve `/accept-invite` en la raíz del host, fuera de la ruta base `/login`, y el fallback de SPA del servidor independiente la sirve. Lee el token de `p`, nombra la organización cuando el enlace la lleva, pide una contraseña nueva dos veces, la comprueba con la política de contraseñas y la envía a `/api/auth/reset-password`. Un enlace sin token, o con un token desconocido o caducado, muestra un mensaje de «no válido o caducado» y ningún formulario; una contraseña que el servidor rechaza (`weak_password`) muestra el mensaje del servidor. Los textos están en los once idiomas. Un host basado en el paquete npm importa `AcceptInvite` de `@authagonal/login` y lo monta en `/accept-invite`.

## Llamar a los servicios desde código {#calling-the-services-from-code}

Los endpoints son delgados: `OrganizationOperations` y `UserInviteOperations` (`Authagonal.Server.Services`, registrados con ámbito y `TryAdd`) poseen cada regla, cada código de error y cada fila de auditoría, de modo que una superficie propia del host (una ruta de portal, una herramienta) puede llamarlos y escribir las mismas filas. `OrganizationOperations` tiene `ListPageAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`, `ListMembersPageAsync`, `AddMemberAsync`, `UpdateMemberAsync`, `RemoveMemberAsync`, `AddDomainAsync`, `VerifyDomainAsync`, `RemoveDomainAsync`, `ListUserOrganizationsAsync` y `BackfillAsync`; `UserInviteOperations` tiene `InviteAsync` y `ResendInviteAsync`. Reciben el actor de auditoría como cadena y ningún tipo HTTP, y quién puede llamarlos (quién puede invitar a una organización, con qué frecuencia se puede reenviar una invitación) es política del host.

Cada una devuelve `OperationResult<T>`: `Outcome` (`Ok`, `Invalid`, `NotFound`, `Conflict` o `Failed`), `Value`, `Error` (el código), `Message`, `Fields` y `Succeeded`. La API de administración asigna los resultados a `400`, `404`, `409` y `502`. Una lista devuelve `ResultPage<T>` (`Items` y `NextCursor`). `ListUserOrganizationsAsync` y `BackfillAsync` devuelven su valor directamente.

## Acciones de auditoría {#audit-actions}

Cada escritura pasa por `IAuditLogger`, con el tipo de entidad `organization` (`user` en las dos últimas) y el sujeto del administrador que llama como actor.

| Acción | Se escribe cuando | Detalle |
|---|---|---|
| `organization.created` | se crea una organización | el slug |
| `organization.updated` | se actualiza una organización | el slug |
| `organization.deleted` | se elimina una organización | el slug |
| `organization.member.added` | se añade un miembro | `user=<userId>` |
| `organization.member.updated` | cambian el estado o los roles de un miembro | `user=<userId>` |
| `organization.member.removed` | se quita un miembro | `user=<userId>` |
| `organization.member.invited` | una invitación crea la pertenencia `invited` | `user=<userId>` |
| `organization.member.joined` | una pertenencia `invited` pasa a `active` al confirmar el correo (el actor es el usuario) | `user=<userId>` |
| `organization.domain_added` | se reclama un dominio | `domain=<domain>` |
| `organization.domain_verified` | se demuestra un dominio | `domain=<domain>` |
| `organization.domain_removed` | se libera un dominio | `domain=<domain>` |
| `organization.backfill` | un backfill que no es una ejecución en seco | `organizationsCreated=<n> membershipsCreated=<n>` |
| `user.invited` | se invita a alguien | la dirección con la parte local enmascarada (`***@acme.com`) |
| `user.invite_resent` | se reenvía una invitación | lo mismo |

## Roles con alcance de organización {#organization-scoped-roles}

`OrganizationMembership.Roles` contiene los roles que tiene un usuario **dentro** de esa organización. Los nombres proceden del catálogo de roles existente del inquilino: un ISV declara "Auditor" una vez, y cada empresa cliente lo concede a su propia gente.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

Se unen en el claim `roles` junto con los roles asignados directamente al usuario y los que conceda la pertenencia a grupos SCIM, bajo el mismo control del ámbito `roles`. Un servidor de recursos no necesita saber si un rol se concedió para todo el inquilino o por organización, pero **sí** tiene que leer `org_id` junto con `roles`, porque el mismo nombre de rol significa ahora "en esta organización".

Cuatro reglas lo delimitan:

- **Solo una organización seleccionada explícitamente aporta roles**: una designada por el parámetro `organization` o por una restricción de cliente con una sola entrada. Una organización heredada de `AuthUser.OrganizationId` no aporta ninguno, la misma asimetría que tiene la barrera de pertenencia.
- **Solo aporta una pertenencia `active`.** Un miembro invitado que no ha aceptado o un miembro suspendido no concede nada, exactamente igual que no autoriza nada.
- **Los roles nunca cruzan organizaciones.** Se leen de la fila de pertenencia con la clave de la organización seleccionada, de modo que un rol que se tiene en una no puede llegar a un token emitido para otra.
- **Los prefijos reservados se eliminan.** Un rol que empieza por `tenant:` o `platform:` se descarta en la unión y se registra en el nivel Warning. Una fila de pertenencia son datos con alcance de la empresa cliente, así que una pertenencia capaz de conceder `tenant:admin` convertiría "puede gestionar mi propia organización" en "puede administrar el inquilino". Los roles asignados directamente y las asignaciones de grupo→rol de SCIM no se ven afectados: los escribe un operador a través de una superficie de administración autenticada, que es la autoridad que una fila de pertenencia no tiene.

Los roles se vuelven a leer de la fila de pertenencia en cada rotación de actualización, de modo que cambiarlos llega a una sesión activa en su siguiente actualización.

Los roles de todo el inquilino se **unen con** los de la organización, no se sustituyen por ellos: `tenant:admin` es autoridad del portal y se mantiene al seleccionar una organización.

## Seleccionar una organización en una solicitud de autorización {#selecting-an-organization-on-an-authorization-request}

Envíe `organization` con el slug o el id de una organización:

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

El valor debe coincidir con `^[A-Za-z0-9._~-]{1,200}$` (el conjunto de caracteres no reservados de RFC 3986); cualquier otra cosa produce `invalid_request`. Cómo se resuelve depende de sus mayúsculas y minúsculas:

- **Cualquier carácter en mayúscula → se resuelve solo como id, de forma exacta.** Los slugs solo admiten minúsculas, así que ese valor no puede ser uno. Pasarlo a minúsculas y consultar de todos modos el índice de slugs equivaldría a preguntar "¿es el slug de alguna organización la forma en minúsculas de este id?", y si lo fuera, a quien nombra un id se le entregaría otra empresa cliente.
- **Todo en minúsculas → primero como slug y después como id.** Podría ser cualquiera de los dos, y lo que suele enviar una parte de confianza es un slug. No hay ambigüedad porque un almacén no permite que un id y un slug compartan un valor.

`org_slug` y `org_id` se aceptan como alias: ambos se usan en otros proveedores, y que el servidor ignore sin aviso el que no eligió es peor que aceptar los dos. Enviar dos que designen organizaciones *diferentes* se rechaza con `invalid_request`: la solicitud significa dos cosas y, eligiera el servidor la que eligiera, a la parte de confianza se le habría comunicado la otra. Repetir cualquiera de los tres se rechaza por el mismo motivo que `redirect_uri`.

El parámetro sobrevive al viaje de ida y vuelta por la interfaz de inicio de sesión, porque toda la URL de autorización viaja como `returnUrl`. También funciona con [Solicitudes de autorización enviadas (PAR)](par) sin trabajo adicional: el endpoint PAR almacena todos los campos que recibe, y `/connect/authorize` lee la carga enviada en lugar de la cadena de consulta.

### Precedencia {#precedence}

La organización se resuelve en este orden:

1. **En una actualización, la organización para la que se emitió la concesión.**
2. **La organización para la que una [conexión de SSO limitada a una organización](self-service-sso#organisation-scoped-connections) autenticó esta sesión.** La única fuente aquí que se ha *demostrado* en lugar de afirmarla quien llama: el usuario inició sesión en un IdP que pertenece exactamente a una organización. Una solicitud que nombre otra se rechaza con `access_denied` en lugar de emitirse sin aviso para la otra.
3. **El parámetro `organization`.**
4. **`OAuthClient.RestrictedToOrganizationIds`, cuando tiene exactamente una entrada.** Una aplicación por empresa cliente nombra su organización una sola vez, en el registro, y su parte de confianza nunca envía ningún parámetro. Esta es la forma que quieren la mayoría de los productos con una instancia por empresa cliente.
5. **`AuthUser.OrganizationId`**: la organización almacenada en la propia cuenta.

Las reglas 1-4 son selecciones *explícitas* y deben cumplir la pertenencia. La regla 5 no lo es: el propio registro de la cuenta es la afirmación de pertenencia, y exigir una segunda dejaría fuera a todos los usuarios preexistentes en el momento en que se creara la organización correspondiente.

Antes de que nadie se haya autenticado (descubrimiento del dominio de origen, la lista de proveedores de la página de inicio de sesión, `/sso-check`), no hay usuario ni concesión, por lo que las reglas 3 y 4 y, después, `ITenantContext.OrganizationId` se resuelven por sí solas. Consulte [Conexiones limitadas a una organización](self-service-sso#organisation-scoped-connections).

## Restringir un cliente a una organización {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Cada entrada debe coincidir con la forma de id de organización `^[A-Za-z0-9._~-]{1,200}$`; la API de administración responde `400 invalid_request` ante una entrada vacía o mal formada, porque una restricción que lista un id que ningún parámetro `organization` puede enviar no coincide con nada, y una restricción que no coincide con nada rechaza todas las solicitudes. Una lista `null` se normaliza a vacía.

Vacía (lo que tienen todos los clientes existentes) significa sin restricción. Una solicitud cuya organización no está en la lista se rechaza con `access_denied`. Una lista de un elemento también selecciona, según la regla 4 anterior. Una lista de varios restringe pero no selecciona: la solicitud debe seguir nombrando una, o se rechaza con `account_selection_required`.

## Los claims {#the-claims}

Tanto en el token de ID como en el token de acceso:

| Claim | Valor | Ámbito |
|---|---|---|
| `org_id` | `Organization.Id` | ninguno; siempre presente cuando el sujeto tiene una organización |
| `org_slug` | `Organization.Slug` | ninguno; siempre presente cuando la organización es un registro real |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` y `org_slug` no dependen de ningún ámbito, deliberadamente.** Son contexto de autorización, no datos de perfil: indican para qué empresa cliente puede actuar el token, que es lo primero que comprueba un servidor de recursos con varias empresas clientes, antes de haber decidido si le interesa un nombre, y a menudo con un token que no solicitó ningún perfil. Con un control por `profile`, un cliente solo de API que pedía únicamente `openid` recibía un token sin ninguna organización, lo que se lee como "no pertenece a nadie": el servidor de recursos o bien rechaza a quien llama legítimamente, o bien trata el token como sin alcance y sirve con él los datos de todas las empresas clientes. El segundo fallo es silencioso, y es el que importa.

Emitirlos sin control no revela nada que el cliente no hubiera establecido ya: eligió la organización, o está restringido a una. `org_name` mantiene el control por `profile` porque es presentación, y nada debería autorizarse en función de él.

Una cuenta sin organización no emite ninguno de los tres, de modo que un token que antes no llevaba claims de organización tampoco los lleva ahora.

Los tres están reservados: ni la lista `UserClaims` de ningún ámbito ni ningún atributo de usuario personalizado pueden producirlos ni sobrescribirlos. Esto importa sobre todo para `org_slug`, que es la clave estable que una parte de confianza compara con la instancia de la empresa cliente que atiende. Uno autoafirmado sería la respuesta a esa comparación.

Una cuenta que lleva un id de organización que no corresponde a ningún registro emite solo `org_id`. La ausencia de `org_slug` significa "no hay slug", nunca "se ha ocultado".

**Valide `org_id` en su aplicación:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, introspección e intercambio de tokens {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** responde `org_id`, `org_slug`, `org_name` y `roles` a partir del **token presentado**, no del registro del usuario. `org_id` y `org_slug` se devuelven siempre que el token los lleve, sin control por ámbito, por el mismo motivo por el que no tienen control en el propio token; `org_name` necesita `profile`. Es la única fuente que puede acertar cuando un usuario puede pertenecer a varias organizaciones: la cuenta lleva un valor predeterminado, mientras que el token nombra la organización para la que se emitió realmente la concesión. Los campos de perfil (`email`, `name`, `phone_number`) siguen siendo en vivo: son los datos actuales del sujeto, que es para lo que sirve userinfo.

Así, volver a etiquetar una cuenta no cambia lo que userinfo dice sobre un token ya emitido, y a un usuario que ha iniciado sesión en la organización B nunca se le comunica `org_id` A desde el mismo servidor que puso B en su token de ID.

**`/connect/introspect`** incluye `org_id` y `org_slug` cuando el token los lleva. Un servidor de recursos que valida el JWT por sí mismo los lee del token; uno que usa la introspección obtiene ahora la misma respuesta.

**El intercambio de tokens de RFC 8693** traslada `org_id`, `org_slug` y `org_name` del token del sujeto al token intercambiado, y aplica sobre ellos el `RestrictedToOrganizationIds` del cliente **que realiza el intercambio**: un cliente registrado para atender a una empresa cliente no puede intercambiar el token de otra, ni puede intercambiar un token que no lleve ninguna organización. Como `org_id` no depende de ningún ámbito, esa comprobación también funciona con un token de servidor de recursos emitido sin el ámbito `profile`: con el control anterior, ese token parecía no atribuido, y a un cliente restringido se le rechazaba su propio tráfico. Un rechazo es `invalid_target`, en consonancia con los demás rechazos por política de destino en esa vía. Un intercambio es una proyección de una sesión existente, y una proyección que perdiera la organización en cuyo nombre actuaba quedaría sin atribuir en lugar de ser más restringida. El `ITokenExchangeSubjectTransformer` de un host puede volver a vincular deliberadamente el intercambio a otra organización (para eso sirven los intercambios vinculados a un contexto), pero tiene que hacerlo de forma explícita.

## Actualización {#refresh}

La organización para la que se emitió una concesión se traslada a través de cada rotación de actualización, y se vuelve a comprobar en cada una. Por lo tanto, tres cosas surten efecto en la siguiente rotación en lugar de esperar a que termine la duración de la actualización:

- revocar o suspender una pertenencia,
- deshabilitar una organización (`Enabled = false`),
- reducir el `RestrictedToOrganizationIds` de un cliente.

**Cada una rechaza la actualización; ninguna revoca la concesión.** El token de actualización presentado no se consume y la familia se mantiene intacta, de modo que la cadena sigue siendo rechazable mientras se mantenga la condición y se reanuda en cuanto deja de cumplirse: restaurar una pertenencia, o volver a habilitar una organización, recupera la sesión sin un nuevo inicio de sesión. La concesión sigue caducando según su propia duración absoluta. Es el mismo comportamiento que con un usuario desactivado, cuyas actualizaciones se rechazan mientras `IsActive` es false.

Para terminar de verdad una sesión, revoque la concesión: `POST /connect/revocation` con el token de actualización, o `GrantRevocation` en el lado del host. Deshabilitar una organización es un control, no una revocación.

Una concesión que simplemente heredó la organización de la cuenta se vuelve a derivar en cada rotación, de modo que volver a etiquetar una cuenta sigue surtiendo efecto.

**Cambiar de organización es una nueva solicitud de autorización**, no una actualización. Envíe de nuevo `/connect/authorize` con otro `organization`; la sesión existente se reutiliza, por lo que no hay un segundo inicio de sesión, y comienza una nueva concesión. No espere que el endpoint de actualización cambie de organización: no tiene agente de usuario ni consentimiento, y la concesión registra los ámbitos aprobados para la organización para la que se emitió.

## Desactivar la barrera de pertenencia {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

Activada por defecto. Desactívela en un despliegue que use las organizaciones para la personalización de marca y el enrutamiento y no para el acceso: cualquiera que pueda nombrar la organización recibe entonces un token para ella. Una organización cuya pertenencia es meramente indicativa no es un límite; tome esa decisión de forma deliberada.

## Rechazar una emisión desde un hook del host {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` se dispara inmediatamente antes de que las concesiones `authorization_code`, `refresh_token` y `device_code` emitan nada, con el sujeto resuelto:

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Lanzar una excepción rechaza la emisión con `access_denied` y el mensaje de la excepción como `error_description`; lanzar en su lugar una `ProtocolTokenException` permite indicar su propio error OAuth. En la vía de actualización, el control se ejecuta **antes** de la rotación, de modo que un rechazo deja sin consumir el token de actualización presentado y la familia intacta: "ahora no" no es "termine esta sesión".

Es un miembro de interfaz predeterminado, por lo que un `IAuthHook` existente que no lo sobrescriba no se ve afectado. Las dos emisiones agénticas (`client_credentials` e intercambio de tokens, cada una con un perfil de agente) lo disparan exactamente igual que antes.

## Rechazos {#refusals}

| Condición | Error |
|---|---|
| Dos selectores que designan organizaciones diferentes | `invalid_request` |
| Cualquier selector repetido | `invalid_request` (se entrega directamente, no se refleja a `redirect_uri`) |
| La organización designada no existe | `access_denied` |
| La organización está deshabilitada | `access_denied` |
| El cliente no está permitido para esta organización | `access_denied` |
| El usuario no es miembro activo (selección explícita) | `access_denied` |
| El cliente atiende a varias organizaciones y la solicitud no nombró ninguna | `account_selection_required` |

## Flujo de dispositivo {#device-flow}

La concesión de dispositivo no tiene una solicitud de autorización que pueda llevar un parámetro, así que recurre a la restricción del cliente y, después, al valor predeterminado de la cuenta. Un cliente de dispositivo que deba quedar fijado a una organización debe registrarse con un `RestrictedToOrganizationIds` de una sola entrada.

## Actualizar un despliegue existente {#upgrading-an-existing-deployment}

Nada cambia hasta que existe una organización. Sin registros:

- ninguna solicitud puede seleccionar una organización,
- no interviene ninguna barrera de pertenencia,
- una cuenta que lleva un `OrganizationId` heredado sigue emitiendo `org_id` a partir del registro de usuario, exactamente igual que antes,
- un token para un usuario sin organización no lleva ninguno de los tres claims.

Una vez que cree organizaciones, conceda las pertenencias **antes** de apuntar un cliente o una parte de confianza a una de ellas: una selección explícita exige una pertenencia activa, y se rechazará a una empresa cliente cuyos usuarios tengan registros pero no pertenencias.
