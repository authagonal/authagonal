---
layout: default
title: Localisation
locale: fr
---

# Localisation

L'interface de connexion est livrée avec onze langues prêtes à l'emploi : anglais, chinois simplifié (`zh-Hans`), allemand (`de`), français (`fr`), espagnol (`es`), vietnamien (`vi`), portugais (`pt`), arabe (`ar`), afrikaans (`af`), hindi (`hi`) et japonais (`ja`). Les réponses de l'API du serveur et les pages rendues côté serveur sont elles aussi traduites dans ces onze langues. La localisation couvre les réponses de l'API du serveur, l'interface de connexion et ce site de documentation.

## Langues prises en charge {#supported-languages}

| Code | Langue | Interface de connexion | API du serveur |
|---|---|---|---|
| `en` | Anglais (par défaut) | ✓ | ✓ |
| `zh-Hans` | Chinois simplifié | ✓ | ✓ |
| `de` | Allemand | ✓ | ✓ |
| `fr` | Français | ✓ | ✓ |
| `es` | Espagnol | ✓ | ✓ |
| `vi` | Vietnamien | ✓ | ✓ |
| `pt` | Portugais | ✓ | ✓ |
| `ar` | Arabe (de droite à gauche) | ✓ | ✓ |
| `af` | Afrikaans | ✓ | ✓ |
| `hi` | Hindi | ✓ | ✓ |
| `ja` | Japonais | ✓ | ✓ |

## Serveur (réponses de l'API) {#server-api-responses}

Le serveur utilise la localisation intégrée d'ASP.NET Core, avec `IStringLocalizer<T>` et des fichiers de ressources `.resx`. La langue est choisie à partir de l'en-tête HTTP `Accept-Language`.

### Ce qui est traduit {#what-is-localized}

