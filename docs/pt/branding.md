---
layout: default
title: Personalização da marca
locale: pt
---

# Personalizar a marca da interface de início de sessão

A SPA de início de sessão é configurável em tempo de execução através de um ficheiro `branding.json` servido a partir da raiz web. Não é necessário recompilar; basta montar a sua configuração e os seus recursos.

## Como funciona {#how-it-works}

No arranque, a SPA obtém `/branding.json`. Se o ficheiro não existir ou estiver inacessível, são usadas as predefinições. (Um servidor anfitrião também pode incorporar a configuração como payload de arranque `<script type="application/json" id="authagonal-boot">`; quando presente, a SPA lê-o em vez de fazer o pedido.) A configuração controla:

- O nome da aplicação (apresentado no cabeçalho e no título da página)
- A imagem do logótipo, com um "chip" de fundo opcional por modo
- A cor principal (botões, hiperligações, anéis de foco), com uma variante opcional para o modo escuro
- As cores de fundo da página e do cartão, por modo
- A visibilidade das hiperligações de recuperação da palavra-passe e de registo
- O modo escuro predefinido (claro / seguir o SO / escuro)
- As opções do seletor de idioma
- O rodapé "Com tecnologia da Authagonal"
- CSS personalizado para um estilo mais aprofundado

## Nome da organização {#organisation-name}

Num anfitrião multi-inquilino que resolve uma organização para o pedido (por exemplo, um domínio personalizado associado
a um cliente), o payload `authagonal-boot` pode transportar um terceiro membro, a par de `branding` e
`providers`:

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

`organization` é `null` (ou o membro está ausente) quando o pedido não resolveu nenhuma organização:
uma implementação de inquilino único, ou uma sem associação de domínio personalizado para este anfitrião. Esta biblioteca
não resolve, ela própria, uma organização para um pedido anónimo anterior à autenticação (`OrganizationSelector`
precisa de um `AuthUser` com sessão iniciada); um anfitrião com a sua própria resolução anterior à autenticação (o
Authagonal Cloud associa uma por domínio personalizado) define `organization` quando monta o payload de arranque.

Quando `organization.name` está presente e difere de `branding.appName` (sem distinção entre maiúsculas e minúsculas e
ignorando espaços nas extremidades, para que uma organização com o mesmo nome do inquilino não produza "Acme / A entrar em Acme"), o cartão de início de sessão apresenta um subtítulo sob o título: "A entrar em {name}"
(`data-testid="login-org-name"`, chave i18n `login.signingInTo`). É apresentado uma única vez, pelo cabeçalho partilhado
`AuthLayout`, pelo que todas as rotas montadas através dele (início de sessão, registo, recuperação/reposição da
palavra-passe, as páginas de desafio e de configuração de MFA, a página de dispositivo, o consentimento e o
consentimento de agentes, as concessões e a conta) o mostram de forma idêntica. Nada é apresentado, e o cabeçalho mantém
o espaçamento normal, quando `organization` está ausente, é `null` ou o seu nome coincide com `branding.appName`.

## Configuração {#configuration}

Coloque um ficheiro `branding.json` no diretório `wwwroot/` (ou monte-o no contentor Docker):

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

### Opções {#options}

| Propriedade | Tipo | Predefinição | Descrição |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Apresentado no cabeçalho e no título do separador do browser |
| `logoUrl` | `string \| null` | `null` | URL de uma imagem de logótipo. Quando definido, substitui o cabeçalho de texto. |
| `primaryColor` | `string` | `"#2563eb"` | Cor hexadecimal para botões, hiperligações e indicadores de foco |
| `supportEmail` | `string \| null` | `null` | Email de contacto do suporte (reservado para utilização futura) |
| `showForgotPassword` | `boolean` | `true` | Mostra/oculta a hiperligação "Esqueceu a senha?" na página de início de sessão |
| `showRegistration` | `boolean` | `false` | Mostra/oculta a hiperligação de registo self-service |
| `customCssUrl` | `string \| null` | `null` | URL de um ficheiro CSS personalizado carregado depois dos estilos predefinidos |
| `welcomeTitle` | `LocalizedString` | `null` | Saudação opcional apresentada sob o cabeçalho nas páginas de autenticação (string simples ou `{ "en": "...", "de": "..." }`). Nada é apresentado quando não está definida. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Linha opcional sob `welcomeTitle`, no mesmo formato. Nada é apresentado quando não está definida. |
| `languages` | `array \| null` | `null` | Opções do seletor de idioma (`[{ "code": "en", "label": "English" }, ...]`). `null` mostra todos os idiomas incluídos, exceto as localizações de novidade (consulte [Localização](localization)). |
| `poweredBy` | `boolean` | `true` | Mostra/oculta o rodapé "Com tecnologia da Authagonal" nas páginas de autenticação |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Tema predefinido quando o visitante ainda não escolheu nenhum: `"off"` (apenas claro), `"auto"` (seguir a preferência do SO), `"force"` (sempre escuro). O seletor de tema do visitante continua a prevalecer. |
| `lightBg` | `string \| null` | `null` | Cor de fundo da página no modo claro |
| `lightCardBg` | `string \| null` | `null` | Cor de fundo do cartão/formulário no modo claro |
| `darkBg` | `string \| null` | `null` | Cor de fundo da página no modo escuro |
| `darkCardBg` | `string \| null` | `null` | Cor de fundo do cartão/formulário no modo escuro |
| `darkPrimaryColor` | `string \| null` | `null` | Substitui `primaryColor` no modo escuro |
| `lightLogoBg` | `string \| null` | `null` | Fundo do chip do logótipo no modo claro (ver abaixo) |
| `darkLogoBg` | `string \| null` | `null` | Fundo do chip do logótipo no modo escuro (ver abaixo) |

