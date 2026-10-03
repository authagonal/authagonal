---
layout: default
title: Branding
locale: de
---

# Branding der Login-Oberfläche

Die Login-SPA lässt sich zur Laufzeit über eine Datei `branding.json` konfigurieren, die aus dem Web-Root ausgeliefert wird. Ein Neubau ist nicht nötig; Sie binden nur Ihre Konfiguration und Ihre Assets ein.

## Funktionsweise {#how-it-works}

Beim Start ruft die SPA `/branding.json` ab. Fehlt die Datei oder ist sie nicht erreichbar, gelten die Standardwerte. (Ein Host-Server kann die Konfiguration auch direkt als Boot-Payload `<script type="application/json" id="authagonal-boot">` einbetten; ist sie vorhanden, liest die SPA diese, statt die Datei abzurufen.) Die Konfiguration steuert:

- den Anwendungsnamen (in der Kopfzeile und im Seitentitel)
- das Logo, optional mit einem "Chip" als Hintergrund je Modus
- die Primärfarbe (Schaltflächen, Links, Fokusringe), optional mit einer Variante für den Dunkelmodus
- die Hintergrundfarben von Seite und Karte, je Modus
- die Sichtbarkeit der Links für "Passwort vergessen" und die Registrierung
- den Standard für den Dunkelmodus (hell / Betriebssystem folgen / dunkel)
- die Optionen der Sprachauswahl
- die Fußzeile "Bereitgestellt von Authagonal"
- eigenes CSS für weitergehende Gestaltung

## Name der Organisation {#organisation-name}

Auf einem mandantenfähigen Host, der für den Request eine Organisation ermittelt (zum Beispiel eine
benutzerdefinierte Domain, die fest einem Kunden zugeordnet ist), kann die Payload `authagonal-boot`
neben `branding` und `providers` ein drittes Element enthalten:

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

`organization` ist `null` (oder das Element fehlt), wenn der Request zu keiner Organisation aufgelöst
wurde: bei einer Bereitstellung mit nur einem Mandanten oder wenn für diesen Host keine benutzerdefinierte
Domain einer Organisation zugeordnet ist. Diese Bibliothek ermittelt für einen anonymen Request vor der
Authentifizierung selbst keine Organisation (`OrganizationSelector` benötigt einen angemeldeten
`AuthUser`); ein Host mit eigener Auflösung vor der Authentifizierung (Authagonal Cloud ordnet je
benutzerdefinierter Domain eine zu) setzt `organization`, wenn er die Boot-Payload zusammenstellt.

Ist `organization.name` vorhanden und weicht es von `branding.appName` ab (ohne Beachtung von Groß- und
Kleinschreibung und nach Entfernen von Leerzeichen am Rand, damit eine Organisation mit demselben Namen wie
der Mandant nicht "Acme / Anmeldung bei Acme" ergibt), zeigt die Login-Karte unter der Überschrift einen
Untertitel an: "Anmeldung bei {name}" (`data-testid="login-org-name"`, i18n-Schlüssel `login.signingInTo`).
Er wird genau einmal gerendert, von der gemeinsamen Kopfzeile in `AuthLayout`, sodass jede darüber
eingebundene Route (Anmeldung, Registrierung, Passwort vergessen und zurücksetzen, die Seiten für
MFA-Abfrage und MFA-Einrichtung, die Geräteseite, Zustimmung und Agenten-Zustimmung, Grants und Konto) ihn
identisch anzeigt. Nichts wird gerendert, und die Kopfzeile behält ihre normalen Abstände, wenn
`organization` fehlt, `null` ist oder der Name mit `branding.appName` übereinstimmt.

## Konfiguration {#configuration}

Legen Sie eine Datei `branding.json` im Verzeichnis `wwwroot/` ab (oder binden Sie sie in den Docker-Container ein):

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

### Optionen {#options}

