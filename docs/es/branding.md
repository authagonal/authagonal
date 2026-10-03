---
layout: default
title: Personalización de marca
locale: es
---

# Personalización de marca de la interfaz de inicio de sesión

La SPA de inicio de sesión se configura en tiempo de ejecución mediante un archivo `branding.json` servido desde la raíz web. No hace falta recompilar: basta con montar su configuración y sus recursos.

## Cómo funciona {#how-it-works}

Al arrancar, la SPA obtiene `/branding.json`. Si el archivo no existe o no es accesible, se usan los valores por defecto. (Un servidor host también puede incrustar la configuración como carga de arranque `<script type="application/json" id="authagonal-boot">`; si está presente, la SPA la lee en lugar de descargarla). La configuración controla:

- El nombre de la aplicación (se muestra en el encabezado y en el título de la página)
- La imagen del logotipo, con un «chip» de fondo opcional por modo
- El color principal (botones, enlaces, anillos de foco), con una variante opcional para el modo oscuro
- Los colores de fondo de la página y de la tarjeta, por modo
- La visibilidad de los enlaces de contraseña olvidada y de registro
- El modo oscuro por defecto (claro / seguir al sistema operativo / oscuro)
- Las opciones del selector de idioma
- El pie «Con la tecnología de Authagonal»
- CSS personalizado para una personalización más profunda

## Nombre de la organización {#organisation-name}

En un host multiinquilino que resuelve una organización para la solicitud (p. ej., un dominio personalizado fijado
a un cliente), la carga `authagonal-boot` puede llevar un tercer miembro junto a `branding` y
`providers`:

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

`organization` es `null` (o el miembro no está presente) cuando la solicitud no se resolvió a ninguna organización:
un despliegue de un solo inquilino, o uno sin un dominio personalizado fijado para este host. Esta biblioteca no
resuelve por sí misma una organización para una solicitud anónima previa a la autenticación (`OrganizationSelector`
necesita un `AuthUser` con sesión iniciada); un host que tiene su propia resolución previa a la autenticación
(Authagonal Cloud fija una por dominio personalizado) establece `organization` al ensamblar la carga de arranque.

Cuando `organization.name` está presente y difiere de `branding.appName` (sin distinguir mayúsculas y minúsculas y sin
espacios en los extremos, para que una organización con el mismo nombre que el inquilino no produzca «Acme / Iniciando
sesión en Acme»), la tarjeta de inicio de sesión muestra un subtítulo bajo el encabezado: «Iniciando sesión en {name}»
(`data-testid="login-org-name"`, clave i18n `login.signingInTo`). Se muestra una sola vez, en el encabezado compartido
de `AuthLayout`, de modo que todas las rutas que se montan a través de él (inicio de sesión, registro, contraseña
olvidada y restablecimiento de contraseña, las páginas de desafío y de configuración inicial de MFA, la página de dispositivo, el
consentimiento y el consentimiento de agentes, las concesiones y la cuenta) lo muestran de forma idéntica. No se muestra
nada, y el encabezado conserva su espaciado normal, cuando `organization` no está presente, es `null` o su nombre
coincide con `branding.appName`.

## Configuración {#configuration}