Os valores de cor têm de ser uma cor hexadecimal (`#rgb`, `#rrggbb`, `#rrggbbaa`) ou uma expressão `rgb()`/`rgba()`/`hsl()`/`hsla()`; qualquer outro valor é ignorado. As cores por modo são injetadas como uma regra `<style id="branding-theme-vars">` depois dos estilos incluídos: os valores `light*` em `:root:where(:not(.dark))`, para que nunca se apliquem no modo escuro; os valores escuros em `.dark`; e `primaryColor` em `:root`, por ser a cor base de ambos os modos. `:where()` não acrescenta especificidade, pelo que `customCssUrl` continua a sobrepor-se a todas elas.

### Chip de fundo do logótipo {#logo-background-chip}

Se o seu logótipo tiver elementos brancos ou transparentes, pode desaparecer contra o cartão claro. Defina `lightLogoBg` e/ou `darkLogoBg` para apresentar o logótipo dentro de um "chip" arredondado, com margem interior e essa cor de fundo:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

O chip (um wrapper `data-auth="logo-chip"` controlado pela variável CSS `--auth-logo-bg`) só recebe margem interior e fundo quando está configurado um fundo de logótipo, pelo que os inquilinos que não o definem veem o logótipo encostado ao cartão exatamente como antes. Os dois campos são independentes: defina apenas `lightLogoBg` para colocar o logótipo num chip no modo claro e deixá-lo sem chip no modo escuro.

## Exemplo com Docker {#docker-example}

Monte os seus ficheiros de marca no contentor:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Ou com docker-compose:

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

A opção `customCssUrl` carrega uma folha de estilos adicional depois dos estilos predefinidos, pelo que as suas regras têm precedência. É útil para mudar tipos de letra, ajustar espaçamentos ou alterar o estilo de elementos específicos. O URL tem de ter a mesma origem (URLs relativos como `/branding/custom.css` são aceites); as folhas de estilos de outra origem são ignoradas silenciosamente.

### Propriedades personalizadas de CSS {#css-custom-properties}

A interface de início de sessão expõe várias propriedades personalizadas de CSS para um controlo detalhado:

| Propriedade | Predefinição | Descrição |
|---|---|---|
| `--brand-primary` | `#2563eb` | Cor principal para botões, hiperligações e anéis de foco |
| `--auth-bg` | `#f3f4f6` | Cor de fundo da página |
| `--auth-card-bg` | `#ffffff` | Cor de fundo do cartão/formulário |
| `--auth-logo-bg` | `transparent` | Fundo do chip do logótipo (a margem interior do chip só aparece quando está configurado um fundo de logótipo) |
| `--auth-radius` | `0.5rem` | Raio dos cantos do cartão de autenticação |
| `--auth-font` | *(herdado; pilha de tipos de letra do sistema)* | Família de tipo de letra do cartão de autenticação |
| `--auth-heading` | `#111827` | Cor do texto dos títulos |

As variáveis de cor aqui indicadas correspondem diretamente a campos de configuração (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), pelo que deve preferir a configuração para alterações simples de cor e reservar o CSS personalizado para tudo o resto.

Substitua-as no seu CSS personalizado:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

