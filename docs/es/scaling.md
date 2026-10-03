---
layout: default
title: Escalado
locale: es
---

# Escalado

Authagonal está diseñado para escalar tanto vertical como horizontalmente sin ninguna configuración especial.

## Sin estado por diseño {#stateless-by-design}

Todo el estado persistente se guarda en el almacén subyacente (Azure Table Storage, DynamoDB en el backend de AWS o PostgreSQL en el backend SQL autoalojado). No hay estado en proceso que requiera sesiones persistentes (sticky sessions) ni coordinación entre instancias:

- **Claves de firma**: se cargan desde Table Storage y se renuevan cada hora
- **Códigos de autorización y tokens de actualización**: se guardan en Table Storage con uso único garantizado
- **Prevención de reproducción en SAML**: los ID de solicitud se registran en Table Storage con eliminación atómica
- **State de OIDC y verificadores PKCE**: se guardan en Table Storage
- **Configuración de clientes y proveedores**: se obtiene de Table Storage en cada solicitud

## Cifrado de cookies (protección de datos) {#cookie-encryption-data-protection}

El anillo de claves de Data Protection de ASP.NET Core protege la cookie de autenticación, así que todas las instancias deben compartir uno. Se persiste automáticamente, en este orden:

1. `DataProtection:BlobUri`, si está establecido (un blob explícito, autenticado con `DefaultAzureCredential`).
2. Un contenedor `dataprotection` en la cuenta indicada por `Storage:ConnectionString`, salvo que sea Azurite.
3. En la vía de identidad administrada (`Storage:TableServiceUri`), el endpoint de blobs hermano de la misma cuenta, `https://{account}.blob.…/dataprotection/keys.xml`. La identidad necesita el rol Storage Blob Data Contributor en la cuenta.

Solo un endpoint de tablas no reconocido (Azurite, emuladores con direcciones de tipo ruta) recurre al almacén de archivos por máquina, que es efímero y propio de cada pod: los reinicios cierran la sesión de todos y las réplicas no pueden leer las cookies de las demás. La comprobación de arranque registra un log `Critical` cuando eso ocurre.

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

