---
layout: default
title: Copia de seguridad y restauración
locale: es
---

# Copia de seguridad y restauración

Authagonal ofrece dos herramientas CLI para hacer copias de seguridad de los datos de Azure Table Storage y restaurarlos. Ambas son aplicaciones de consola .NET del directorio `tools/`, y ambas son envoltorios ligeros sobre el paquete NuGet `Authagonal.Backup`. Los hosts que necesiten copias de seguridad programadas, multiinquilino o fuera del sistema de archivos pueden usar la biblioteca directamente (consulte [Uso de la biblioteca](#using-the-library)).

## Copia de seguridad {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Opciones {#options}

| Opción | Descripción |
|---|---|
| `--connection-string <conn>` | Cadena de conexión de Azure Table Storage (o establezca la variable de entorno `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Directorio de salida (predeterminado: `./backups`) |
| `--incremental` | Solo copia las entidades modificadas desde la última copia de seguridad |
| `--tables <t1,t2,...>` | Lista de tablas separadas por comas (predeterminado: todas las tablas de Authagonal) |
| `--prefix <prefix>` | Prefijo de los nombres de tabla (para almacenamiento multiinquilino) |
| `--gzip` | Comprime los archivos de copia de seguridad con gzip (`.jsonl.gz`) |
| `--encryption-key <base64>` | Clave de cifrado de claves AES-256 de 32 bytes. Cifra todos los archivos de datos. Guárdela **fuera** del destino de la copia de seguridad. También lee `BACKUP_ENCRYPTION_KEY` (preferible; consulte más abajo). |
| `--manifest-key <base64>` | Clave HMAC de ≥32 bytes. Firma el manifiesto para que la restauración pueda demostrar que los hashes registrados no se reescribieron junto con los archivos. Guárdela **fuera** del destino de la copia de seguridad. También lee `BACKUP_MANIFEST_KEY` (preferible; consulte más abajo). |
| `--dry-run` | Muestra qué se copiaría sin escribir nada |

### Formato de salida {#output-format}

Cada copia de seguridad crea un directorio con marca de tiempo:

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

Con `--prefix`, las copias de seguridad se anidan un nivel más abajo, bajo el prefijo: `backups/acmecorp/20260329-120000/`.
Esto es lo que impide que colisionen las copias completas de dos inquilinos que van a parar al mismo directorio `--output` en el mismo
segundo. El id de la copia de seguridad sigue siendo una simple marca de tiempo `yyyyMMdd-HHmmss[-incr]` con
resolución de un segundo y sin prefijo, por lo que, sin el anidamiento, dos prefijos copiados en el
mismo segundo obtendrían el mismo id y, por tanto, el mismo directorio. Apunte `--input` al
directorio anidado para restaurar desde él (`--input backups/acmecorp/20260329-120000`); las ejecuciones sin prefijo
no se ven afectadas y mantienen la estructura plana mostrada arriba.

Cada archivo `.jsonl` contiene un objeto JSON por línea (uno por entidad de la tabla). Con `--gzip`, los archivos se comprimen como `.jsonl.gz`. El `_manifest.json` registra el id de la copia de seguridad, la marca de tiempo, el modo (`full` o `incremental`), la compresión, la marca de agua incremental, el número de entidades por tabla, el número de tombstones, qué tablas (si las hay) se leyeron mediante el registro de cambios (`ChangeLogTables`; null significa cobertura de escaneo completo) y los hashes SHA-256 de los archivos para la verificación de integridad.

Las copias de seguridad incrementales también escriben un archivo `_tombstones.jsonl(.gz)` que registra las eliminaciones desde la marca de agua: una línea por fila eliminada con `Table`, `PartitionKey`, `RowKey` y `DeletedAt`. La restauración los reproduce para que las filas eliminadas no reaparezcan (consulte [Reproducción de tombstones](#tombstone-replay)).

Los valores de las entidades se conservan exactamente en el viaje de ida y vuelta: cada fila copiada lleva un marcador de formato `"@v"` y una anotación explícita `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) para cada columna que JSON no puede representar sin ambigüedad, de modo que la restauración vuelve a escribir los tipos originales en lugar de valores convertidos a cadena o deducidos de nuevo.

### Verificación de integridad {#integrity-verification}

Cada manifiesto de copia de seguridad incluye un diccionario `FileHashes` que asigna a cada nombre de archivo su hash SHA-256. Durante la restauración, cada archivo se verifica con su hash registrado (a partir de la misma lectura de la que se aplican las entidades, de modo que los bytes comprobados son los bytes que se escriben) antes de que cualquiera de sus datos llegue a una tabla. Un archivo que no supera la comprobación, un archivo de datos ausente del manifiesto o un archivo listado en el manifiesto que falta en el almacén abortan la restauración. Las copias de seguridad escritas antes de que existieran los hashes de integridad (sin `FileHashes`) no se pueden verificar y se rechazan salvo con `--allow-unverified`. La verificación se puede desactivar mediante código con `RestoreOptions.VerifyIntegrity` (predeterminado: `true`).

### Pase las claves por variable de entorno, no en la línea de comandos {#pass-the-keys-by-environment-variable-not-on-the-command-line}

Ambas herramientas leen `BACKUP_ENCRYPTION_KEY` y `BACKUP_MANIFEST_KEY`, y una copia de seguridad programada debe usarlas.

Una opción pasa a formar parte de la línea de comandos del proceso. En Kubernetes eso significa que la especificación del CronJob contiene literalmente la
KEK en base64 y la clave HMAC, de modo que cualquiera con `get`/`list` sobre los cronjobs o pods de ese espacio de nombres puede leer ambas
con `kubectl get cronjob -o yaml`, un conjunto de entidades de seguridad mucho más amplio que quienes tienen acceso al Secret, y uno que
se concede habitualmente a paneles de solo lectura y a cuentas de servicio de CI. Los mismos valores son visibles en
`/proc/<pid>/cmdline` para cualquier proceso del nodo, y en el historial de shell o el registro de CI que haya compuesto el
comando. `--connection-string` tiene una vía por variable de entorno precisamente por este motivo; las dos claves que protegen
la copia de seguridad no la tenían.

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

Si se establecen ambas, la opción sigue prevaleciendo, por lo que una restauración interactiva puntual no necesita ningún cambio.

Los hashes establecen que la copia de seguridad coincide con el manifiesto, no que ninguno de los dos sea auténtico: el manifiesto está en el mismo destino que los datos, así que quien pueda reescribir `Clients.jsonl.gz` puede reescribir la línea que registra su hash. `--manifest-key` cierra esa vía: la copia de seguridad calcula el HMAC del manifiesto, la restauración lo verifica y la clave está en un lugar al que no puede llegar quien escribe la copia de seguridad. **La restauración falla en modo cerrado**: sin `--manifest-key` la rechaza en lugar de advertir, y `--allow-unauthenticated-manifest` es la exclusión explícita para las copias de seguridad escritas antes de que existiera la firma del manifiesto.

### Copias de seguridad incrementales {#incremental-backups}

Pase `--incremental` para copiar solo las entidades modificadas desde la última copia de seguridad correcta. La herramienta usa la propiedad integrada `Timestamp` de Azure Table Storage para filtrar y registra la marca de agua máxima en un archivo `.lastbackup` del directorio de salida.

Si no existe ningún archivo `.lastbackup`, la primera ejecución incremental realiza una copia completa.

Cada filtro incremental por `Timestamp` resta un pequeño margen de seguridad (`BackupDefaults.WatermarkSkewMargin`, 5 minutos) antes de filtrar. La marca de agua procede del reloj de quien llama, mientras que las marcas de tiempo de las filas las pone el servicio de almacenamiento, por lo que, de lo contrario, una mutación confirmada dentro del desfase de relojes se perdería en esta ejecución y en todas las posteriores. Volver a leer el margen cuesta unas pocas filas duplicadas por ejecución, que la semántica de inserción o actualización de la restauración deduplica.

### Tablas predeterminadas {#default-tables}

La herramienta de copia de seguridad incluye por defecto todas las tablas de Authagonal (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`, `UserRoles` y `UpstreamRefreshTokens` están en el conjunto deliberadamente: sin ellas, un despliegue restaurado es, sin que se note, más débil que el copiado (los clientes agente pierden su techo y sus barreras de consentimiento, los roles están definidos pero nadie los tiene, y los tokens de actualización de origen desaparecen).

Las tablas transitorias (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) se excluyen por defecto, ya que sus entradas están acotadas por la duración de los tokens; inclúyalas explícitamente con `--tables` si es necesario. El motor de copia de seguridad trata por separado la tabla de registro de cambios `Tombstones`, que no debe incluirse en la lista.

### Las claves de firma se excluyen por defecto {#signing-keys-are-excluded-by-default}

La tabla `SigningKeys` está en la lista de tablas predeterminada, pero **se filtra de las copias de seguridad por defecto** (`BackupOptions.IncludeSigningKeys`, predeterminado `false`; la CLI nunca lo activa). En los hosts que usan el origen de claves local (almacenado en tabla), esta tabla contiene la **clave privada** de firma de JWT, y escribirla en un archivo de copia de seguridad en texto plano permitiría falsificar tokens a cualquiera que lea la copia. Esto se aplica a **todos** los hosts: la firma de JWT no se delega en Vault Transit, por lo que no existe ninguna configuración en la que la tabla `SigningKeys` no contenga una clave privada.

> ⚠️ Inclúyala mediante `BackupOptions.IncludeSigningKeys` solo cuando el propio destino de la copia de seguridad esté cifrado en reposo y tenga control de acceso. Lo mismo se aplica al resto de la copia de seguridad: con el proveedor de secretos **de texto plano** predeterminado, las copias de seguridad también contienen sin cifrar los secretos de cliente de OIDC de origen y las semillas de TOTP / MFA. Consulte [Configuración → Proveedor de secretos](configuration#secret-provider).

### `--tables` nombra tablas del conjunto de copia de seguridad {#--tables-names-tables-from-the-backup-set}

Solo se pueden nombrar tablas del conjunto de tablas declarado (`BackupDefaults.Tables`, o `KnownTables` más abajo). Una tabla fuera de él se rechaza de entrada en lugar de
producir una copia de seguridad que la restauración rechazaría. La lista de permitidos de la restauración es ese mismo conjunto, por lo que una copia de seguridad que nombrara
cualquier otra tabla podría escribirse, convertirse en hash y firmarse, y después no restaurarse nunca. Las tablas transitorias (entradas de tokens
revocados, contadores de limitación de velocidad) se excluyen deliberadamente: caducan por sí solas, y restaurar filas obsoletas
no aporta nada.

## Restauración {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Opciones {#options-1}

| Opción | Descripción |
|---|---|
| `--connection-string <conn>` | Cadena de conexión de Azure Table Storage (o establezca la variable de entorno `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Directorio de la copia de seguridad desde el que restaurar |
| `--mode <mode>` | Modo de restauración: `upsert` (predeterminado), `merge` o `clean` |
| `--tables <t1,t2,...>` | Lista de tablas separadas por comas que se restauran (predeterminado: todos los archivos `.jsonl`/`.jsonl.gz` de la copia de seguridad) |
| `--prefix <prefix>` | Prefijo de los nombres de tabla (para almacenamiento multiinquilino) |
| `--clean-env <env>` | Con `--mode clean`, borra solo las filas de este entorno (prefijo de PartitionKey `<env>|`) |
| `--allow-clean-from-incremental` | Permite `--mode clean` contra una copia de seguridad incremental |
| `--allow-clean-all-envs` | Permite `--mode clean` sin `--clean-env`, vaciando la tabla entera |
| `--encryption-key <base64>` | La clave de cifrado de claves de 32 bytes con la que se escribió la copia de seguridad. Obligatoria para una copia de seguridad cifrada. También lee `BACKUP_ENCRYPTION_KEY`. |
| `--manifest-key <base64>` | La clave HMAC con la que se firmó la copia de seguridad. **Obligatoria** salvo con `--allow-unauthenticated-manifest`. También lee `BACKUP_MANIFEST_KEY`. |
| `--allow-unauthenticated-manifest` | Restaura sin `--manifest-key`, aceptando hashes que detectan la corrupción pero no la manipulación |
| `--allow-unverified` | Restaura una copia de seguridad cuyo manifiesto no lleva ningún hash de archivo |
| `--dry-run` | Muestra qué se restauraría sin escribir nada |

### Modos de restauración {#restore-modes}

| Modo | Comportamiento |
|---|---|
| `upsert` | Inserta o reemplaza cada entidad. Los datos existentes se sobrescriben. |
| `merge` | Inserta o combina. Se conservan las propiedades existentes que no están en la copia de seguridad. |
| `clean` | Elimina todos los datos existentes de cada tabla antes de restaurar. |

Los archivos de copia de seguridad comprimidos con gzip (`.jsonl.gz`) se detectan y descomprimen automáticamente; no se necesitan opciones adicionales.

### Reproducción de tombstones {#tombstone-replay}

Después de los archivos de datos, la restauración aplica el archivo `_tombstones` de la copia de seguridad: cada clave registrada se elimina de las tablas restauradas (`RestoreOptions.ApplyTombstones`, predeterminado `true`). Las eliminaciones de una copia incremental forman parte de su estado tanto como sus inserciones o actualizaciones; omitirlas haría reaparecer filas eliminadas, incluidas las borradas en virtud del RGPD, al restaurar una secuencia de copia completa más incrementales. Las copias completas no llevan archivo de tombstones. Al restaurar una copia completa seguida de incrementales, aplíquelas de la más antigua a la más reciente para que una recreación posterior llegue después de una eliminación anterior. El hash del archivo de tombstones se verifica con el manifiesto, igual que los archivos de datos.

### Conservación exacta de los tipos {#exact-type-round-trip}

Las filas escritas con el marcador de formato `"@v"` llevan anotaciones de tipo EDM explícitas, de modo que la restauración reconstruye exactamente los tipos de columna originales (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); una cadena sin anotación se restaura como cadena. Los archivos de copia de seguridad heredados sin el marcador recurren a una deducción basada en la forma, que se mantiene solo para que las copias antiguas sigan siendo restaurables (la deducción puede asignar un tipo erróneo a columnas de cadena con forma de GUID o de fecha).

### Códigos de salida {#exit-codes}

| Código | Significado |
|---|---|
| `0` | Éxito |
| `1` | Error (faltan argumentos, entrada no válida) |
| `2` | Éxito parcial (algunas entidades tuvieron errores) |

### Un host con sus propias tablas: `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` y `RestoreOptions.KnownTables` (ambas `string[]?`; null significa `BackupDefaults.Tables`) declaran el conjunto de tablas que es legítimo que nombre una copia de seguridad de su despliegue. Un host que almacena sus propios datos junto a los de Authagonal y copia ambos en una sola copia de seguridad debe establecerlo; de lo contrario, toda copia de seguridad que nombre esas tablas se rechaza de entrada (`BackupService.cs:48`) y toda restauración rechaza la copia de seguridad (`RestoreService.cs:17,167`).

- El host declara el conjunto de antemano. Nunca se deriva de la copia de seguridad, y ahí está la clave: una copia de seguridad no puede elegir qué tablas escribe una restauración.
- Pase el **mismo** conjunto a ambas opciones. Una copia de seguridad hecha con un conjunto más amplio solo se restaura mediante una restauración que declare ese mismo conjunto.

## Uso de la biblioteca {#using-the-library}

El paquete NuGet `Authagonal.Backup` expone las mismas operaciones mediante código, para servicios en segundo plano u orquestación personalizada:

| Tipo | Propósito |
|---|---|
| `BackupService` | Ejecuta una copia completa o incremental contra un `TableServiceClient` y escribe en un `IBackupTarget` |
| `RestoreService` | Verifica los hashes y vuelve a escribir una copia de seguridad en Table Storage |
| `MergeService` | Transmite una copia completa más las incrementales (y sus tombstones) en una única vista del estado actual |
| `RollupService` | Integra las incrementales en una copia completa nueva y, opcionalmente, elimina las de entrada |
| `BackupOptions` / `RestoreOptions` | Configuración por ejecución |
| `BackupDefaults` | Lista de tablas predeterminada y ajustes preestablecidos del registro de cambios |
| `IBackupSource` / `IBackupTarget` | Abstracciones de almacenamiento; `FileSystemBackupSource` / `FileSystemBackupTarget` son las implementaciones integradas. Implemente `IBackupTarget` para escribir en Blob Storage o en otro lugar. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incrementales basadas en el registro de cambios {#change-log-driven-incrementals}

Azure Table Storage solo indexa `PartitionKey` y `RowKey`, por lo que una copia incremental filtrada por `Timestamp` sigue siendo un escaneo completo de cada tabla. Para evitarlo, los almacenes de Authagonal registran cada mutación en un registro de cambios mediante el punto de extensión `IChangeWriter` (`Authagonal.Core`), implementado para Azure por `TableChangeWriter` (`Authagonal.AzureProvider`). Es una única tabla física, que se sigue llamando `Tombstones`: PK = el nombre lógico de la tabla, RK = `"{pk}|{rk}"`, una columna `Op` con `"U"` (inserción o actualización) o `"D"` (eliminación), y las columnas de referencia `OrigPK`/`OrigRK` (un `|` dentro de la PartitionKey original hace ambigua la división de la RowKey compuesta, por lo que el lector de la copia de seguridad confía en las columnas y solo recurre a la división en las filas heredadas). Cada clave tiene una sola fila (inserción con reemplazo), así que prevalece la última operación de una ventana de copia de seguridad.

Con la vía del registro de cambios habilitada, una copia incremental enumera las entradas `Op = "U"` del registro de cambios de una tabla desde la marca de agua y lee de forma puntual cada fila viva en lugar de escanear la tabla. La función es **opcional y está desactivada por defecto**: `BackupOptions.ChangeLoggedTables` null o vacío significa que todas las tablas siguen por la vía del escaneo, de modo que el mecanismo se distribuye inactivo hasta que se active deliberadamente (un despliegue no puede perder sin aviso filas modificadas por código anterior a la captura). Dos ajustes preestablecidos:

| Ajuste preestablecido | Contenido |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Las tablas cuyas escrituras captura por completo el registro de cambios: `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | El mismo conjunto más `Users`. Las escrituras del estado de inicio de sesión de Users no se capturan deliberadamente (ruta crítica, poco valor), por lo que este ajuste **solo es seguro si también ejecuta el escaneo completo de red de seguridad descrito abajo** |

La propiedad `ChangeLogTables` del manifiesto indica qué tablas leyó una ejecución mediante el registro de cambios; null o vacío significa que la ejecución tuvo cobertura de escaneo completo (una copia completa, una incremental por escaneo simple o un escaneo de red de seguridad).

### Escaneo completo como red de seguridad {#full-scan-backstop}

Como la captura del registro de cambios puede perder escrituras (campos de estado de inicio de sesión, escritores que no pasan por el almacén, pods que ejecutan código anterior a la captura durante un despliegue), combine las incrementales basadas en el registro de cambios con un nuevo escaneo completo periódico. Establezca `BackupOptions.WatermarkOverride` en la marca de tiempo del último escaneo con cobertura completa y deje `ChangeLoggedTables` sin establecer en esa ejecución: la incremental filtra entonces por `Timestamp` en toda la ventana desde ese escaneo y recoge todo lo que el registro de cambios nunca capturó. Un escaneo de red de seguridad diario junto a incrementales horarias basadas en el registro de cambios es una cadencia razonable. Las eliminaciones son la única clase de mutación sin autorreparación (un escaneo de filas vivas no puede ver una fila que ya no está), y por eso los almacenes escriben el tombstone de eliminación **antes** de eliminar la fila de datos.

Todos los filtros incrementales, incluido el de red de seguridad, restan `BackupDefaults.WatermarkSkewMargin` (5 minutos) de la marca de agua; quienes purguen el registro de cambios después de una copia de seguridad deben acotar la purga con el mismo margen o eliminarán filas que la siguiente ejecución todavía necesita.

### Consolidaciones {#rollups}

`RollupService.RollupAsync` combina una copia completa y sus incrementales en una nueva copia completa; `RollupAndCleanAsync` además elimina después las de entrada. El parámetro opcional `newBackupId` da nombre al resultado (null deriva un id de marca de tiempo); una instantánea que se conserva de forma especial (por ejemplo, una consolidación semanal) debe pasar aquí su id, ya que la retención basada en id lista los ids físicos de las copias de seguridad, no los manifiestos.

Durante una combinación, los tombstones se aplican según el orden temporal: una eliminación quita una fila capturada solo cuando el `Timestamp` de la fila no es posterior al `DeletedAt` del tombstone. Una clave eliminada al principio de la ventana y recreada después tiene tanto un tombstone como una captura viva, y la fila recreada sobrevive a la consolidación. Los tombstones heredados sin `DeletedAt` eliminan incondicionalmente.

## Docker {#docker}

La herramienta de copia de seguridad incluye un Dockerfile (`tools/Authagonal.Backup/Dockerfile`) para ejecutarla en CI o sin instalar el SDK de .NET:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

La herramienta de restauración no tiene imagen; ejecútela con el SDK de .NET (`dotnet run --project tools/Authagonal.Restore`).

## Programar copias de seguridad {#scheduling-backups}

Para el uso en producción, ejecute la herramienta de copia de seguridad de forma programada (por ejemplo, una copia completa diaria + incrementales cada hora):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Los hosts que integran la biblioteca suelen ejecutar incrementales cada hora con la vía del registro de cambios activada, un escaneo completo diario de red de seguridad y consolidaciones periódicas para acotar la cadena de incrementales.