| Eigenschaft | Typ | Standard | Beschreibung |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Wird in der Kopfzeile und im Titel des Browser-Tabs angezeigt |
| `logoUrl` | `string \| null` | `null` | URL eines Logobilds. Ist sie gesetzt, ersetzt sie die Textüberschrift. |
| `primaryColor` | `string` | `"#2563eb"` | Hex-Farbe für Schaltflächen, Links und Fokusanzeigen |
| `supportEmail` | `string \| null` | `null` | E-Mail-Adresse für Support (für künftige Verwendung reserviert) |
| `showForgotPassword` | `boolean` | `true` | Blendet den Link "Passwort vergessen?" auf der Login-Seite ein oder aus |
| `showRegistration` | `boolean` | `false` | Blendet den Link zur Selbstregistrierung ein oder aus |
| `customCssUrl` | `string \| null` | `null` | URL einer eigenen CSS-Datei, die nach den Standard-Styles geladen wird |
| `welcomeTitle` | `LocalizedString` | `null` | Optionale Begrüßung unter der Kopfzeile der Authentifizierungsseiten (einfacher String oder `{ "en": "...", "de": "..." }`). Ist sie nicht gesetzt, wird nichts gerendert. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Optionale Zeile unter `welcomeTitle`, in derselben Form. Ist sie nicht gesetzt, wird nichts gerendert. |
| `languages` | `array \| null` | `null` | Optionen der Sprachauswahl (`[{ "code": "en", "label": "English" }, ...]`). `null` zeigt alle mitgelieferten Sprachen außer Spaß-Locales (siehe [Lokalisierung](localization)). |
| `poweredBy` | `boolean` | `true` | Blendet die Fußzeile "Bereitgestellt von Authagonal" auf den Authentifizierungsseiten ein oder aus |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Standard-Theme, solange der Besucher keines gewählt hat: `"off"` (nur hell), `"auto"` (folgt der Einstellung des Betriebssystems), `"force"` (immer dunkel). Der Theme-Umschalter des Besuchers hat weiterhin Vorrang. |
| `lightBg` | `string \| null` | `null` | Hintergrundfarbe der Seite im hellen Modus |
| `lightCardBg` | `string \| null` | `null` | Hintergrundfarbe von Karte und Formular im hellen Modus |
| `darkBg` | `string \| null` | `null` | Hintergrundfarbe der Seite im Dunkelmodus |
| `darkCardBg` | `string \| null` | `null` | Hintergrundfarbe von Karte und Formular im Dunkelmodus |
| `darkPrimaryColor` | `string \| null` | `null` | Überschreibt `primaryColor` im Dunkelmodus |
| `lightLogoBg` | `string \| null` | `null` | Hintergrund des Logo-Chips im hellen Modus (siehe unten) |
| `darkLogoBg` | `string \| null` | `null` | Hintergrund des Logo-Chips im Dunkelmodus (siehe unten) |

Farbwerte müssen eine Hex-Farbe (`#rgb`, `#rrggbb`, `#rrggbbaa`) oder ein Ausdruck `rgb()`/`rgba()`/`hsl()`/`hsla()` sein; alles andere wird ignoriert. Die Farben je Modus werden als Regel `<style id="branding-theme-vars">` nach den mitgelieferten Styles eingefügt: die `light*`-Werte unter `:root:where(:not(.dark))`, sodass sie im Dunkelmodus nie greifen; die dunklen Werte unter `.dark`; und `primaryColor` unter `:root`, da es die Grundfarbe beider Modi ist. `:where()` erhöht die Spezifität nicht, daher überschreibt `customCssUrl` weiterhin alle diese Werte.

### Logo-Hintergrund-Chip {#logo-background-chip}

Hat Ihr Logo weiße oder transparente Grafikelemente, kann es auf der hellen Karte verschwinden. Setzen Sie `lightLogoBg` und/oder `darkLogoBg`, um das Logo in einem abgerundeten "Chip" mit Innenabstand und dieser Hintergrundfarbe darzustellen:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

