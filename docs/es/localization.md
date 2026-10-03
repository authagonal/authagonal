---
layout: default
title: Localización
locale: es
---

# Localización

La interfaz de inicio de sesión incluye once idiomas de serie: inglés, chino simplificado (`zh-Hans`), alemán (`de`), francés (`fr`), español (`es`), vietnamita (`vi`), portugués (`pt`), árabe (`ar`), afrikáans (`af`), hindi (`hi`) y japonés (`ja`). Las respuestas de la API del servidor y las páginas renderizadas en el servidor también están localizadas en los once. La localización abarca las respuestas de la API del servidor, la interfaz de inicio de sesión y este sitio de documentación.

## Idiomas admitidos {#supported-languages}

| Código | Idioma | Interfaz de inicio de sesión | API del servidor |
|---|---|---|---|
| `en` | Inglés (predeterminado) | ✓ | ✓ |
| `zh-Hans` | Chino simplificado | ✓ | ✓ |
| `de` | Alemán | ✓ | ✓ |
| `fr` | Francés | ✓ | ✓ |
| `es` | Español | ✓ | ✓ |
| `vi` | Vietnamita | ✓ | ✓ |
| `pt` | Portugués | ✓ | ✓ |
| `ar` | Árabe (de derecha a izquierda) | ✓ | ✓ |
| `af` | Afrikáans | ✓ | ✓ |
| `hi` | Hindi | ✓ | ✓ |
| `ja` | Japonés | ✓ | ✓ |

## Servidor (respuestas de la API) {#server-api-responses}

El servidor usa la localización integrada de ASP.NET Core con `IStringLocalizer<T>` y archivos de recursos `.resx`. El idioma se selecciona a partir del encabezado HTTP `Accept-Language`.

### Qué se localiza {#what-is-localized}

- Mensajes de error de validación de contraseñas
- Etiquetas de la política de contraseñas (`GET /api/auth/password-policy`)
- Mensajes del flujo de restablecimiento de contraseña (errores de token, caducidad, éxito)
- Descripciones de error genéricas del middleware de gestión de excepciones
- Mensajes de la administración de usuarios (confirmación de correo electrónico, verificación, etc.)
- Mensaje de confirmación del cierre de sesión
- Páginas renderizadas en el servidor (resultado de la confirmación del correo electrónico, páginas de confirmación del cierre de sesión y de sesión cerrada), incluidos `<html lang>` y `dir="rtl"` para el árabe
- Correos electrónicos enviados por la biblioteca (`EmailService`: verificación, restablecimiento de contraseña y el aviso de "la cuenta ya existe"), en el idioma guardado del destinatario (`AuthUser.Locale`); si no lo hay, en la cultura de la solicitud y, en su defecto, en inglés

### Qué NO se localiza {#what-is-not-localized}

- Los códigos `error` legibles por máquina (`"email_required"`, `"invalid_credentials"`, etc.): son contratos de la API y no cambian
- Los códigos de error de OAuth/OIDC y las descripciones de error dirigidas a desarrolladores en los endpoints de token, autorización y revocación
- Los mensajes internos de registro y los mensajes de excepción

### Probar la localización del servidor {#testing-server-localization}

