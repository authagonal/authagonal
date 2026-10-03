---
layout: default
title: Inicio rápido
locale: es
---

# Inicio rápido

Ponga Authagonal en marcha en local en 5 minutos.

## 1. Iniciar el servidor {#1-start-the-server}

```bash
docker compose up
```

Esto inicia Authagonal en `http://localhost:8080` con Azurite como almacenamiento.

> El archivo compose establece `Auth__AllowInsecureHttp=true`, porque RFC 6749 §3.1/§3.2 exigen TLS en los endpoints de autorización y de token, y de lo contrario Authagonal rechaza las solicitudes en texto plano a `/connect/*`. Ese interruptor es para un portátil. Todo lo que otra persona pueda alcanzar debe ir detrás de un proxy que termine TLS y reenvíe `X-Forwarded-Proto: https`, sin ese interruptor: consulte [Instalación](installation).

## 2. Comprobar que está en ejecución {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Registrar un cliente {#3-register-a-client}

Añada un cliente a su `appsettings.json` (o páselo mediante variables de entorno):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Los clientes se cargan al iniciar, y es seguro hacerlo en cada despliegue.

## 4. Iniciar un inicio de sesión {#4-initiate-a-login}

Redirija a sus usuarios a:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

El usuario ve la página de inicio de sesión, se autentica y se le redirige de vuelta con un código de autorización.

> **Primer usuario:** registre uno en `http://localhost:8080/login/register`, o cree uno mediante la [API de administración](admin-api). El autorregistro envía un correo de verificación y, sin un remitente de correo configurado (el valor predeterminado en local), ese correo se descarta; por eso, para pruebas locales, establezca `Auth__AutoConfirmEmailDomains__0=example.dev` (cualquier dominio con el que se registre) para omitir la verificación, o configure `Email:ResendApiKey` + `Email:SenderEmail`. Consulte [Configuración → Correo electrónico](configuration#email).

## 5. Canjear el código {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Respuesta:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` es el `AccessTokenLifetimeSeconds` del cliente (1800 para un cliente cargado desde la configuración, salvo que usted lo establezca). Aquí no aparece ningún `refresh_token`: un cliente solo recibe uno cuando establece `AllowOfflineAccess` y la solicitud pide el ámbito `offline_access`.

## Demo funcional {#working-demo}

El directorio `demos/sample-app/` contiene una SPA de React + API completa que implementa todo el flujo OIDC anterior. Consulte el [README de las demos](https://github.com/authagonal/authagonal/tree/master/demos) para ver las instrucciones.

## Próximos pasos {#next-steps}

- [Configuración](configuration): referencia completa de todos los ajustes
- [Extensibilidad](extensibility): alojar como biblioteca y añadir hooks personalizados
- [Personalización de marca](branding): personalizar la interfaz de inicio de sesión
- [SAML](saml): añadir proveedores de SSO SAML
- [Aprovisionamiento](provisioning): aprovisionar usuarios en aplicaciones descendentes