- Les messages d'erreur de validation des mots de passe
- Les libellés de la politique de mot de passe (`GET /api/auth/password-policy`)
- Les messages du flux de réinitialisation du mot de passe (erreurs de jeton, expiration, succès)
- Les descriptions d'erreur génériques produites par le middleware de gestion des exceptions
- Les messages de gestion des utilisateurs côté administration (confirmation d'e-mail, vérification, etc.)
- Le message de confirmation de fin de session
- Les pages rendues côté serveur (résultat de la confirmation d'e-mail, pages de confirmation de fin de session et de déconnexion effectuée), y compris `<html lang>` et `dir="rtl"` pour l'arabe
- Les e-mails envoyés par la bibliothèque (`EmailService` : vérification, réinitialisation du mot de passe et avis « un compte existe déjà »), dans la langue enregistrée du destinataire (`AuthUser.Locale`), à défaut dans la culture de la requête, à défaut en anglais

### Ce qui n'est PAS traduit {#what-is-not-localized}

- Les codes `error` lisibles par une machine (`"email_required"`, `"invalid_credentials"`, etc.) : ils font partie du contrat de l'API et restent constants
- Les codes d'erreur OAuth/OIDC et les descriptions d'erreur destinées aux développeurs sur les endpoints de jeton, d'autorisation et de révocation
- Les messages de journal internes et les messages d'exception

### Tester la localisation du serveur {#testing-server-localization}

Envoyez un en-tête `Accept-Language` à n'importe quel endpoint traduit :

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Fichiers de ressources {#resource-files}

Toutes les chaînes traduites du serveur se trouvent dans des fichiers `.resx` sous `src/Authagonal.Server/Resources/` :

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

## Interface de connexion {#login-ui}

La SPA de connexion utilise [react-i18next](https://react.i18next.com/) pour la localisation côté client. La langue est détectée automatiquement à partir du réglage `navigator.language` du navigateur.

Les langues enregistrées figurent dans un registre unique `LANGUAGES`, dans `login-app/src/i18n/index.ts`, qui pilote à la fois l'enregistrement des ressources i18next et chaque sélecteur de langue, de sorte que les deux ne peuvent pas diverger. Toutes les langues enregistrées apparaissent actuellement dans le sélecteur par défaut. `DEFAULT_LANGUAGES` est exporté séparément de `LANGUAGES` afin qu'une future langue à diffusion restreinte puisse être exclue des sélecteurs sans toucher aux points d'appel, mais aucune n'est exclue aujourd'hui. Les locataires peuvent aussi restreindre le sélecteur de la même manière : un tableau `languages` dans `branding.json` remplace entièrement la liste par défaut (voir [Personnalisation de l'interface](branding)).

La langue active est répercutée sur `<html lang>` et `<html dir>`, si bien que pour les langues qui s'écrivent de droite à gauche (`ar`), la carte d'authentification s'inverse automatiquement, y compris lorsque la langue est changée sur place via le sélecteur.

### Détection de la langue {#language-detection}

L'ordre de détection est le suivant :

1. **localStorage** : préférence conservée lors d'une visite précédente
2. **Paramètre de requête** : `?lng=de` prime sur la détection du navigateur
3. **Langue du navigateur** : `navigator.language` (automatique)
4. **Langue de repli** : anglais (`en`)

### Fichiers de traduction {#translation-files}

Les fichiers JSON de traduction sont intégrés à l'application dans `login-app/src/i18n/` :

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

### Libellés de la politique de mot de passe {#password-policy-labels}

La page de réinitialisation du mot de passe traduit côté client sa liste d'exigences de mot de passe à partir de la clé `rule` renvoyée par `GET /api/auth/password-policy` (en reprenant le `label` fourni par le serveur pour les règles non reconnues). Les exigences suivent ainsi la langue sélectionnée dans l'interface, même si l'en-tête `Accept-Language` du navigateur diffère. La page d'inscription affiche les valeurs `label` fournies par le serveur, qui sont traduites d'après `Accept-Language`.

### Utilisation via le paquet npm {#npm-package-consumers}

Si vous utilisez l'application de connexion via `@authagonal/login`, l'instance i18n est exportée :

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentation {#documentation}

Le site de documentation est organisé par répertoires. Les pages anglaises sont à la racine, et les traductions se trouvent dans des sous-répertoires par langue (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`, `/ja/`). Une liste déroulante dans la barre latérale permet de passer d'une langue à l'autre.

## Ajouter une langue {#adding-a-new-language}

Pour prendre en charge une nouvelle langue (par exemple l'italien, `it`) :

### 1. Serveur {#1-server}

Créez un nouveau fichier `.resx` en copiant le fichier anglais et en traduisant les valeurs :

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

Ajoutez `"it"` à `SupportedLocales.All` dans `src/Authagonal.Server/Services/SupportedLocales.cs`, la liste unique que lisent à la fois le middleware de localisation des requêtes et les pages rendues côté serveur :

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. Interface de connexion {#2-login-ui}

Créez un nouveau fichier JSON de traduction en copiant `en.json` et en traduisant les valeurs :

```
login-app/src/i18n/it.json
```

Enregistrez-le dans le tableau `LANGUAGES` de `login-app/src/i18n/index.ts`. Cette seule entrée enregistre la ressource i18next et ajoute la langue à chaque sélecteur :

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. Documentation {#3-documentation}

Créez un nouveau répertoire contenant les fichiers markdown traduits :

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

Ajoutez une valeur par défaut pour la langue dans `docs/_config.yml` :

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

Ajoutez l'option de langue au sélecteur dans `docs/_layouts/default.html`.

## Ajouter de nouvelles chaînes {#adding-new-strings}

### Serveur {#server}

1. Ajoutez la clé et la valeur anglaise à `SharedMessages.resx`
2. Ajoutez les valeurs traduites au fichier `.resx` de chaque langue
3. Utilisez `IStringLocalizer<SharedMessages>` pour accéder à la chaîne :

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interface de connexion {#login-ui-1}

1. Ajoutez la clé et la valeur anglaise à `en.json`
2. Ajoutez les valeurs traduites au fichier JSON de chaque langue
3. Utilisez la fonction `t()` dans les composants :

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
