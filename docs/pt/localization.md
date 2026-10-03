---
layout: default
title: Localização
locale: pt
---

# Localização

A interface de início de sessão inclui onze locales de origem: inglês, chinês simplificado (`zh-Hans`), alemão (`de`), francês (`fr`), espanhol (`es`), vietnamita (`vi`), português (`pt`), árabe (`ar`), africânder (`af`), hindi (`hi`) e japonês (`ja`). As respostas da API do servidor e as páginas renderizadas no servidor também estão localizadas nos onze. A localização abrange as respostas da API do servidor, a interface de início de sessão e este site de documentação.

## Idiomas suportados {#supported-languages}

| Código | Idioma | Interface de início de sessão | API do servidor |
|---|---|---|---|
| `en` | Inglês (predefinido) | ✓ | ✓ |
| `zh-Hans` | Chinês simplificado | ✓ | ✓ |
| `de` | Alemão | ✓ | ✓ |
| `fr` | Francês | ✓ | ✓ |
| `es` | Espanhol | ✓ | ✓ |
| `vi` | Vietnamita | ✓ | ✓ |
| `pt` | Português | ✓ | ✓ |
| `ar` | Árabe (da direita para a esquerda) | ✓ | ✓ |
| `af` | Africânder | ✓ | ✓ |
| `hi` | Hindi | ✓ | ✓ |
| `ja` | Japonês | ✓ | ✓ |

## Servidor (respostas da API) {#server-api-responses}

O servidor utiliza a localização integrada do ASP.NET Core com `IStringLocalizer<T>` e ficheiros de recursos `.resx`. O idioma é selecionado a partir do cabeçalho HTTP `Accept-Language`.

### O que está localizado {#what-is-localized}

- Mensagens de erro de validação da palavra-passe
- Etiquetas da política de palavras-passe (`GET /api/auth/password-policy`)
- Mensagens do fluxo de redefinição da palavra-passe (erros de token, expiração, sucesso)
- Descrições de erro genéricas do middleware de tratamento de exceções
- Mensagens da gestão de utilizadores pelo administrador (confirmação de email, verificação, etc.)
- Mensagem de confirmação do fim de sessão
- Páginas renderizadas no servidor (resultado da confirmação de email, páginas de confirmação de fim de sessão e de sessão terminada), incluindo `<html lang>` e `dir="rtl"` para árabe
- Emails enviados pela biblioteca (`EmailService`: verificação, redefinição da palavra-passe e o aviso de "a conta já existe"), no locale guardado do destinatário (`AuthUser.Locale`) ou, na sua falta, na cultura do pedido ou, por fim, em inglês

### O que NÃO está localizado {#what-is-not-localized}

- Códigos `error` legíveis por máquina (`"email_required"`, `"invalid_credentials"`, etc.): são contratos da API e permanecem constantes
- Códigos de erro OAuth/OIDC e descrições de erro destinadas a programadores nos endpoints de token, de autorização e de revogação
- Mensagens de registo internas e mensagens de exceção

### Testar a localização do servidor {#testing-server-localization}

Envie um cabeçalho `Accept-Language` para qualquer endpoint localizado:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Ficheiros de recursos {#resource-files}

Todas as cadeias de tradução do servidor estão em ficheiros `.resx` em `src/Authagonal.Server/Resources/`:

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

## Interface de início de sessão {#login-ui}

A SPA de início de sessão utiliza o [react-i18next](https://react.i18next.com/) para a localização do lado do cliente. O idioma é detetado automaticamente a partir da definição `navigator.language` do browser.

Os locales registados estão num único registo `LANGUAGES` em `login-app/src/i18n/index.ts`, que determina tanto o registo de recursos do i18next como todos os seletores de idioma, pelo que os dois não podem divergir. Atualmente, todos os locales registados aparecem no seletor predefinido. `DEFAULT_LANGUAGES` é exportado separadamente de `LANGUAGES` para que um futuro locale restrito possa ser excluído dos seletores sem alterar os pontos de chamada, mas hoje nada é excluído. Os inquilinos também podem restringir o seletor da mesma forma: uma matriz `languages` em `branding.json` substitui por completo a lista predefinida (consulte [Personalização visual](branding)).

O idioma ativo é refletido em `<html lang>` e `<html dir>`, pelo que os idiomas da direita para a esquerda (`ar`) invertem automaticamente o cartão de autenticação, incluindo quando o idioma é mudado no próprio local através do seletor.

### Deteção do idioma {#language-detection}

A ordem de deteção é:

1. **localStorage**: preferência guardada de uma visita anterior
2. **Parâmetro de consulta**: `?lng=de` sobrepõe-se à deteção do browser
3. **Idioma do browser**: `navigator.language` (automático)
4. **Alternativa**: inglês (`en`)

### Ficheiros de tradução {#translation-files}

Os ficheiros JSON de tradução são incluídos na aplicação em `login-app/src/i18n/`:

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

### Etiquetas da política de palavras-passe {#password-policy-labels}

A página de redefinição da palavra-passe traduz a sua lista de requisitos da palavra-passe do lado do cliente, com base na chave `rule` devolvida por `GET /api/auth/password-policy` (recorrendo à `label` fornecida pelo servidor para regras não reconhecidas). Isto garante que os requisitos seguem o idioma selecionado na interface, mesmo que o cabeçalho `Accept-Language` do browser seja diferente. A página de registo apresenta os valores `label` fornecidos pelo servidor, que são localizados a partir de `Accept-Language`.

### Consumidores do pacote npm {#npm-package-consumers}

Se consumir a aplicação de início de sessão através de `@authagonal/login`, a instância i18n é exportada:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentação {#documentation}

O site de documentação utiliza uma abordagem baseada em diretórios. As páginas em inglês estão na raiz e as traduções estão em subdiretórios por locale (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`, `/ja/`). Uma lista pendente de seleção de idioma na barra lateral permite alternar entre idiomas.

## Adicionar um novo idioma {#adding-a-new-language}

Para adicionar suporte a um novo idioma (por exemplo, italiano `it`):

### 1. Servidor {#1-server}

Crie um novo ficheiro `.resx` copiando o ficheiro em inglês e traduzindo os valores:

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

Adicione `"it"` a `SupportedLocales.All` em `src/Authagonal.Server/Services/SupportedLocales.cs`, a lista única que é lida tanto pelo middleware de localização de pedidos como pelas páginas renderizadas no servidor:

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. Interface de início de sessão {#2-login-ui}

Crie um novo ficheiro JSON de tradução copiando `en.json` e traduzindo os valores:

```
login-app/src/i18n/it.json
```

Registe-o na matriz `LANGUAGES` em `login-app/src/i18n/index.ts`. Essa única entrada regista o recurso do i18next e adiciona o idioma a todos os seletores:

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. Documentação {#3-documentation}

Crie um novo diretório com os ficheiros markdown traduzidos:

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

Adicione uma predefinição de locale em `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

Adicione a opção de idioma ao seletor em `docs/_layouts/default.html`.

## Adicionar novas cadeias {#adding-new-strings}

### Servidor {#server}

1. Adicione a chave e o valor em inglês a `SharedMessages.resx`
2. Adicione os valores traduzidos ao ficheiro `.resx` de cada locale
3. Utilize `IStringLocalizer<SharedMessages>` para aceder à cadeia:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interface de início de sessão {#login-ui-1}

1. Adicione a chave e o valor em inglês a `en.json`
2. Adicione os valores traduzidos ao ficheiro JSON de cada locale
3. Utilize a função `t()` nos componentes:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