A interface de início de sessão usa Tailwind CSS. O CSS personalizado pode visar elementos HTML padrão e classes utilitárias do Tailwind. Os componentes de interface exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usam Tailwind internamente.

## Modo escuro {#dark-mode}

A SPA de início de sessão inclui os temas claro, escuro e **sistema**. O seletor de tema está sempre visível no layout. A escolha do utilizador é guardada em `localStorage` sob a chave `auth-theme`.

### Como funciona {#how-it-works-1}

- **Predefinição**: até o visitante escolher um tema, a opção de marca `darkMode` define a predefinição: `"off"` (claro), `"auto"` (sistema, a predefinição) ou `"force"` (escuro). Assim que o visitante usa o seletor, a sua escolha prevalece sempre.
- **Deteção**: quando o tema é "sistema", a SPA observa `window.matchMedia('(prefers-color-scheme: dark)')` e volta a aplicar o tema automaticamente à medida que a preferência do SO muda.
- **Aplicação**: a SPA alterna uma classe `.dark` em `<html>`. A variante dark do Tailwind (`&:where(.dark, .dark *)`) ativa os estilos escuros compilados em todos os componentes.
- **Persistência**: as escolhas explícitas "claro" / "escuro" / "sistema" são guardadas em `localStorage`.

### Variáveis CSS {#css-variables}

Os valores claros são declarados em `:root`; as substituições do modo escuro estão limitadas a `.dark`, pelo que a marca do inquilino em `customCssUrl` tem sempre precedência quando é fornecida.

| Variável | Claro | Escuro |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (ou `lightBg`) | `#030712` (ou `darkBg`) |
| `--auth-card-bg` | `#ffffff` (ou `lightCardBg`) | `#111827` (ou `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (ou `lightLogoBg`) | `transparent` (ou `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (ou `primaryColor`) | o valor claro (ou `darkPrimaryColor`) |

### Desativar ou substituir {#disabling-or-overriding}

A marca do inquilino prevalece sempre. Para impor um único tema, defina os seus próprios valores em `customCssUrl`:

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

Para remover completamente o seletor de tema, use a via do pacote npm, importe `AuthLayout` e apresente-o sem o seletor, ou faça um fork da SPA.

### Atributos de dados {#data-attributes}

Todos os elementos do formulário de início de sessão têm atributos `data-auth` para seleção por CSS e automatização de testes:

| Atributo | Elemento |
|---|---|
| `data-auth="page"` | Wrapper principal da página |
| `data-auth="header"` | Secção do cabeçalho |
| `data-auth="logo-chip"` | Wrapper em torno da imagem do logótipo (só tem margem interior quando está definido um fundo de logótipo) |
| `data-auth="logo"` | Imagem do logótipo |
| `data-auth="app-name"` | Título com o nome da aplicação |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | As linhas opcionais `welcomeTitle` / `welcomeSubtitle` (só presentes quando definidas) |
| `data-auth="content"` | Área de conteúdo principal |
| `data-auth="languages"` | Seletor de idioma |
| `data-auth="language-trigger"` | Botão que abre o seletor de idioma |
| `data-auth="theme-toggle"` | Seletor de tema claro/sistema/escuro |
| `data-auth="powered-by"` | Rodapé "Com tecnologia da Authagonal" |
| `data-auth="login-form"`, `"email-field"`, `"password-field"`, `"submit-button"` | O formulário de início de sessão e as suas partes (apenas na página de início de sessão) |

Vise-os no seu CSS personalizado:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Exemplo: fundo e tipo de letra personalizados {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Níveis de personalização {#customization-tiers}

| Nível | O que faz | Via de atualização |
|---|---|---|
| **Apenas configuração** | Montar `branding.json` + logótipo | Sem atritos: atualize a imagem Docker e mantenha as suas montagens |
| **Configuração + CSS** | Acrescentar `customCssUrl` com substituições de estilo | Igual; as classes CSS são estáveis |
| **Pacote npm** | `npm install @authagonal/login`, personalizar `branding.json`, compilar para `wwwroot/` | Atualizável: `npm update` obtém as novas versões |
| **Fork da SPA** | Clonar `login-app/`, modificar o código-fonte, compilar a sua própria versão | A interface é sua; as atualizações do servidor são independentes |
| **Escrever a sua própria** | Construir um frontend totalmente personalizado sobre a API de autenticação | Controlo total; consulte [API de autenticação](auth-api) para o contrato |

Consulte `demos/custom-server/` para um exemplo funcional com marca personalizada (tema verde, "Acme Corp").