Envíe un encabezado `Accept-Language` a cualquier endpoint localizado:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Archivos de recursos {#resource-files}

Todas las cadenas de traducción del servidor están en archivos `.resx` dentro de `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
  SharedMessages.ja.resx
  SharedMessages.ar.resx
  SharedMessages.af.resx
  SharedMessages.hi.resx
```

## Interfaz de inicio de sesión {#login-ui}

La SPA de inicio de sesión usa [react-i18next](https://react.i18next.com/) para la localización en el cliente. El idioma se detecta automáticamente a partir del ajuste `navigator.language` del navegador.

Los idiomas registrados están en un único registro `LANGUAGES` en `login-app/src/i18n/index.ts`, que controla tanto el registro de recursos de i18next como todos los selectores de idioma, de modo que ambos no pueden divergir. Actualmente, todos los idiomas registrados aparecen en el selector predeterminado. `DEFAULT_LANGUAGES` se exporta por separado de `LANGUAGES` para que un futuro idioma restringido pueda excluirse de los selectores sin tocar los puntos de llamada, pero hoy no se excluye ninguno. Los inquilinos también pueden reducir el selector del mismo modo: una matriz `languages` en `branding.json` sustituye por completo la lista predeterminada (consulte [Personalización de marca](branding)).

El idioma activo se refleja en `<html lang>` y `<html dir>`, de modo que los idiomas de derecha a izquierda (`ar`) invierten automáticamente la tarjeta de autenticación, también cuando se cambia el idioma sobre la marcha desde el selector.

### Detección del idioma {#language-detection}

El orden de detección es:

1. **localStorage**: preferencia guardada de una visita anterior
2. **Parámetro de consulta**: `?lng=de` tiene prioridad sobre la detección del navegador
3. **Idioma del navegador**: `navigator.language` (automático)
4. **Idioma de reserva**: inglés (`en`)

### Archivos de traducción {#translation-files}

Los archivos JSON de traducción se empaquetan con la aplicación en `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
  ja.json         # Japanese
```

### Etiquetas de la política de contraseñas {#password-policy-labels}

La página de restablecimiento de contraseña traduce en el cliente su lista de requisitos de contraseña a partir de la clave `rule` que devuelve `GET /api/auth/password-policy` (y recurre a la `label` proporcionada por el servidor para las reglas que no reconoce). Así, los requisitos siguen el idioma seleccionado en la interfaz aunque el encabezado `Accept-Language` del navegador sea distinto. La página de registro muestra los valores `label` proporcionados por el servidor, que se localizan a partir de `Accept-Language`.

### Consumidores del paquete npm {#npm-package-consumers}

Si consume la aplicación de inicio de sesión mediante `@authagonal/login`, la instancia de i18n se exporta:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentación {#documentation}

El sitio de documentación usa un enfoque basado en directorios. Las páginas en inglés están en la raíz y las traducciones en subdirectorios por idioma (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`, `/ja/`). Un desplegable de cambio de idioma en la barra lateral permite pasar de un idioma a otro.

## Añadir un nuevo idioma {#adding-a-new-language}

Para añadir compatibilidad con un nuevo idioma (por ejemplo, italiano `it`):

### 1. Servidor {#1-server}

Cree un nuevo archivo `.resx` copiando el de inglés y traduciendo los valores:

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

Añada `"it"` a `SupportedLocales.All` en `src/Authagonal.Server/Services/SupportedLocales.cs`, la única lista que leen tanto el middleware de localización de solicitudes como las páginas renderizadas en el servidor:

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. Interfaz de inicio de sesión {#2-login-ui}

Cree un nuevo archivo JSON de traducción copiando `en.json` y traduciendo los valores:

```
login-app/src/i18n/it.json
```

Regístrelo en la matriz `LANGUAGES` de `login-app/src/i18n/index.ts`. Esa única entrada registra el recurso de i18next y añade el idioma a todos los selectores:

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. Documentación {#3-documentation}

Cree un nuevo directorio con los archivos markdown traducidos:

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

Añada un valor predeterminado de idioma en `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

Añada la opción del idioma al selector de `docs/_layouts/default.html`.

## Añadir nuevas cadenas {#adding-new-strings}

### Servidor {#server}

1. Añada la clave y el valor en inglés a `SharedMessages.resx`
2. Añada los valores traducidos al archivo `.resx` de cada idioma
3. Use `IStringLocalizer<SharedMessages>` para acceder a la cadena:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interfaz de inicio de sesión {#login-ui-1}

1. Añada la clave y el valor en inglés a `en.json`
2. Añada los valores traducidos al archivo JSON de cada idioma
3. Use la función `t()` en los componentes:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