En el backend de AWS, pase un cliente de S3 y un bucket a `AddAuthagonalAwsStorage` para persistir el anillo de claves en S3; sin ellos, el anillo de claves está en memoria y las cookies dejan de funcionar al reiniciar y entre nodos. Consulte [Instalación → Backend de AWS](installation#aws-backend). En el backend SQL, el anillo lo persisten `AddAuthagonalPostgres` / `AddAuthagonalSqlite`.

Persistir no es cifrar: el anillo es XML en texto plano salvo que esté establecido `DataProtection:KeyVaultKeyId` o `DataProtection:CertificateThumbprint`. Al arrancar, se rechaza un anillo sin cifrado y todavía sin claves, y uno que ya tiene claves arranca con un log `Critical` (`DataProtection:AllowUnencryptedKeyRing=true` lo acepta de forma deliberada). Consulte [Configuración](configuration) para ver la tabla completa de `DataProtection:*`.

## Cachés por instancia {#per-instance-caches}

Un pequeño número de valores de lectura frecuente y cambio lento se almacena en memoria en cada instancia para reducir los accesos a Table Storage:

| Dato | Duración de la caché | Efecto de un valor desactualizado |
|---|---|---|
| Documentos de descubrimiento de OIDC | 60 minutos (configurable) | Se tarda en detectar la rotación de claves del IdP |
| Metadatos del IdP SAML | 60 minutos (configurable) | Igual |
| Orígenes permitidos por CORS | 60 minutos (configurable) | Los orígenes nuevos tardan hasta una hora en propagarse |

Estas cachés son aceptables para uso en producción. Todas las duraciones se pueden configurar mediante la sección de configuración `Cache`; consulte [Configuración](configuration). Si necesita una propagación inmediata, reinicie las instancias afectadas.

## Limitación de frecuencia {#rate-limiting}

Los endpoints propensos al abuso (registro por IP, restablecimiento de contraseña por correo de destino, SCIM por cliente, registro dinámico de clientes por IP; consulte [Configuración → Limitación de frecuencia](configuration#rate-limiting)) están protegidos por un limitador de frecuencia integrado.

Por defecto, los límites se aplican **en proceso, por nodo**, detrás de la abstracción `IRateLimiter`, de modo que con N instancias el techo efectivo es N veces el valor configurado. Es deliberado: el limitador es una red de seguridad frente al abuso descontrolado de un solo nodo, y el límite global de referencia corresponde al perímetro (WAF / ingress / CDN), que ve todo el tráfico antes de que se reparta entre instancias.

Esa compensación es correcta para los límites de volumen e incorrecta en un caso: un presupuesto que protege un **secreto adivinable**. El `user_code` del flujo de dispositivo es una cadena corta de un alfabeto reducido, y el límite de intentos es lo único que se interpone entre un atacante y un código que concede una sesión activa. Un techo que se multiplica por el número de réplicas no sirve ahí, y convierte el límite real en una propiedad de la configuración de su ingress en lugar del servidor.

Establezca **`Auth:DurableRateLimiting=true`** para trasladar los contadores al almacén que ya utiliza, de modo que todas las réplicas compartan un mismo presupuesto. Cuesta un acceso al almacén por cada comprobación del límite, usa ventanas fijas (un presupuesto de N permite hasta 2N alrededor del límite entre dos ventanas) y, si el almacén no está accesible, deja pasar las solicitudes, así que se suma a la regla del perímetro en lugar de sustituirla. Las filas de contadores se recogen automáticamente en los tres backends. Consulte [Configuración → Límites para todo el clúster](configuration#cluster-wide-limits-authdurableratelimiting).

## Clústeres {#clustering}

Varias instancias se coordinan mediante una **elección de líder** y un **bus de eventos entre nodos**, ambos detrás de backends intercambiables:

- **Elección de líder**: una elección basada en un arrendamiento (lease) (`Cluster:LeaseTtlSeconds`, 30 s por defecto, renovado aproximadamente a la mitad de ese intervalo). Exactamente un nodo tiene el arrendamiento; el liderazgo se transfiere automáticamente cuando el líder cae. El trabajo reservado al líder solo se ejecuta en el líder: la *desactivación* de claves de firma al caducar (cuando `Auth:KeyRotationEnabled` está activado), el barrido de reconciliación de concesiones (solo en el backend de Azure), el relleno retroactivo del cifrado en reposo (cuando `Auth:AtRestBackfillEnabled` está activado; un nodo no líder espera brevemente a obtener el liderazgo y, si no, lo omite) y el barrido de contadores de limitación de frecuencia (backend de Azure con `Auth:DurableRateLimiting`). Con `Cluster:Enabled=false`, el único nodo es líder de forma permanente, así que un despliegue independiente sigue ejecutándolos todos.
- **Bus de eventos**: notificaciones entre nodos (p. ej., la invalidación de cachés en hosts multiinquilino), consultadas cada `Cluster:PollIntervalSeconds` (3 s por defecto).

Cada instancia genera al arrancar un ID de nodo aleatorio de 12 caracteres hexadecimales para identificarse; no se persiste.

### Backends {#backends}

El **valor por defecto es en proceso**: un único nodo siempre es su propio líder y los eventos son solo locales, lo cual es correcto para una instancia sin ninguna configuración. Los despliegues con varios nodos sustituyen esto por un backend real mediante el callback `configureClustering` de `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registran solo el bus de eventos y mantienen el arrendamiento en proceso (siempre líder); úselos en nodos que deben recibir eventos del clúster pero nunca deben competir por el liderazgo.

> **Nota:** con el valor por defecto en proceso en varios nodos, *todos* los nodos creen que son el líder. Es inocuo para la mayoría de las cargas de trabajo, pero active un backend de arrendamiento real antes de activar `Auth:KeyRotationEnabled` en varias instancias.

La **generación** de claves de firma es independiente de esa desactivación reservada al líder y no depende de ella: todos los nodos llaman a `EnsureActiveKeyAsync` al arrancar y en cada renovación de `Auth:SigningKeyCacheRefreshMinutes`, de modo que con `KeyRotationEnabled` desactivado (el valor por defecto), la sustitución al caducar a los 90 días depende por completo de esa vía. La generación toma su propio arrendamiento breve del clúster, así que solo escribe un nodo siempre que haya configurado un backend de arrendamiento real. Con el valor por defecto en proceso en varios nodos no existe esa coordinación, y dos nodos que lleguen a una clave caducada en el mismo momento pueden generar una cada uno; ambas acaban en el JWKS y los tokens firmados con cualquiera de ellas se verifican, pero la clave que se indica como activa puede alternar. Es otro motivo para configurar un backend de arrendamiento real en despliegues con varios nodos.

Consulte la página de [Configuración](configuration#cluster) para ver todos los ajustes del clúster.

### Despliegues multiinquilino {#multi-tenant-deployments}

En modo multiinquilino (`AddAuthagonalCore()`), `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` y los servicios de carga desde la configuración (clientes, proveedores, ámbitos, roles) no se registran: forman parte de la composición de inquilino único `AddAuthagonal()`, y es el host quien gestiona ese trabajo para cada inquilino.

## Partición caliente del índice de nombres {#name-index-hot-partition}

La búsqueda por prefijo de nombre de la administración se apoya en las tablas de índice `UserFirstNames` / `UserLastNames`, que usan una **única partición caliente**. A gran escala, esto limita el rendimiento de escritura del índice a unas 2000 operaciones por segundo, lo que puede convertirse en un cuello de botella al crear o actualizar usuarios con mucha carga. Si no expone la búsqueda por nombre de la administración, establezca `Storage:NameIndexesEnabled = false` para omitir por completo estas escrituras. Consulte [Configuración](configuration).

## Proxy de confianza y endpoints internos {#trusted-proxy-and-internal-endpoints}

Cuando se ejecutan varias instancias detrás de un balanceador de carga:

- **Cabeceras reenviadas**: la limitación de frecuencia y el bloqueo de cuentas se basan en la IP del cliente, que se resuelve a partir de `X-Forwarded-For`. Establezca `ForwardedHeaders:KnownNetworks` en el CIDR de su ingress o de sus pods para que la IP del cliente no se pueda falsificar entre instancias. `ForwardedHeaders:ForwardLimit` vale `1` por defecto. Consulte [Configuración](configuration#forwarded-headers-trusted-proxy).
- **Endpoints internos**: `/_internal/backchannel-logout` exige `Cluster:Secret` en la cabecera `X-Cluster-Secret` (comparada en tiempo constante). Sin ella, el endpoint no autoriza a nadie y responde 404; la IP de origen no se trata como credencial, porque loopback es lo que presenta un proxy inverso en el mismo host para cada solicitud reenviada, y un rango privado es cualquier carga de trabajo vecina en una red de clúster compartida. `Cluster:AllowLoopbackWithoutSecret` es una opción solo para desarrollo que vuelve a admitir un par de loopback anterior al reenvío. El producto tal como se distribuye nunca llama a esta ruta (la difusión de cierres de sesión se hace en proceso mediante `SessionTermination`), así que solo importa para una difusión que construya usted mismo.

## Recomendaciones de escalado {#scaling-recommendations}

**Escalado vertical**: aumente la CPU y la memoria de una única instancia. Útil para atender más solicitudes simultáneas por instancia.

**Escalado horizontal**: ejecute varias instancias detrás de un balanceador de carga. No se necesitan sesiones persistentes ni cachés compartidas. Cada instancia es totalmente independiente.

**Escalado a cero**: Authagonal admite despliegues con escalado a cero (p. ej., Azure Container Apps con `minReplicas: 0`). La primera solicitud tras un periodo de inactividad tendrá un arranque en frío de unos segundos mientras se inicializa el runtime de .NET y se cargan las claves de firma desde el almacenamiento.
