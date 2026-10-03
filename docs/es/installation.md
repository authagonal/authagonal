---
layout: default
title: Instalación
locale: es
---

# Instalación

## Docker (recomendado) {#docker-recommended}

Descargue y ejecute la imagen precompilada:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Para el desarrollo local con Azurite (emulador de Azure Storage):

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` es un ajuste de desarrollo.** RFC 6749 §3.1/§3.2 exigen TLS en los endpoints de autorización y de token, por lo que Authagonal rechaza las solicitudes que no son https a `/connect/*` a menos que esté activado. El esquema se lee después del procesamiento de los encabezados reenviados, así que un proxy que termine TLS y reenvíe `X-Forwarded-Proto: https` cumple el requisito con el ajuste desactivado, que es lo que debe hacer todo despliegue al que pueda llegar alguien que no sea usted. Con él activado, un observador situado en la ruta lee el código de autorización, el secreto del cliente en el encabezado `Authorization: Basic` y los tokens de acceso y de actualización. Consulte [Configuración](configuration#authentication).

## Compilar desde el código fuente {#building-from-source}

### Requisitos previos {#prerequisites}

- SDK de .NET 10
- Node.js 24+

Authagonal tiene como destino `net9.0` y `net10.0`, y requiere en tiempo de ejecución un framework compartido **con los parches aplicados**: **como mínimo 9.0.18 o 10.0.10**. Consulte la [lista de comprobación de seguridad para producción](#production-security-checklist) para saber por qué, y `Auth:RequireMinimumRuntime` para convertir la comprobación de inicio en un rechazo.

### Compilación {#build}

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Compilación de Docker {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como biblioteca (NuGet) {#as-a-library-nuget}

Haga referencia a los paquetes de Authagonal en su propio proyecto de ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

El paquete del proveedor de almacenamiento es intercambiable: `Authagonal.AzureProvider` para Azure Table Storage (la configuración predeterminada de `AddAuthagonal()`), `Authagonal.SqlProvider` para PostgreSQL o SQLite autoalojados (consulte [Backend SQL](#sql-backend)), o `Authagonal.AwsProvider` para DynamoDB / S3 / Secrets Manager (consulte [Backend de AWS](#aws-backend)).

> **El orden de registro importa.** Un proveedor de almacenamiento debe registrarse **antes** de `AddAuthagonal()`. Ese registro previo de `IUserStore` es lo que hace que `AddAuthagonal()` omita su configuración integrada de Azure Table Storage; un proveedor registrado después pierde, sin aviso, todas las interfaces que `AddAuthagonal()` ya ha cubierto, porque esos registros usan `TryAdd`.
>
> Tres interfaces (`IOrganizationStore`, `IOrganizationMembershipStore` e `IScimGroupRoleMappingStore`) tienen implementaciones de reserva en memoria, vacías y de solo lectura, para que la inyección de dependencias se resuelva en un host que no configure ningún almacén para ellas. `AddAuthagonal()` aparta esas implementaciones de reserva antes de que se registre el proveedor de almacenamiento y las restaura con `TryAdd` después, de modo que el almacén persistente de un proveedor siempre prevalece y la implementación de reserva sigue cubriendo un host que no tenga uno. Si registra su propia implementación de cualquiera de las tres, regístrela antes de `AddAuthagonal()`, como cualquier otro almacén.

Después, compóngalo en su `Program.cs`:

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Consulte [Extensibilidad](extensibility) para ver todos los puntos de sustitución y [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) para un ejemplo completo.

### Correo electrónico {#email}

El remitente integrado de [Resend](https://resend.com) se activa automáticamente cuando se configuran `Email:ResendApiKey` y `Email:SenderEmail`, sin necesidad de registrar ningún servicio. Sin ningún `IEmailService`, los correos de verificación y de restablecimiento de contraseña se **descartan sin aviso** y, como el inicio de sesión exige por defecto un correo electrónico confirmado, los usuarios autorregistrados nunca podrán iniciar sesión (`UseAuthagonal` registra una advertencia al iniciar). Establezca las claves `Email:*`, registre su propio `IEmailService` antes de `AddAuthagonal()` o incluya sus dominios en `Auth:AutoConfirmEmailDomains` para omitir la verificación (solo para desarrollo y pruebas). Consulte [Configuración → Correo electrónico](configuration#email).

## Backend SQL {#sql-backend}

Para ejecutarlo sobre su propia base de datos en lugar de un servicio en la nube, haga referencia a `Authagonal.SqlProvider` y regístrelo **antes** de `AddAuthagonal()`; esos registros son los que hacen que `AddAuthagonal()` omita su configuración de Azure Table Storage:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Las tablas reproducen una a una las estructuras de Azure y DynamoDB y se crean al iniciar si no existen (cada sentencia es `IF NOT EXISTS`, por lo que es seguro que varios pods compitan por ejecutarlas, y no hacen nada frente a un esquema que haya aprovisionado usted mismo). No se necesita ninguna configuración `Storage:*`. El conjunto de claves de DataProtection se persiste en la misma base de datos, de modo que las cookies y los tokens antifalsificación sobreviven a los reinicios y funcionan entre pods sin ningún servicio adicional.

SQLite serializa a quienes escriben, por lo que es un backend de un solo nodo: el arrendamiento (lease) en proceso y el bus de eventos de clúster registrados por defecto son la combinación correcta en ese caso. Un despliegue de PostgreSQL con varios pods requiere `clustering.UseSql(dataSource)` para la elección de líder.

> **Intercalación.** En PostgreSQL, las columnas de clave se fijan a `COLLATE "C"`. El esquema de claves es ordinal por bytes en todas partes (límites de prefijo, rangos de partición por entorno, el barrido de caducidad de concesiones, la paginación por conjunto de claves), y una base de datos creada con una intercalación lingüística (`en_US.UTF-8` y las configuraciones regionales de ICU son los valores predeterminados habituales) ordenaría de forma distinta los signos de puntuación y las mayúsculas, y devolvería sin aviso filas incorrectas. Esta fijación hace que la estructura sea independiente de cómo se creó la base de datos; no es necesario crearla de ninguna forma concreta.

> ⚠️ **El material de claves reside en esa base de datos.** En Azure, la clave de firma de tokens está en Table Storage y el conjunto de claves de DataProtection en un contenedor de Blob, cada uno con un RBAC que se concede de forma independiente; en AWS, en DynamoDB y S3. En SQL ambos son tablas detrás de la misma cadena de conexión que todo lo demás, así que trate la cadena de conexión como equivalente a la clave de firma: de lo contrario, un `pg_dump`, una réplica de lectura, un rol de análisis con `SELECT` o una copia de seguridad restaurada proporcionan a la vez la capacidad de emitir tokens para cualquier sujeto y las claves de todas las cookies de autenticación. Registre un `IFieldCipher` antes de `AddAuthagonalPostgres()` para cifrar `SigningKeys.keyMaterialJson` en reposo, y establezca `DataProtection:KeyVaultKeyId` o `DataProtection:CertificateThumbprint` para que el conjunto de claves no se almacene con un `<masterKey>` sin protección: un despliegue nuevo que persista el conjunto de claves sin ninguno de los dos se rechaza al iniciar, y uno existente recibe una advertencia de nivel `Critical` en cada inicio. Consulte el [README del paquete](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys) para ambos casos y para apuntar el conjunto de claves a un esquema independiente con su propio rol.

Consulte el [README del paquete](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) para ver la estructura de las tablas, las primitivas de concurrencia detrás de cada garantía de uso único y cómo añadir un dialecto para otro motor.

## Backend de AWS {#aws-backend}

Para ejecutarlo en AWS en lugar de Azure, haga referencia a `Authagonal.AwsProvider` y registre el paquete de AWS **antes** de `AddAuthagonal()`; esos registros son los que hacen que `AddAuthagonal()` omita su configuración de Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Las tablas de DynamoDB reproducen una a una la estructura de Azure y se garantizan al iniciar (de forma idempotente; no hacen nada cuando ya las ha aprovisionado Terraform). Las credenciales se resuelven mediante la cadena estándar de AWS (entorno / rol de instancia de EC2 / IRSA), por lo que no hay división entre cadena de conexión e identidad administrada y no se necesita ninguna configuración `Storage:*`.

> ⚠️ **Claves de DataProtection en S3.** Sin un cliente de S3 y un bucket, el conjunto de claves de Data Protection de ASP.NET Core se mantiene en memoria, lo cual sirve para un único nodo en desarrollo, pero en producción las cookies y los tokens antifalsificación dejan de funcionar tras un reinicio y entre nodos. Pase siempre el cliente de S3 y el bucket en un despliegue de producción en AWS.

## SPA de inicio de sesión (npm) {#login-spa-npm}

La interfaz de inicio de sesión se publica como paquete npm para su personalización:

```bash
npm install @authagonal/login react react-dom react-router
```

El paquete incluye JS y CSS compilados; importe los componentes y los estilos directamente en su propia aplicación React. Consulte [Servidor personalizado](custom-server) para ver un recorrido completo.

`react`, `react-dom` y `react-router` son dependencias **peer**: la compilación las externaliza, de modo que los componentes usan las copias de su aplicación en lugar de las suyas propias. Eso es lo que permite que las páginas exportadas llamen a `useNavigate` dentro de su `<BrowserRouter>` y ejecuten sus hooks contra la instancia de React que las renderiza. Instálelas junto al paquete; no deje que este traiga las suyas.

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

Si su SPA llama a API con un token de portador, guarde el token en un BFF en lugar de en el navegador. El BFF se publica como paquete NuGet (`Authagonal.Bff`) y como paquete npm (`@authagonal/bff`); ninguno de los dos forma parte de la imagen del servidor. Consulte [Backend-for-Frontend](bff).

## Lista de comprobación de seguridad para producción {#production-security-checklist}

Antes de exponer Authagonal a tráfico real, confirme lo siguiente. Cada punto se detalla en la página de [Configuración](configuration).

- **Ejecútelo sobre un runtime de .NET con los parches aplicados: como mínimo 9.0.18 o 10.0.10.** Las correcciones de GHSA-37gx-xxp4-5rgx y GHSA-w3x6-4m5h-cxqf (un bucle infinito y una pareja de XXE / agotamiento de recursos en `System.Security.Cryptography.Xml`, ambos alcanzables desde el endpoint ACS de SAML **anónimo**) se distribuyen en el framework compartido, no en ningún paquete al que Authagonal pueda hacer referencia, por lo que nada en su grafo de dependencias puede garantizarlas. Authagonal registra un mensaje `Critical` al iniciar cuando el runtime en ejecución está por debajo del mínimo; establezca `Auth:RequireMinimumRuntime = true` para que, en su lugar, se niegue a iniciarse. Las imágenes de contenedor publicadas ya usan un runtime igual o superior al mínimo.
- **Ejecútelo detrás de un proxy que termine TLS y declárelo.** Authagonal debe situarse detrás de un proxy inverso / ingress que termine TLS (o terminar TLS por sí mismo). HSTS solo se emite sobre HTTPS y `/connect/*` rechaza el texto plano, por lo que el proxy debe reenviar `X-Forwarded-Proto: https`, y ese encabezado se ignora a menos que establezca `ForwardedHeaders:KnownNetworks` (o `KnownProxies`) en el CIDR / la dirección de su proxy. Use `["0.0.0.0/0", "::/0"]` si el proxy no tiene una dirección fija y nada más puede llegar al proceso. `ForwardedHeaders:ForwardLimit` es `1` por defecto (solo se confía en el último salto).
- **Establezca `SecretProvider:VaultUri`.** El proveedor de secretos predeterminado es **de texto plano**: sin Key Vault, los secretos de cliente de OIDC de origen y las semillas de TOTP / MFA se almacenan sin cifrar en Table Storage (y en las copias de seguridad). Configure Key Vault en cualquier despliegue de producción.
- **Restrinja la API de administración.** `AdminApi:Enabled` es **true** por defecto. El ámbito de administración (`AdminApi:Scope`, por defecto `authagonal-admin`) concede gestión completa y suplantación de usuarios. Restrinja a nivel de red las rutas de administración `/api/v1/*` y controle estrictamente a quién se emite el ámbito de administración, o establezca `AdminApi:Enabled = false` si no lo usa.
- **Proteja los endpoints internos.** Establezca `Cluster:Secret` para que el endpoint interno `/_internal/backchannel-logout` exija el encabezado `X-Cluster-Secret` (comparado en tiempo constante). Sin secreto, el endpoint no autoriza a **nadie** y responde 404: una dirección de origen no es una credencial, y loopback es lo que presenta un proxy inverso en el mismo host para cada solicitud que reenvía. `Cluster:AllowLoopbackWithoutSecret` vuelve a admitir a un par de loopback anterior al reenvío, solo para desarrollo local. Nada en el producto distribuido llama a ese endpoint, por lo que el cierre por defecto no rompe ningún flujo propio; establezca el secreto si construye sobre él su propia difusión entre pods.
- **Cifre las copias de seguridad.** Con el proveedor de secretos de texto plano, las copias de seguridad contienen secretos. La tabla `SigningKeys` se excluye de las copias de seguridad por defecto; si la incluye mediante `Backup:IncludeSigningKeys`, el destino de la copia de seguridad debe estar cifrado en reposo. Consulte [Copia de seguridad y restauración](backup-restore).

## Herramienta de migración {#migration-tool}

Para migrar desde Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migración](migration) para más detalles.
