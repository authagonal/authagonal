---
layout: default
title: Actualizar un usuario
locale: es
---

# Actualizar un usuario (toma de posesión de una cuenta sin contraseña)

Algunas cuentas nacen sin contraseña:

- un **invitado** que abrió un enlace compartido y fue creado sobre la marcha por un inicio de sesión federado,
- un usuario **aprovisionado por JIT** mediante un inicio de sesión SSO o una invitación de organización,
- un usuario de directorio enviado mediante **SCIM**.

Cada uno es un usuario real de Authagonal (id estable, normalmente con el acceso descendente ya aprovisionado) que
simplemente no tiene credencial local. Su vía de entrada *fue* la federación, el enlace o la invitación.

**Actualizar** a un usuario así le permite establecer una contraseña propia y, normalmente, mejora al mismo tiempo su relación
con su producto (invitado → miembro estándar, prueba → de pago, «cree su organización»).
Authagonal lo incluye como un flujo de primer nivel y opcional: la persona vuelve a registrarse con el mismo correo, demuestra
que controla el buzón y toma posesión de su cuenta **existente** *en el mismo sitio* (mismo id de usuario, así que todo su
acceso anterior se conserva) mientras su aplicación ejecuta la lógica de actualización que necesite.

> Esto no es, a propósito, lo mismo que «restablecer la contraseña». Un restablecimiento de contraseña presupone una cuenta
> con credencial y envía por correo un enlace de restablecimiento. Una toma de posesión convierte una cuenta *sin credencial* en una con credencial y
> vuelve a ejecutar el aprovisionamiento, para que su sistema descendente pueda reaccionar a la promoción.

## Cuándo usarlo {#when-to-use-it}

Active el flujo de toma de posesión cuando un producto descendente considere que «alguien que se registra con el correo de una identidad federada»
es una vía de actualización legítima; el caso clásico es un invitado de un enlace compartido que decide crear una
cuenta real. Si su despliegue no tiene esa vía, déjelo desactivado (el valor por defecto): todo correo existente se
trata entonces como duplicado, y el registro devuelve la respuesta normal, neutra frente a la enumeración.

## 1. Activarlo {#1-enable-it}

