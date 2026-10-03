---
layout: default
title: Personnalisation de l'apparence
locale: fr
---

# Personnaliser l'apparence de l'interface de connexion

La SPA de connexion se configure à l'exécution au moyen d'un fichier `branding.json` servi depuis la racine web. Aucune recompilation n'est nécessaire : il suffit de monter votre configuration et vos ressources.

## Fonctionnement {#how-it-works}

Au démarrage, la SPA récupère `/branding.json`. Si le fichier n'existe pas ou est inaccessible, les valeurs par défaut sont utilisées. (Un serveur hôte peut aussi intégrer la configuration directement dans la page, sous forme d'une charge utile d'amorçage `<script type="application/json" id="authagonal-boot">` ; lorsqu'elle est présente, la SPA la lit au lieu d'effectuer la requête.) La configuration contrôle :

- le nom de l'application (affiché dans l'en-tête et le titre de la page) ;
- l'image du logo, avec une « pastille » d'arrière-plan facultative propre à chaque mode ;
- la couleur principale (boutons, liens, anneaux de focus), avec une variante facultative pour le mode sombre ;
- les couleurs d'arrière-plan de la page et de la carte, pour chaque mode ;
- la visibilité des liens de mot de passe oublié et d'inscription ;
- le mode sombre par défaut (clair / selon le système d'exploitation / sombre) ;
- les options du sélecteur de langue ;
- le pied de page « Propulsé par Authagonal » ;
- du CSS personnalisé pour un style plus poussé.

## Nom de l'organisation {#organisation-name}

Sur un hôte multi-locataire qui résout une organisation pour la requête (par exemple un domaine personnalisé rattaché à un seul client), la charge utile `authagonal-boot` peut porter un troisième membre, à côté de `branding` et de `providers` :

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

`organization` vaut `null` (ou le membre est absent) lorsque la requête n'a été résolue vers aucune organisation : un déploiement mono-locataire, ou un déploiement sans domaine personnalisé rattaché pour cet hôte. Cette bibliothèque ne résout pas elle-même d'organisation pour une requête anonyme, antérieure à l'authentification (`OrganizationSelector` a besoin d'un `AuthUser` connecté) ; un hôte qui dispose de sa propre résolution avant authentification (Authagonal Cloud en rattache une par domaine personnalisé) définit `organization` lorsqu'il assemble la charge utile d'amorçage.