Der Chip (ein Wrapper `data-auth="logo-chip"`, gesteuert über die CSS-Variable `--auth-logo-bg`) erhält Innenabstand und Hintergrund nur, wenn ein Logo-Hintergrund konfiguriert ist. Mandanten, die keinen setzen, sehen das Logo also wie bisher bündig auf der Karte. Die beiden Felder sind unabhängig: Setzen Sie nur `lightLogoBg`, erscheint das Logo im hellen Modus im Chip und im Dunkelmodus ohne.

## Docker-Beispiel {#docker-example}

Binden Sie Ihre Branding-Dateien in den Container ein:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Oder mit docker-compose:

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

## Eigenes CSS {#custom-css}

Die Option `customCssUrl` lädt nach den Standard-Styles ein zusätzliches Stylesheet, sodass Ihre Regeln Vorrang haben. Das eignet sich, um Schriftarten zu ändern, Abstände anzupassen oder einzelne Elemente neu zu gestalten. Die URL muss denselben Origin haben (relative URLs wie `/branding/custom.css` sind in Ordnung); Stylesheets von einem anderen Origin werden stillschweigend übersprungen.

### CSS Custom Properties {#css-custom-properties}

Die Login-Oberfläche stellt mehrere CSS Custom Properties für eine feinere Steuerung bereit:

| Eigenschaft | Standard | Beschreibung |
|---|---|---|
| `--brand-primary` | `#2563eb` | Primärfarbe für Schaltflächen, Links und Fokusringe |
| `--auth-bg` | `#f3f4f6` | Hintergrundfarbe der Seite |
| `--auth-card-bg` | `#ffffff` | Hintergrundfarbe von Karte und Formular |
| `--auth-logo-bg` | `transparent` | Hintergrund des Logo-Chips (der Innenabstand des Chips erscheint nur, wenn ein Logo-Hintergrund konfiguriert ist) |
| `--auth-radius` | `0.5rem` | Eckenradius der Authentifizierungskarte |
| `--auth-font` | *(geerbt; System-Schriftstapel)* | Schriftfamilie der Authentifizierungskarte |
| `--auth-heading` | `#111827` | Textfarbe der Überschriften |