Coloque un archivo `branding.json` en el directorio `wwwroot/` (o móntelo en el contenedor Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Opciones {#options}

| Propiedad | Tipo | Valor por defecto | Descripción |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Se muestra en el encabezado y en el título de la pestaña del navegador |
| `logoUrl` | `string \| null` | `null` | URL de una imagen de logotipo. Si se establece, sustituye al encabezado de texto. |
| `primaryColor` | `string` | `"#2563eb"` | Color hexadecimal para botones, enlaces e indicadores de foco |
| `supportEmail` | `string \| null` | `null` | Correo de contacto de soporte (reservado para uso futuro) |
| `showForgotPassword` | `boolean` | `true` | Muestra u oculta el enlace «¿Olvidaste tu contraseña?» en la página de inicio de sesión |
| `showRegistration` | `boolean` | `false` | Muestra u oculta el enlace de registro de autoservicio |
| `customCssUrl` | `string \| null` | `null` | URL de un archivo CSS personalizado que se carga después de los estilos por defecto |
| `welcomeTitle` | `LocalizedString` | `null` | Saludo opcional que se muestra bajo el encabezado en las páginas de autenticación (cadena simple o `{ "en": "...", "de": "..." }`). Si no se establece, no se muestra nada. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Línea opcional bajo `welcomeTitle`, con la misma forma. Si no se establece, no se muestra nada. |
| `languages` | `array \| null` | `null` | Opciones del selector de idioma (`[{ "code": "en", "label": "English" }, ...]`). `null` muestra todos los idiomas incluidos excepto las configuraciones regionales de fantasía (consulte [Localización](localization)). |
| `poweredBy` | `boolean` | `true` | Muestra u oculta el pie «Con la tecnología de Authagonal» en las páginas de autenticación |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Tema por defecto cuando el visitante no ha elegido uno: `"off"` (solo claro), `"auto"` (sigue la preferencia del sistema operativo), `"force"` (siempre oscuro). El selector de tema del visitante sigue prevaleciendo. |
| `lightBg` | `string \| null` | `null` | Color de fondo de la página en modo claro |
| `lightCardBg` | `string \| null` | `null` | Color de fondo de la tarjeta o formulario en modo claro |
| `darkBg` | `string \| null` | `null` | Color de fondo de la página en modo oscuro |
| `darkCardBg` | `string \| null` | `null` | Color de fondo de la tarjeta o formulario en modo oscuro |
| `darkPrimaryColor` | `string \| null` | `null` | Sustituye a `primaryColor` en modo oscuro |
| `lightLogoBg` | `string \| null` | `null` | Fondo del chip del logotipo en modo claro (consulte más abajo) |
| `darkLogoBg` | `string \| null` | `null` | Fondo del chip del logotipo en modo oscuro (consulte más abajo) |

Los valores de color deben ser un color hexadecimal (`#rgb`, `#rrggbb`, `#rrggbbaa`) o una expresión `rgb()`/`rgba()`/`hsl()`/`hsla()`; cualquier otra cosa se ignora. Los colores por modo se inyectan como una regla `<style id="branding-theme-vars">` después de los estilos incluidos: los valores `light*` en `:root:where(:not(.dark))`, de modo que nunca se aplican en modo oscuro; los valores oscuros en `.dark`; y `primaryColor` en `:root`, ya que es el color base de ambos modos. `:where()` no añade especificidad, así que `customCssUrl` sigue sustituyéndolos todos.

### Chip de fondo del logotipo {#logo-background-chip}

Si su logotipo tiene un diseño blanco o transparente, puede desaparecer sobre la tarjeta clara. Establezca `lightLogoBg` y/o `darkLogoBg` para mostrar el logotipo dentro de un «chip» redondeado y con relleno con ese color de fondo:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

El chip (un contenedor `data-auth="logo-chip"` controlado por la variable CSS `--auth-logo-bg`) solo recibe su relleno y su fondo cuando se configura un fondo de logotipo, de modo que los inquilinos que no lo establecen ven el logotipo pegado a la tarjeta exactamente igual que antes. Los dos campos son independientes: establezca solo `lightLogoBg` para mostrar el logotipo en un chip en modo claro y dejarlo sin chip en modo oscuro.

## Ejemplo con Docker {#docker-example}

Monte sus archivos de marca en el contenedor:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

O con docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## CSS personalizado {#custom-css}

La opción `customCssUrl` carga una hoja de estilos adicional después de los estilos por defecto, de modo que sus reglas tienen prioridad. Resulta útil para cambiar las fuentes, ajustar el espaciado o volver a dar estilo a elementos concretos. La URL debe ser del mismo origen (las URL relativas como `/branding/custom.css` sirven); las hojas de estilos de otro origen se omiten sin aviso.

### Propiedades personalizadas de CSS {#css-custom-properties}

La interfaz de inicio de sesión expone varias propiedades personalizadas de CSS para un control detallado:

| Propiedad | Valor por defecto | Descripción |
|---|---|---|
| `--brand-primary` | `#2563eb` | Color principal para botones, enlaces y anillos de foco |
| `--auth-bg` | `#f3f4f6` | Color de fondo de la página |
| `--auth-card-bg` | `#ffffff` | Color de fondo de la tarjeta o formulario |
| `--auth-logo-bg` | `transparent` | Fondo del chip del logotipo (el relleno del chip solo aparece cuando se configura un fondo de logotipo) |
| `--auth-radius` | `0.5rem` | Radio del borde de la tarjeta de autenticación |
| `--auth-font` | *(heredada; pila de fuentes del sistema)* | Familia tipográfica de la tarjeta de autenticación |
| `--auth-heading` | `#111827` | Color del texto de los encabezados |

Estas variables de color se corresponden directamente con campos de configuración (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), así que prefiera la configuración para cambios de color sencillos y reserve el CSS personalizado para todo lo demás.

Sustitúyalas en su CSS personalizado:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

La interfaz de inicio de sesión usa Tailwind CSS. El CSS personalizado puede dirigirse a elementos HTML estándar y a clases de utilidad de Tailwind. Los componentes de interfaz exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usan Tailwind internamente.

## Modo oscuro {#dark-mode}

La SPA de inicio de sesión incluye los temas claro, oscuro y **del sistema**. El selector de tema siempre está visible en el diseño. La elección del usuario se guarda en `localStorage` bajo la clave `auth-theme`.

### Cómo funciona {#how-it-works-1}

- **Valor por defecto**: hasta que el visitante elige un tema, la opción de marca `darkMode` fija el valor por defecto: `"off"` (claro), `"auto"` (del sistema, el valor por defecto) o `"force"` (oscuro). En cuanto el visitante usa el selector, su elección siempre prevalece.
- **Detección**: cuando el tema es «del sistema», la SPA observa `window.matchMedia('(prefers-color-scheme: dark)')` y vuelve a aplicar el tema automáticamente cuando cambia la preferencia del sistema operativo.
- **Aplicación**: la SPA activa o desactiva una clase `.dark` en `<html>`. La variante oscura de Tailwind (`&:where(.dark, .dark *)`) activa los estilos oscuros compilados en cada componente.
- **Persistencia**: las elecciones explícitas «claro», «oscuro» o «del sistema» se guardan en `localStorage`.

### Variables CSS {#css-variables}

Los valores claros se declaran en `:root`; las sustituciones del modo oscuro se limitan a `.dark`, de modo que la marca del inquilino en `customCssUrl` siempre tiene prioridad cuando se proporciona.

| Variable | Claro | Oscuro |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (o `lightBg`) | `#030712` (o `darkBg`) |
| `--auth-card-bg` | `#ffffff` (o `lightCardBg`) | `#111827` (o `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (o `lightLogoBg`) | `transparent` (o `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (o `primaryColor`) | el valor claro (o `darkPrimaryColor`) |

### Desactivación o sustitución {#disabling-or-overriding}

La marca del inquilino siempre prevalece. Para imponer un único tema, establezca sus propios valores en `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Para eliminar por completo el selector de tema, use la vía del paquete npm, importe `AuthLayout` y renderice sin el selector, o cree un fork de la SPA.

### Atributos de datos {#data-attributes}

Todos los elementos del formulario de inicio de sesión tienen atributos `data-auth` para seleccionarlos con CSS y para la automatización de pruebas:

| Atributo | Elemento |
|---|---|
| `data-auth="page"` | Contenedor principal de la página |
| `data-auth="header"` | Sección del encabezado |
| `data-auth="logo-chip"` | Contenedor de la imagen del logotipo (con relleno solo cuando se establece un fondo de logotipo) |
| `data-auth="logo"` | Imagen del logotipo |
| `data-auth="app-name"` | Encabezado con el nombre de la aplicación |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | Las líneas opcionales `welcomeTitle` / `welcomeSubtitle` (solo presentes cuando se establecen) |
| `data-auth="content"` | Área de contenido principal |
| `data-auth="languages"` | Selector de idioma |
| `data-auth="language-trigger"` | Botón que abre el selector de idioma |
| `data-auth="theme-toggle"` | Selector de tema claro/del sistema/oscuro |
| `data-auth="powered-by"` | Pie «Con la tecnología de Authagonal» |
| `data-auth="login-form"`, `"email-field"`, `"password-field"`, `"submit-button"` | El formulario de inicio de sesión y sus partes (solo en la página de inicio de sesión) |

Selecciónelos en su CSS personalizado:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Ejemplo: fondo y fuente personalizados {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveles de personalización {#customization-tiers}

| Nivel | Qué hace usted | Vía de actualización |
|---|---|---|
| **Solo configuración** | Monta `branding.json` + logotipo | Sin fricción: actualice la imagen Docker y conserve sus montajes |
| **Configuración + CSS** | Añade `customCssUrl` con sustituciones de estilo | Igual: las clases CSS son estables |
| **Paquete npm** | `npm install @authagonal/login`, personaliza `branding.json` y compila en `wwwroot/` | Actualizable: `npm update` descarga las versiones nuevas |
| **Fork de la SPA** | Clona `login-app/`, modifica el código fuente y compila su propia versión | La interfaz es suya; las actualizaciones del servidor son independientes |
| **Escribir la suya propia** | Crea un frontend completamente personalizado sobre la API de autenticación | Control total; consulte [API de autenticación](auth-api) para el contrato |

Consulte `demos/custom-server/` para ver un ejemplo funcional con marca personalizada (tema verde, «Acme Corp»).
