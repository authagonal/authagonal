---
layout: default
title: Inicio
locale: es
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticación OAuth 2.0 / OpenID Connect / SAML 2.0 para .NET, con almacenamiento intercambiable: su propio PostgreSQL o SQLite, Azure Table Storage o AWS (DynamoDB / S3 / Secrets Manager).

Un único despliegue autocontenido. El servidor y la interfaz de inicio de sesión se distribuyen como una sola imagen Docker, y la SPA se sirve desde el mismo origen que la API, de modo que la autenticación por cookies, las redirecciones y la CSP funcionan sin la complejidad de los orígenes cruzados.

> **¿Prefiere un servicio gestionado?** [Authagonal Cloud](https://authagonal.io) ejecuta todo esto por usted, en modo multiinquilino, con todas las funciones en todos los planes y sin cargos de SSO por conexión. → [authagonal.io](https://authagonal.io)

## Funciones principales {#key-features}

- **Proveedor OIDC**: concesiones authorization_code + PKCE, client_credentials, refresh_token y device_code, con rotación de un solo uso
- **SP SAML 2.0**: implementación propia con compatibilidad completa con Azure AD (respuesta firmada, aserción firmada o ambas), un par de claves de SP por conexión para AuthnRequests firmadas + descifrado de `EncryptedAssertion`, y Single Logout (iniciado por el SP y por el IdP)
- **Federación OIDC dinámica**: conexión con Google, Apple, Azure AD o cualquier IdP compatible con OIDC
- **Autenticación multifactor**: TOTP, WebAuthn/passkeys, códigos de recuperación; política por cliente (`Disabled` / `Enabled` / `Required`) con anulación por usuario mediante `IAuthHook`, aplicada también a los inicios de sesión federados
- **Aprovisionamiento SCIM 2.0**: aprovisionamiento entrante de usuarios y grupos desde Entra ID, Okta y OneLogin; listado paginado por cursor y filtros `eq` respaldados por índice ciego
- **Pantalla de consentimiento OAuth**: consentimiento por cliente con nueva solicitud según los ámbitos y gestión de concesiones
- **Device Authorization Grant**: flujo RFC 8628 para dispositivos con entrada limitada (televisores inteligentes, CLI, IoT)
- **Introspección de tokens**: RFC 7662 para que los servidores de recursos verifiquen la validez de un token
- **Firma de tokens**: solo ES256. Los tokens de acceso llevan el `typ: at+jwt` de RFC 9068 para que un servidor de recursos
  pueda distinguirlos de los id_tokens y de los tokens de cierre de sesión, pero **no se declara conformidad con RFC 9068**: §2.1
  exige RS256 entre los algoritmos admitidos, y este servidor no lo emite ni lo acepta. Un
  único algoritmo es una postura deliberada: cada algoritmo adicional aceptado es otra vía para que a un
  verificador se le convenza de usar el equivocado.
- **Back-Channel Logout**: notificaciones de OIDC Back-Channel Logout 1.0 a las partes de confianza
- **Sesiones del lado del servidor** *(opcional)*: `AddAuthagonalServerSideSessions` guarda el ticket de SSO en el almacenamiento, de modo que la cookie de autenticación solo lleva un id opaco, y habilita el listado de autoservicio `GET /api/auth/sessions` y la revocación por dispositivo ([API de autenticación](auth-api#sessions-self-service))
- **Backend-for-Frontend**: `Authagonal.Bff` (.NET) y `@authagonal/bff` (Node), un BFF de cliente confidencial para que una SPA nunca tenga un token ([BFF](bff))
- **Autoservicio RGPD** *(Authagonal Cloud)*: exportación de datos y eliminación programada de la cuenta desde la página
  de cuenta alojada. La aplicación de inicio de sesión incluye la interfaz para ello, pero los endpoints a los que llama
  (`GET /api/v1/account/export`, `POST /api/v1/account/erasure`) los sirve el host de autenticación de Cloud y
  **no** forman parte de la superficie de esta biblioteca. Un despliegue autoalojado debe implementarlos o dejar los dos
  botones fuera de su página de cuenta: `MapFallbackToFile` responde a una ruta no implementada con 200 y el propio HTML
  de la SPA, de modo que una exportación no implementada tiene que reconocerse como tal en lugar de descargarse.
- **Aprovisionamiento TCC**: aprovisionamiento Try-Confirm-Cancel en aplicaciones descendentes en el momento de la autorización
- **Interfaz de inicio de sesión personalizable**: configurable en tiempo de ejecución mediante un archivo JSON (logotipo, colores, propiedades personalizadas de CSS) sin necesidad de recompilar; traducida a 11 idiomas
- **Hooks de autenticación**: extensibilidad mediante `IAuthHook` para registro de auditoría, validación personalizada y webhooks
- **Puntos de extensión para cifrado de PII**: puntos de extensión `IFieldCipher` / `IIndexTokenizer` para cifrado en reposo por campo con búsqueda por índice ciego con clave (HMAC); códigos de recuperación cifrados mediante `ISecretProvider`
- **Cliente de HashiCorp Vault Transit**: firma/verificación, cifrado/descifrado y HMAC con clave contra el motor Transit de Vault, para construir un `IFieldCipher` o un `IIndexTokenizer`. La firma remota de JWT no está conectada: la clave de firma de tokens es siempre la de `ISigningKeyStore`.
- **Biblioteca componible**: `AddAuthagonal()` / `UseAuthagonal()` para alojarlo en su propio proyecto con sustituciones de servicios personalizadas
- **Preparado para Native AOT**: recorte de IL y serialización JSON generada en código fuente para un inicio rápido
- **Almacenamiento intercambiable**: PostgreSQL o SQLite autoalojados (sin cuenta en la nube), o Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) como backends de bajo coste y aptos para entornos serverless
- **Copia de seguridad y restauración**: copias de seguridad incrementales (basadas en el registro de cambios, con un escaneo completo como red de seguridad), verificación de integridad y seguimiento de eliminaciones basado en tombstones
- **API de administración**: CRUD de usuarios, gestión de proveedores SAML/OIDC, enrutamiento de dominios SSO y suplantación de tokens

## Integraciones habituales {#common-integrations}

Guías orientadas a tareas para los flujos que los equipos construyen con más frecuencia:

- **[Actualizar un usuario](user-upgrade)**: convierta una cuenta de invitado / SSO / invitación en una con credenciales mediante la toma de posesión de una cuenta sin contraseña, y ejecute su promoción de invitado → miembro estándar al confirmarse.
- **[SSO de autoservicio](self-service-sso)**: aprovisionamiento JIT para conexiones empresariales: incorporación solo por invitación frente a autoservicio, cómo evitar que los IdP externos se conviertan en una trampa, y pantallas intermedias previas a la federación.
- **[Sesiones federadas](federated-sessions)**: revoque la sesión local cuando lo haga el IdP de origen (`RevalidateOnRefresh`).
- **[Backend-for-Frontend (BFF)](bff)**: mantenga los tokens fuera del navegador: un cliente OIDC confidencial en su backend con una cookie de sesión httpOnly y un proxy de API que inyecta el token, en .NET o Node.
- **[Autenticación de WebSocket](websocket-auth)**: autentique WebSockets del navegador a través del BFF sin exponer ningún token.
- **[Autenticación agéntica](agentic-auth)**: delegue la autoridad de un usuario en agentes de IA: agentes registrados, autoridad detallada según RFC 9396, tokens de delegación compuestos (`act` de RFC 8693), consentimiento permanente, aprobaciones justo a tiempo y tickets de capacidad.
- **[Organizaciones](organizations)**: atienda a muchos clientes desde un único inquilino: registros `Organization` y de pertenencia, el parámetro de autorización `organization`, `org_id` / `org_slug` / `org_name` en los tokens, roles con alcance de organización y rechazo de quienes no son miembros.

## Arquitectura {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Empiece con la guía de [Instalación](installation) o vaya directamente al [Inicio rápido](quickstart). Para alojar Authagonal en su propio proyecto, consulte [Extensibilidad](extensibility). Para la gestión de datos, consulte [Copia de seguridad y restauración](backup-restore). Para el historial completo de cambios, consulte el [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