Die Farbvariablen entsprechen direkt Konfigurationsfeldern (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`). Verwenden Sie für einfache Farbänderungen daher bevorzugt die Konfiguration und eigenes CSS für alles Übrige.

Überschreiben Sie sie in Ihrem eigenen CSS:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Die Login-Oberfläche verwendet Tailwind CSS. Eigenes CSS kann Standard-HTML-Elemente und Tailwind-Utility-Klassen ansprechen. Die exportierten UI-Komponenten (`Button`, `Input`, `Card`, `Alert` usw.) verwenden intern Tailwind.

## Dunkelmodus {#dark-mode}

Die Login-SPA bringt ein helles, ein dunkles und ein **System**-Theme mit. Der Theme-Umschalter ist im Layout immer sichtbar. Die Auswahl des Benutzers wird in `localStorage` unter dem Schlüssel `auth-theme` gespeichert.

### Funktionsweise {#how-it-works-1}

- **Standard**: Bis der Besucher ein Theme wählt, legt die Branding-Option `darkMode` den Standard fest: `"off"` (hell), `"auto"` (System, der Standard) oder `"force"` (dunkel). Sobald der Besucher den Umschalter benutzt, hat seine Wahl immer Vorrang.
- **Erkennung**: Ist das Theme "System", beobachtet die SPA `window.matchMedia('(prefers-color-scheme: dark)')` und wendet das Theme automatisch neu an, wenn sich die Einstellung des Betriebssystems ändert.
- **Anwendung**: Die SPA schaltet eine Klasse `.dark` auf `<html>` um. Die Dark-Variante von Tailwind (`&:where(.dark, .dark *)`) aktiviert die dunklen Styles, die in jede Komponente kompiliert sind.
- **Speicherung**: Eine ausdrückliche Wahl von "hell", "dunkel" oder "System" wird in `localStorage` gespeichert.

### CSS-Variablen {#css-variables}

Die hellen Werte sind unter `:root` deklariert; die Überschreibungen für den Dunkelmodus sind auf `.dark` beschränkt, sodass das Branding des Mandanten in `customCssUrl` immer Vorrang hat, wenn es angegeben ist.

| Variable | Hell | Dunkel |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (oder `lightBg`) | `#030712` (oder `darkBg`) |
| `--auth-card-bg` | `#ffffff` (oder `lightCardBg`) | `#111827` (oder `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (oder `lightLogoBg`) | `transparent` (oder `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (oder `primaryColor`) | der helle Wert (oder `darkPrimaryColor`) |

### Deaktivieren oder Überschreiben {#disabling-or-overriding}

Das Branding des Mandanten hat immer Vorrang. Um ein einziges Theme zu erzwingen, setzen Sie eigene Werte in `customCssUrl`:

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

Um den Theme-Umschalter ganz zu entfernen, nutzen Sie den Weg über das npm-Paket, importieren `AuthLayout` und rendern ohne den Umschalter, oder Sie forken die SPA.

### Data-Attribute {#data-attributes}

Alle Elemente des Login-Formulars tragen `data-auth`-Attribute, die sich für CSS-Selektoren und Testautomatisierung eignen:

| Attribut | Element |
|---|---|
| `data-auth="page"` | Wrapper der Hauptseite |
| `data-auth="header"` | Kopfbereich |
| `data-auth="logo-chip"` | Wrapper um das Logobild (nur mit Innenabstand, wenn ein Logo-Hintergrund gesetzt ist) |
| `data-auth="logo"` | Logobild |
| `data-auth="app-name"` | Überschrift mit dem App-Namen |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | Die optionalen Zeilen `welcomeTitle` / `welcomeSubtitle` (nur vorhanden, wenn gesetzt) |
| `data-auth="content"` | Hauptinhaltsbereich |
| `data-auth="languages"` | Sprachauswahl |
| `data-auth="language-trigger"` | Schaltfläche, die die Sprachauswahl öffnet |
| `data-auth="theme-toggle"` | Umschalter für helles, System- und dunkles Theme |
| `data-auth="powered-by"` | Fußzeile "Bereitgestellt von Authagonal" |
| `data-auth="login-form"`, `"email-field"`, `"password-field"`, `"submit-button"` | Das Anmeldeformular und seine Teile (nur auf der Login-Seite) |

Sprechen Sie diese in Ihrem eigenen CSS an:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Beispiel: Eigener Hintergrund und eigene Schrift {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Stufen der Anpassung {#customization-tiers}

| Stufe | Was Sie tun | Aktualisierungsweg |
|---|---|---|
| **Nur Konfiguration** | `branding.json` und Logo einbinden | Nahtlos: Docker-Image aktualisieren, Ihre Einbindungen bleiben |
| **Konfiguration + CSS** | `customCssUrl` mit Style-Überschreibungen hinzufügen | Ebenso; die CSS-Klassen sind stabil |
| **npm-Paket** | `npm install @authagonal/login`, `branding.json` anpassen, nach `wwwroot/` bauen | Aktualisierbar; `npm update` holt neue Versionen |
| **SPA forken** | `login-app/` klonen, Quellcode ändern, selbst bauen | Die Oberfläche gehört Ihnen; Server-Updates sind davon unabhängig |
| **Selbst schreiben** | Ein vollständig eigenes Frontend gegen die Auth-API bauen | Volle Kontrolle; den Vertrag beschreibt die [Auth-API](auth-api) |

Ein funktionierendes Beispiel mit eigenem Branding (grünes Theme, "Acme Corp") finden Sie in `demos/custom-server/`.