Lorsque `organization.name` est présent et diffère de `branding.appName` (comparaison insensible à la casse, après suppression des espaces en début et en fin, afin qu'une organisation portant le même nom que le locataire ne produise pas « Acme / Connexion à Acme »), la carte de connexion affiche un sous-titre sous le titre : « Connexion à {name} » (`data-testid="login-org-name"`, clé i18n `login.signingInTo`). Il est affiché une seule fois, par l'en-tête partagé `AuthLayout`, de sorte que chaque route montée par son intermédiaire (connexion, inscription, mot de passe oublié et réinitialisation, pages de défi et de configuration MFA, page d'appareil, consentement et consentement d'agent, octrois et compte) l'affiche de manière identique. Rien n'est affiché, et l'en-tête conserve son espacement normal, lorsque `organization` est absent, vaut `null` ou que son nom correspond à `branding.appName`.

## Configuration {#configuration}

Placez un fichier `branding.json` dans le répertoire `wwwroot/` (ou montez-le dans le conteneur Docker) :

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

### Options {#options}

| Propriété | Type | Valeur par défaut | Description |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Affiché dans l'en-tête et le titre de l'onglet du navigateur |
| `logoUrl` | `string \| null` | `null` | URL d'une image de logo. Lorsqu'elle est définie, elle remplace l'en-tête textuel. |
| `primaryColor` | `string` | `"#2563eb"` | Couleur hexadécimale des boutons, des liens et des indicateurs de focus |
| `supportEmail` | `string \| null` | `null` | Adresse e-mail de contact du support (réservée à un usage futur) |
| `showForgotPassword` | `boolean` | `true` | Affiche ou masque le lien « Mot de passe oublié ? » sur la page de connexion |
| `showRegistration` | `boolean` | `false` | Affiche ou masque le lien d'inscription en libre-service |
| `customCssUrl` | `string \| null` | `null` | URL d'un fichier CSS personnalisé chargé après les styles par défaut |
| `welcomeTitle` | `LocalizedString` | `null` | Message d'accueil facultatif affiché sous l'en-tête sur les pages d'authentification (chaîne simple ou `{ "en": "...", "de": "..." }`). Rien n'est affiché lorsqu'il n'est pas défini. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Ligne facultative sous `welcomeTitle`, sous la même forme. Rien n'est affiché lorsqu'elle n'est pas définie. |
| `languages` | `array \| null` | `null` | Options du sélecteur de langue (`[{ "code": "en", "label": "English" }, ...]`). `null` affiche toutes les langues livrées, à l'exception des locales fantaisie (voir [Localisation](localization)). |
| `poweredBy` | `boolean` | `true` | Affiche ou masque le pied de page « Propulsé par Authagonal » sur les pages d'authentification |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Thème par défaut lorsque le visiteur n'en a pas choisi : `"off"` (clair uniquement), `"auto"` (suit la préférence du système d'exploitation), `"force"` (toujours sombre). Le sélecteur de thème du visiteur reste prioritaire. |
| `lightBg` | `string \| null` | `null` | Couleur d'arrière-plan de la page en mode clair |
| `lightCardBg` | `string \| null` | `null` | Couleur d'arrière-plan de la carte ou du formulaire en mode clair |
| `darkBg` | `string \| null` | `null` | Couleur d'arrière-plan de la page en mode sombre |
| `darkCardBg` | `string \| null` | `null` | Couleur d'arrière-plan de la carte ou du formulaire en mode sombre |
| `darkPrimaryColor` | `string \| null` | `null` | Remplace `primaryColor` en mode sombre |
| `lightLogoBg` | `string \| null` | `null` | Arrière-plan de la pastille du logo en mode clair (voir ci-dessous) |
| `darkLogoBg` | `string \| null` | `null` | Arrière-plan de la pastille du logo en mode sombre (voir ci-dessous) |

Les valeurs de couleur doivent être une couleur hexadécimale (`#rgb`, `#rrggbb`, `#rrggbbaa`) ou une expression `rgb()`/`rgba()`/`hsl()`/`hsla()` ; toute autre valeur est ignorée. Les couleurs propres à chaque mode sont injectées sous forme d'une règle `<style id="branding-theme-vars">` après les styles intégrés : les valeurs `light*` sur `:root:where(:not(.dark))`, de sorte qu'elles ne s'appliquent jamais en mode sombre ; les valeurs sombres sur `.dark` ; et `primaryColor` sur `:root`, puisqu'il s'agit de la couleur de base des deux modes. `:where()` n'ajoute aucune spécificité ; `customCssUrl` les remplace donc toutes.

### Pastille d'arrière-plan du logo {#logo-background-chip}

Si votre logo comporte des éléments blancs ou transparents, il peut disparaître sur la carte claire. Définissez `lightLogoBg` et/ou `darkLogoBg` pour afficher le logo dans une « pastille » arrondie, avec une marge intérieure et cette couleur d'arrière-plan :

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

La pastille (un conteneur `data-auth="logo-chip"` piloté par la variable CSS `--auth-logo-bg`) ne reçoit sa marge intérieure et son arrière-plan que lorsqu'un arrière-plan de logo est configuré ; les locataires qui n'en définissent pas voient donc le logo directement sur la carte, exactement comme avant. Les deux champs sont indépendants : ne définissez que `lightLogoBg` pour placer le logo dans une pastille en mode clair et le laisser tel quel en mode sombre.

## Exemple Docker {#docker-example}

Montez vos fichiers de personnalisation dans le conteneur :

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Ou avec docker-compose :

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

## CSS personnalisé {#custom-css}

L'option `customCssUrl` charge une feuille de style supplémentaire après les styles par défaut ; vos règles sont donc prioritaires. Elle sert à changer de police, à ajuster les espacements ou à restyler des éléments précis. L'URL doit être de même origine (les URL relatives comme `/branding/custom.css` conviennent) ; les feuilles de style d'une autre origine sont ignorées sans avertissement.

### Propriétés CSS personnalisées {#css-custom-properties}

L'interface de connexion expose plusieurs propriétés CSS personnalisées pour un contrôle fin :

| Propriété | Valeur par défaut | Description |
|---|---|---|
| `--brand-primary` | `#2563eb` | Couleur principale des boutons, des liens et des anneaux de focus |
| `--auth-bg` | `#f3f4f6` | Couleur d'arrière-plan de la page |
| `--auth-card-bg` | `#ffffff` | Couleur d'arrière-plan de la carte ou du formulaire |
| `--auth-logo-bg` | `transparent` | Arrière-plan de la pastille du logo (la marge intérieure de la pastille n'apparaît que lorsqu'un arrière-plan de logo est configuré) |
| `--auth-radius` | `0.5rem` | Rayon de bordure de la carte d'authentification |
| `--auth-font` | *(héritée ; pile de polices système)* | Famille de polices de la carte d'authentification |
| `--auth-heading` | `#111827` | Couleur du texte des titres |

Les variables de couleur ci-dessus correspondent directement à des champs de configuration (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`) ; préférez donc la configuration pour les simples changements de couleur et réservez le CSS personnalisé au reste.

Redéfinissez-les dans votre CSS personnalisé :

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

L'interface de connexion utilise Tailwind CSS. Le CSS personnalisé peut cibler les éléments HTML standard et les classes utilitaires Tailwind. Les composants d'interface exportés (`Button`, `Input`, `Card`, `Alert`, etc.) utilisent Tailwind en interne.

## Mode sombre {#dark-mode}

La SPA de connexion est livrée avec les thèmes clair, sombre et **système**. Le sélecteur de thème est toujours visible dans la mise en page. Le choix de l'utilisateur est conservé dans `localStorage` sous la clé `auth-theme`.

### Fonctionnement {#how-it-works-1}

- **Par défaut** : tant que le visiteur n'a pas choisi de thème, l'option de personnalisation `darkMode` définit le thème par défaut : `"off"` (clair), `"auto"` (système, la valeur par défaut) ou `"force"` (sombre). Dès que le visiteur utilise le sélecteur, son choix l'emporte toujours.
- **Détection** : lorsque le thème est « système », la SPA observe `window.matchMedia('(prefers-color-scheme: dark)')` et réapplique automatiquement le thème lorsque la préférence du système d'exploitation change.
- **Application** : la SPA bascule une classe `.dark` sur `<html>`. La variante sombre de Tailwind (`&:where(.dark, .dark *)`) active les styles sombres compilés dans chaque composant.
- **Persistance** : les choix explicites « clair », « sombre » ou « système » sont conservés dans `localStorage`.

### Variables CSS {#css-variables}

Les valeurs claires sont déclarées sur `:root` ; les redéfinitions du mode sombre sont limitées à `.dark`, de sorte que la personnalisation du locataire dans `customCssUrl` est toujours prioritaire lorsqu'elle est fournie.

| Variable | Clair | Sombre |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (ou `lightBg`) | `#030712` (ou `darkBg`) |
| `--auth-card-bg` | `#ffffff` (ou `lightCardBg`) | `#111827` (ou `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (ou `lightLogoBg`) | `transparent` (ou `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (ou `primaryColor`) | la valeur claire (ou `darkPrimaryColor`) |

### Désactivation ou redéfinition {#disabling-or-overriding}

La personnalisation du locataire l'emporte toujours. Pour imposer un seul thème, définissez vos propres valeurs dans `customCssUrl` :

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

Pour supprimer complètement le sélecteur de thème, passez par le paquet npm, importez `AuthLayout` et effectuez le rendu sans le sélecteur, ou forkez la SPA.

### Attributs de données {#data-attributes}

Tous les éléments du formulaire de connexion portent des attributs `data-auth` pour le ciblage CSS et l'automatisation des tests :

| Attribut | Élément |
|---|---|
| `data-auth="page"` | Conteneur principal de la page |
| `data-auth="header"` | Section d'en-tête |
| `data-auth="logo-chip"` | Conteneur autour de l'image du logo (avec marge intérieure uniquement lorsqu'un arrière-plan de logo est défini) |
| `data-auth="logo"` | Image du logo |
| `data-auth="app-name"` | Titre portant le nom de l'application |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | Les lignes facultatives `welcomeTitle` / `welcomeSubtitle` (présentes uniquement lorsqu'elles sont définies) |
| `data-auth="content"` | Zone de contenu principale |
| `data-auth="languages"` | Sélecteur de langue |
| `data-auth="language-trigger"` | Bouton d'ouverture du sélecteur de langue |
| `data-auth="theme-toggle"` | Sélecteur de thème clair/système/sombre |
| `data-auth="powered-by"` | Pied de page « Propulsé par Authagonal » |
| `data-auth="login-form"`, `"email-field"`, `"password-field"`, `"submit-button"` | Le formulaire de connexion et ses éléments (page de connexion uniquement) |

Ciblez-les dans votre CSS personnalisé :

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Exemple : arrière-plan et police personnalisés {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveaux de personnalisation {#customization-tiers}

| Niveau | Ce que vous faites | Mises à jour |
|---|---|---|
| **Configuration seule** | Monter `branding.json` et le logo | Transparentes : mettez à jour l'image Docker et conservez vos montages |
| **Configuration + CSS** | Ajouter `customCssUrl` avec des redéfinitions de style | Identiques : les classes CSS sont stables |
| **Paquet npm** | `npm install @authagonal/login`, personnaliser `branding.json`, compiler dans `wwwroot/` | Possibles : `npm update` récupère les nouvelles versions |
| **Forker la SPA** | Cloner `login-app/`, modifier le code source, compiler votre propre version | L'interface vous appartient ; les mises à jour du serveur sont indépendantes |
| **Écrire la vôtre** | Créer un frontend entièrement personnalisé reposant sur l'API d'authentification | Contrôle total ; voir [API d'authentification](auth-api) pour le contrat |

Voir `demos/custom-server/` pour un exemple fonctionnel avec une personnalisation (thème vert, « Acme Corp »).