La reclamación depende de una única opción opcional de la sección de configuración `Auth` (vinculada a `AuthOptions`):

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (por defecto `false`): activa el flujo.
- **`ClaimAllowedAttributeKeys`** (por defecto vacío = se permiten todas las claves no reservadas): una lista blanca de las
  claves de atributos personalizados que una toma de posesión puede trasladar a la cuenta (consulte
  [Transmitir el contexto de actualización](#4-pass-upgrade-context-safely)). Indique las claves que espera su aprovisionador para que una
  reclamación no pueda inyectar atributos arbitrarios. Pese a su nombre, la misma lista también filtra los
  `customAttributes` de un registro de autoservicio ordinario.

Con el indicador desactivado, un correo existente es un duplicado. Con él activado, una cuenta existente **sin credencial**
(sin `PasswordHash`) admite una toma de posesión; una cuenta que ya tiene contraseña **nunca** se toca: un nuevo
registro no puede sobrescribir una credencial real.

## 2. La toma de posesión, de principio a fin {#2-the-claim-end-to-end}

El usuario llama al endpoint de registro ordinario con el correo de la cuenta que quiere reclamar:

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

La respuesta es `201` con `{ "success": true, "userId": "..." }`. En la vía de toma de posesión, el `userId` es un
valor desechable, no el id real de la cuenta, así que la respuesta no sirve para distinguir una reclamación de un
alta completamente nueva.

Todavía no hay nada activo. El servidor **deja preparados** la contraseña y el perfil y los atributos, y envía por correo un
enlace de verificación nuevo. El usuario lo abre:

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

Ese `GET` solo muestra una página de confirmación de un clic (para que los escáneres de correo y los precargadores de enlaces que obtienen
la URL no consuman el token). **Pulsar el botón** de esa página envía
`POST /api/auth/confirm-email`, y eso es lo que promueve la credencial preparada y ejecuta la actualización. El mismo
`POST` acepta también el token como parámetro de consulta o en un cuerpo JSON (`{ "token": "..." }`); quien llama con JSON
recibe `{ "message": "Email confirmed successfully.", "appLink": ... }`, mientras que el envío del formulario desde la página
redirige a `/login?email_confirmed=1`. Después, el usuario inicia sesión con normalidad con su nueva contraseña.

### Qué hace el servidor {#what-the-server-does}

1. **Registro**: como la cuenta existe y no tiene contraseña, la solicitud se trata como una reclamación. La
   contraseña elegida se convierte en hash en `PendingPasswordHash` (inerte: ninguna vía de autenticación la lee), y el nombre y
   los apellidos, junto con los `customAttributes` de la lista blanca, quedan preparados en `PendingClaimJson`. El sello de seguridad
   de la cuenta se rota en ese mismo momento, lo que invalida cualquier enlace de verificación que ya esté en un
   buzón. Por lo demás, la cuenta **no** se modifica. Se envía un correo de verificación aunque el
   correo de la cuenta ya se hubiera confirmado en su flujo original: esa prueba anterior correspondía a *otro*
   actor, y la toma de posesión necesita la suya. El enlace lleva un resumen `pc=` de la credencial preparada para él.
2. **Confirmación**: la confirmación es la prueba de titularidad. El servidor comprueba que el enlace está ligado a la credencial preparada
   *actualmente*, aplica el perfil y los atributos preparados, ejecuta **`ReprovisionAsync`** (consulte la
   sección siguiente) y, a continuación, promueve `PendingPasswordHash` a `PasswordHash` y vuelve a rotar el sello de seguridad.
   Si el aprovisionamiento rechaza la actualización, la credencial y el perfil preparados se descartan y la
   cuenta sigue sin contraseña y se puede volver a intentar la toma de posesión, de modo que no persiste nada a medias.

Si se envía una segunda toma de posesión antes de confirmar la primera, sustituye a la credencial preparada y el
primer enlace deja de funcionar: confirmarlo responde `claim_superseded` (`400` en JSON, o una redirección a
`/login?error=claim_superseded` desde la página de confirmación). Un enlace sin resumen `pc=`, como el de
una acción de administración «enviar correo de verificación», también falla de este modo mientras haya una credencial preparada. En ambos casos,
el usuario pide un enlace nuevo volviendo a registrarse.

El id de usuario nunca cambia, así que el acceso a proyectos como invitado, la vinculación SCIM, la pertenencia a grupos y todo lo demás se conservan
tras la actualización.

## 3. Hacer la actualización en el sistema descendente {#3-do-the-upgrade-downstream}

La confirmación de una toma de posesión llama a `ReprovisionAsync`, que, a diferencia del aprovisionamiento normal, vuelve a ejecutar el
ciclo [TCC Try/Confirm/Cancel](provisioning) **incluso para las aplicaciones en las que el usuario ya está
aprovisionado**. Esa es precisamente la idea: su aplicación ya aprovisionó a este usuario como *invitado*, así que un
aprovisionamiento simple lo omitiría; el reaprovisionamiento le da un segundo Try, que ahora lleva el contexto del alta, para
que pueda promoverlo.

Su manejador `Try` de aprovisionamiento distingue el «primer aprovisionamiento» de la «actualización» según si ya tiene un
registro para ese `userId`, y reacciona al contexto que llevó la reclamación (aquí, `org_name`):

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

Un `approved: false` de cualquier aplicación (o un callback fallido) hace que la confirmación falle con `400 provisioning_rejected`
(un cuerpo JSON para quienes llaman a la API, o una redirección a `/login?error=provisioning_rejected&error_description=...` desde
la página de confirmación) y deja la cuenta sin actualizar, todavía sin contraseña y todavía reclamable. Un
registro que no es una toma de posesión y que una aplicación de aprovisionamiento rechaza es un `422`; consulte [Aprovisionamiento TCC](provisioning).
Un `organizationId` (o `customAttributes` adicionales) en la respuesta de aprobación se fusiona en el usuario y
viaja en sus tokens.

## 4. Transmitir el contexto de actualización de forma segura {#4-pass-upgrade-context-safely}

Los `customAttributes` de la llamada de registro son la forma en que la toma de posesión lleva el contexto del alta (nombre de la organización, plan,
referido) a su aprovisionador. Quedan **preparados**, se aplican solo al hacer clic en la verificación y se filtran según
`ClaimAllowedAttributeKeys`. Mantenga esa lista blanca ajustada: es el límite que impide que alguien que simplemente
*conoce* el correo de un usuario federado inyecte atributos que viajarían en los tokens del verdadero titular. Una
lista blanca vacía permite todas las claves no reservadas (práctico para flujos propios de confianza); una con contenido
descarta todo lo que no figure en ella.

Diga lo que diga la lista blanca, el filtro aplica siempre estos límites y descarta sin aviso todo lo que los
incumpla (el registro sigue teniendo éxito):

- como máximo 32 atributos, con claves de hasta 64 caracteres y valores de hasta 1024 caracteres;
- estas claves reservadas nunca se aceptan, aunque figuren en `ClaimAllowedAttributeKeys`: `federated_connection`,
  `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`, `amr`, `email`,
  `email_verified`.

El mismo filtro se aplica al registro de autoservicio ordinario (sin reclamación).

## Propiedades de seguridad {#security-properties}

- **Conocer el correo no basta.** La toma de posesión solo se completa cuando el propio buzón de la cuenta recibe y
  confirma el enlace de verificación. Un atacante que conoce la dirección nunca recibe el correo.
- **Una sola credencial preparada a la vez.** El enlace está ligado a la credencial preparada para él, así que una reclamación posterior
  no puede promoverse con un enlace anterior (`claim_superseded`).
- **Nunca se sobrescribe una credencial real.** Solo se puede tomar posesión de una cuenta sin `PasswordHash`; una toma de posesión
  sobre una cuenta con credencial recibe la respuesta normal de duplicado, neutra frente a la enumeración.
- **Nada está activo hasta la confirmación.** La contraseña preparada no puede autenticar, y el perfil y los
  atributos preparados no se aplican, hasta la confirmación. Una actualización rechazada lo revierte todo.
- **La inyección de atributos está acotada** por `ClaimAllowedAttributeKeys`.

## Relacionado {#related}

- [Aprovisionamiento TCC](provisioning): el contrato Try/Confirm/Cancel que implementa su manejador.
- [SSO de autoservicio](self-service-sso): los flujos JIT que crean, en primer lugar, las cuentas sin contraseña.
