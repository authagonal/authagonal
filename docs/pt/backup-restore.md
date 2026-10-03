---
layout: default
title: Cópia de segurança e restauro
locale: pt
---

# Cópia de segurança e restauro

A Authagonal disponibiliza duas ferramentas CLI para fazer cópias de segurança e restaurar dados do Azure Table Storage. Ambas são aplicações de consola .NET no diretório `tools/`, e ambas são invólucros finos sobre o pacote NuGet `Authagonal.Backup`. Os anfitriões que precisem de cópias de segurança agendadas, multi-inquilino ou fora do sistema de ficheiros podem utilizar a biblioteca diretamente (consulte [Utilizar a biblioteca](#using-the-library)).

## Cópia de segurança {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Opções {#options}

| Opção | Descrição |
|---|---|
| `--connection-string <conn>` | Cadeia de ligação do Azure Table Storage (ou defina a variável de ambiente `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Diretório de saída (predefinição: `./backups`) |
| `--incremental` | Copiar apenas as entidades alteradas desde a última cópia de segurança |
| `--tables <t1,t2,...>` | Lista de tabelas separadas por vírgulas (predefinição: todas as tabelas da Authagonal) |
| `--prefix <prefix>` | Prefixo dos nomes das tabelas (para armazenamento multi-inquilino) |
| `--gzip` | Comprimir os ficheiros da cópia de segurança com gzip (`.jsonl.gz`) |
| `--encryption-key <base64>` | Chave de encriptação de chaves AES-256 de 32 bytes. Encripta todos os ficheiros de dados. Mantenha-a **fora** do destino da cópia de segurança. Também lê `BACKUP_ENCRYPTION_KEY` (prefira esta opção; ver abaixo). |
| `--manifest-key <base64>` | Chave HMAC de ≥32 bytes. Assina o manifesto para que o restauro possa provar que os hashes registados não foram reescritos juntamente com os ficheiros. Mantenha-a **fora** do destino da cópia de segurança. Também lê `BACKUP_MANIFEST_KEY` (prefira esta opção; ver abaixo). |
| `--dry-run` | Mostrar o que seria copiado sem escrever nada |

### Formato de saída {#output-format}

Cada cópia de segurança cria um diretório com carimbo temporal:

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

Com `--prefix`, as cópias de segurança ficam aninhadas um nível mais abaixo, sob o prefixo: `backups/acmecorp/20260329-120000/`.
É isto que impede que as cópias de segurança completas de dois inquilinos que caiam no mesmo diretório `--output` no mesmo
segundo colidam. O id da cópia de segurança continua a ser apenas um carimbo temporal `yyyyMMdd-HHmmss[-incr]` com
resolução de um segundo e sem prefixo, pelo que, sem o aninhamento, dois prefixos copiados no mesmo
segundo receberiam o mesmo id e, portanto, o mesmo diretório. Aponte `--input` para o
diretório aninhado para restaurar a partir dele (`--input backups/acmecorp/20260329-120000`); as execuções sem prefixo
não são afetadas e mantêm a estrutura plana mostrada acima.

Cada ficheiro `.jsonl` contém um objeto JSON por linha (um por entidade da tabela). Com `--gzip`, os ficheiros são comprimidos como `.jsonl.gz`. O `_manifest.json` regista o id da cópia de segurança, o carimbo temporal, o modo (`full` ou `incremental`), a compressão, a marca de água incremental, as contagens de entidades por tabela, a contagem de tombstones, quais as tabelas (se as houver) lidas através do registo de alterações (`ChangeLogTables`; null significa cobertura por varredura completa) e os hashes SHA-256 dos ficheiros para verificação de integridade.

As cópias de segurança incrementais também escrevem um ficheiro `_tombstones.jsonl(.gz)` que regista as eliminações desde a marca de água: uma linha por linha eliminada, com `Table`, `PartitionKey`, `RowKey` e `DeletedAt`. O restauro reproduz-nas para que as linhas eliminadas não ressuscitem (consulte [Reprodução de tombstones](#tombstone-replay)).

Os valores das entidades fazem o percurso de ida e volta com exatidão: cada linha copiada transporta um marcador de formato `"@v"` e uma anotação explícita `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) para cada coluna que o JSON não consiga representar sem ambiguidade, pelo que o restauro grava os tipos originais em vez de valores convertidos em cadeia ou novamente inferidos.

### Verificação de integridade {#integrity-verification}

Cada manifesto de cópia de segurança inclui um dicionário `FileHashes` que associa os nomes dos ficheiros aos respetivos hashes SHA-256. Durante o restauro, cada ficheiro é verificado contra o hash registado (a partir da mesma leitura de onde as entidades são aplicadas, pelo que os bytes verificados são os bytes que são gravados) antes de qualquer dos seus dados chegar a uma tabela. Um ficheiro que falhe a verificação, um ficheiro de dados ausente do manifesto ou um ficheiro listado no manifesto que falte no armazenamento interrompem, qualquer um deles, o restauro. As cópias de segurança escritas antes de existir o hashing de integridade (sem `FileHashes`) não podem ser verificadas e são recusadas, a menos que se utilize `--allow-unverified`. A verificação pode ser desativada programaticamente através de `RestoreOptions.VerifyIntegrity` (predefinição `true`).

### Passe as chaves por variável de ambiente, não na linha de comandos {#pass-the-keys-by-environment-variable-not-on-the-command-line}

Ambas as ferramentas leem `BACKUP_ENCRYPTION_KEY` e `BACKUP_MANIFEST_KEY`, e uma cópia de segurança agendada deve utilizá-las.

Uma flag passa a fazer parte da linha de comandos do processo. No Kubernetes, isso significa que a especificação do CronJob contém literalmente a
KEK em base64 e a chave HMAC, pelo que qualquer pessoa com `get`/`list` sobre cronjobs ou pods nesse namespace consegue ler ambas
com `kubectl get cronjob -o yaml`, um conjunto de principais muito mais amplo do que os detentores do Secret, e um conjunto
habitualmente concedido a dashboards só de leitura e a contas de serviço de CI. Os mesmos valores são visíveis em
`/proc/<pid>/cmdline` para qualquer processo no nó, e em qualquer histórico de shell ou registo de CI que tenha montado o
comando. `--connection-string` tem um caminho por variável de ambiente exatamente por esta razão; as duas chaves que protegem
o arquivo não tinham.

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

Uma flag continua a prevalecer se ambas estiverem definidas, pelo que um restauro interativo pontual não precisa de nenhuma alteração.

Os hashes estabelecem que o arquivo corresponde ao manifesto, não que algum dos dois seja autêntico: o manifesto está no mesmo destino que os dados, pelo que quem conseguir reescrever `Clients.jsonl.gz` consegue reescrever a linha que regista o seu hash. `--manifest-key` resolve isso: a cópia de segurança calcula o HMAC do manifesto, o restauro verifica-o, e a chave reside num local a que quem escreve a cópia de segurança não consegue chegar. **O restauro falha de forma fechada**: sem `--manifest-key`, recusa em vez de emitir um aviso, e `--allow-unauthenticated-manifest` é a exclusão explícita para arquivos escritos antes da assinatura de manifestos.

### Cópias de segurança incrementais {#incremental-backups}

Passe `--incremental` para copiar apenas as entidades modificadas desde a última cópia de segurança bem-sucedida. A ferramenta utiliza a propriedade `Timestamp` integrada do Azure Table Storage para filtrar e acompanha a marca de água máxima num ficheiro `.lastbackup` no diretório de saída.

Se não existir nenhum ficheiro `.lastbackup`, a primeira execução incremental faz uma cópia de segurança completa.

Cada filtro incremental por `Timestamp` subtrai uma pequena margem de segurança (`BackupDefaults.WatermarkSkewMargin`, 5 minutos) antes de filtrar. A marca de água vem do relógio de quem chama, enquanto os carimbos temporais das linhas são atribuídos pelo serviço de armazenamento, pelo que uma mutação confirmada dentro do desvio de relógio seria, de outro modo, perdida por esta execução e por todas as seguintes. Reler a margem custa algumas linhas duplicadas por execução, que a semântica de upsert do restauro elimina.

### Tabelas predefinidas {#default-tables}

A ferramenta de cópia de segurança inclui por predefinição todas as tabelas da Authagonal (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`, `UserRoles` e `UpstreamRefreshTokens` estão no conjunto deliberadamente: sem elas, uma implementação restaurada fica, sem que se note, mais fraca do que a que foi copiada (os clientes de agente perdem o seu limite máximo e as verificações de consentimento, as funções estão definidas mas ninguém as detém, os tokens de atualização a montante desaparecem).

As tabelas transitórias (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) são excluídas por predefinição, uma vez que as suas entradas estão limitadas pelos tempos de vida dos tokens; inclua-as explicitamente com `--tables`, se necessário. A tabela de registo de alterações `Tombstones` é tratada separadamente pelo motor de cópia de segurança e não deve ser listada.

### As chaves de assinatura são excluídas por predefinição {#signing-keys-are-excluded-by-default}

A tabela `SigningKeys` está na lista de tabelas predefinida, mas é **filtrada das cópias de segurança por predefinição** (`BackupOptions.IncludeSigningKeys`, predefinição `false`; a CLI nunca a ativa). Nos anfitriões que utilizam a origem de chaves local (guardada em tabela), esta tabela contém a **chave privada** de assinatura de JWT, e gravá-la num ficheiro de cópia de segurança em texto simples permitiria a qualquer pessoa que leia a cópia de segurança forjar tokens. Isto aplica-se a **todos** os anfitriões: a assinatura de JWT não é delegada no Vault Transit, pelo que não existe nenhuma configuração em que a tabela `SigningKeys` não contenha uma chave privada.

> ⚠️ Só opte pela inclusão através de `BackupOptions.IncludeSigningKeys` quando o próprio destino da cópia de segurança estiver encriptado em repouso e com acesso controlado. O mesmo se aplica ao resto da cópia de segurança: com o fornecedor de segredos predefinido de **texto simples**, as cópias de segurança também contêm em claro os segredos de cliente OIDC a montante e as sementes TOTP / MFA. Consulte [Configuração → Fornecedor de segredos](configuration#secret-provider).

### `--tables` indica tabelas do conjunto de cópia de segurança {#--tables-names-tables-from-the-backup-set}

Só podem ser indicadas tabelas do conjunto de tabelas declarado (`BackupDefaults.Tables`, ou `KnownTables` abaixo). Uma tabela fora dele é recusada logo à partida, em vez de
produzir um arquivo que o restauro rejeitaria. A lista de permissões do restauro é esse mesmo conjunto, pelo que um arquivo que indicasse
qualquer outra tabela poderia ser escrito, ter o hash calculado e ser assinado, e depois nunca ser restaurado. As tabelas transitórias (entradas
de tokens revogados, contadores de limitação de taxa) são excluídas deliberadamente: expiram por si mesmas, e restaurar linhas desatualizadas
não serve para nada.

## Restauro {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Opções {#options-1}

| Opção | Descrição |
|---|---|
| `--connection-string <conn>` | Cadeia de ligação do Azure Table Storage (ou defina a variável de ambiente `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Diretório da cópia de segurança a partir do qual restaurar |
| `--mode <mode>` | Modo de restauro: `upsert` (predefinição), `merge` ou `clean` |
| `--tables <t1,t2,...>` | Lista de tabelas a restaurar, separadas por vírgulas (predefinição: todos os ficheiros `.jsonl`/`.jsonl.gz` da cópia de segurança) |
| `--prefix <prefix>` | Prefixo dos nomes das tabelas (para armazenamento multi-inquilino) |
| `--clean-env <env>` | Com `--mode clean`, apagar apenas as linhas deste ambiente (prefixo de PartitionKey `<env>|`) |
| `--allow-clean-from-incremental` | Permitir `--mode clean` a partir de uma cópia de segurança incremental |
| `--allow-clean-all-envs` | Permitir `--mode clean` sem `--clean-env`, esvaziando a tabela inteira |
| `--encryption-key <base64>` | A chave de encriptação de chaves de 32 bytes com que a cópia de segurança foi escrita. Obrigatória para um arquivo encriptado. Também lê `BACKUP_ENCRYPTION_KEY`. |
| `--manifest-key <base64>` | A chave HMAC com que a cópia de segurança foi assinada. **Obrigatória**, a menos que se utilize `--allow-unauthenticated-manifest`. Também lê `BACKUP_MANIFEST_KEY`. |
| `--allow-unauthenticated-manifest` | Restaurar sem `--manifest-key`, aceitando hashes que detetam corrupção mas não adulteração |
| `--allow-unverified` | Restaurar uma cópia de segurança cujo manifesto não contenha nenhum hash de ficheiro |
| `--dry-run` | Mostrar o que seria restaurado sem escrever nada |

### Modos de restauro {#restore-modes}

| Modo | Comportamento |
|---|---|
| `upsert` | Inserir ou substituir cada entidade. Os dados existentes são substituídos. |
| `merge` | Inserir ou fundir. As propriedades existentes que não estejam na cópia de segurança são preservadas. |
| `clean` | Eliminar todos os dados existentes em cada tabela antes de restaurar. |

Os ficheiros de cópia de segurança comprimidos com gzip (`.jsonl.gz`) são detetados e descomprimidos automaticamente; não são necessárias flags adicionais.

### Reprodução de tombstones {#tombstone-replay}

Depois dos ficheiros de dados, o restauro aplica o ficheiro `_tombstones` da cópia de segurança: cada chave registada é eliminada das tabelas restauradas (`RestoreOptions.ApplyTombstones`, predefinição `true`). As eliminações de uma incremental fazem tanto parte do seu estado como os seus upserts; ignorá-las ressuscitaria linhas eliminadas, incluindo as apagadas ao abrigo do RGPD, ao restaurar uma sequência de completa mais incrementais. As cópias de segurança completas não têm ficheiro de tombstones. Ao restaurar uma cópia de segurança completa seguida de incrementais, aplique-as da mais antiga para a mais recente, para que uma recriação posterior fique depois de uma eliminação anterior. O hash do ficheiro de tombstones é verificado contra o manifesto, tal como os ficheiros de dados.

### Ida e volta exata dos tipos {#exact-type-round-trip}

As linhas escritas com o marcador de formato `"@v"` transportam anotações de tipo EDM explícitas, pelo que o restauro reconstrói exatamente os tipos de coluna originais (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); uma cadeia sem anotação é restaurada como cadeia. Os ficheiros de cópia de segurança antigos sem o marcador recorrem à inferência baseada na forma, mantida apenas para que as cópias de segurança antigas continuem restauráveis (a inferência pode atribuir o tipo errado a colunas de cadeia com forma de GUID ou de data).

### Códigos de saída {#exit-codes}

| Código | Significado |
|---|---|
| `0` | Sucesso |
| `1` | Erro (argumentos em falta, entrada inválida) |
| `2` | Sucesso parcial (algumas entidades tiveram erros) |

### Um anfitrião com as suas próprias tabelas: `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` e `RestoreOptions.KnownTables` (ambos `string[]?`; null significa `BackupDefaults.Tables`) declaram o conjunto de tabelas que um arquivo da sua implementação pode legitimamente indicar. Um anfitrião que guarde os seus próprios dados ao lado dos da Authagonal e copie os dois como um único arquivo define-o; caso contrário, todas as cópias de segurança que indiquem essas tabelas são recusadas logo à partida (`BackupService.cs:48`) e todos os restauros recusam o arquivo (`RestoreService.cs:17,167`).

- O anfitrião declara o conjunto antecipadamente. Nunca é derivado do arquivo, e é precisamente essa a questão: não é o arquivo que escolhe as tabelas em que um restauro escreve.
- Passe o **mesmo** conjunto a ambas as opções. Uma cópia de segurança feita com um conjunto mais amplo só é restaurada por um restauro que declare esse mesmo conjunto.

## Utilizar a biblioteca {#using-the-library}

O pacote NuGet `Authagonal.Backup` expõe as mesmas operações de forma programática, para serviços em segundo plano ou orquestração personalizada:

| Tipo | Finalidade |
|---|---|
| `BackupService` | Executa uma cópia de segurança completa ou incremental sobre um `TableServiceClient`, escrevendo para um `IBackupTarget` |
| `RestoreService` | Verifica os hashes e grava uma cópia de segurança de volta no Table Storage |
| `MergeService` | Transmite uma cópia de segurança completa mais as incrementais (e os respetivos tombstones) para uma única vista do estado atual |
| `RollupService` | Consolida as incrementais numa nova cópia de segurança completa, eliminando opcionalmente as cópias de origem |
| `BackupOptions` / `RestoreOptions` | Configuração por execução |
| `BackupDefaults` | Lista de tabelas predefinida e predefinições do registo de alterações |
| `IBackupSource` / `IBackupTarget` | Abstrações de armazenamento; `FileSystemBackupSource` / `FileSystemBackupTarget` são as implementações integradas. Implemente `IBackupTarget` para escrever para armazenamento de blobs ou outro destino. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Incrementais orientadas pelo registo de alterações {#change-log-driven-incrementals}

O Azure Table Storage só indexa `PartitionKey` e `RowKey`, pelo que uma cópia de segurança incremental filtrada por `Timestamp` continua a ser uma varredura completa de cada tabela. Para o evitar, os armazenamentos da Authagonal registam cada mutação num registo de alterações através do ponto de extensão `IChangeWriter` (`Authagonal.Core`), implementado para o Azure por `TableChangeWriter` (`Authagonal.AzureProvider`). É uma única tabela física, ainda com o nome `Tombstones`: PK = o nome lógico da tabela, RK = `"{pk}|{rk}"`, uma coluna `Op` com `"U"` (upsert) ou `"D"` (eliminação), e colunas `OrigPK`/`OrigRK` autoritativas (um `|` dentro da PartitionKey original torna ambígua a divisão da RowKey composta, pelo que o leitor da cópia de segurança confia nas colunas e só recorre à divisão para linhas antigas). Cada chave tem uma única linha (upsert com substituição), pelo que a última operação numa janela de cópia de segurança prevalece.

Com o caminho do registo de alterações ativado, uma cópia de segurança incremental enumera as entradas `Op = "U"` do registo de alterações de uma tabela desde a marca de água e faz uma leitura pontual de cada linha ativa, em vez de varrer a tabela. A funcionalidade é **opcional e está desativada por predefinição**: `BackupOptions.ChangeLoggedTables` nulo ou vazio significa que todas as tabelas permanecem no caminho de varredura, pelo que o mecanismo é distribuído inerte até uma ativação deliberada (uma implementação não pode perder, sem aviso, linhas alteradas por código anterior à captura). Duas predefinições:

| Predefinição | Conteúdo |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | As tabelas cujas escritas são totalmente capturadas pelo registo de alterações: `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | O mesmo conjunto mais `Users`. As escritas de estado de início de sessão de Users não são deliberadamente capturadas (caminho crítico, pouco valor), pelo que esta predefinição **só é segura quando também executa a varredura completa de salvaguarda descrita abaixo** |

A propriedade `ChangeLogTables` do manifesto lista as tabelas que uma execução leu através do registo de alterações; nulo ou vazio significa que a execução teve cobertura por varredura completa (uma cópia de segurança completa, uma incremental simples por varredura ou uma varredura de salvaguarda).

### Varredura completa de salvaguarda {#full-scan-backstop}

Como a captura do registo de alterações pode perder escritas (campos de estado de início de sessão, escritores fora dos armazenamentos, pods a executar código anterior à captura durante uma implementação), combine as incrementais orientadas pelo registo de alterações com uma nova varredura completa periódica. Defina `BackupOptions.WatermarkOverride` com o carimbo temporal da última varredura com cobertura completa e deixe `ChangeLoggedTables` por definir nessa execução: a incremental filtra então por `Timestamp` ao longo de toda a janela desde essa varredura, apanhando tudo o que o registo de alterações nunca capturou. Uma salvaguarda diária a par de incrementais horárias pelo registo de alterações é uma cadência razoável. As eliminações são a única classe de mutação sem autorreparação (uma varredura de linhas ativas não consegue ver uma linha que já não existe), e é por isso que os armazenamentos escrevem o tombstone da eliminação **antes** de eliminarem a linha de dados.

Todos os filtros incrementais, incluindo a salvaguarda, subtraem `BackupDefaults.WatermarkSkewMargin` (5 minutos) à marca de água; quem chama e purga o registo de alterações após uma cópia de segurança tem de limitar a purga pela mesma margem, ou elimina linhas de que a execução seguinte ainda precisa.

### Consolidações (rollups) {#rollups}

`RollupService.RollupAsync` funde uma cópia de segurança completa e as suas incrementais numa nova cópia de segurança completa; `RollupAndCleanAsync` elimina ainda as cópias de origem no final. O parâmetro opcional `newBackupId` dá nome ao resultado (null deriva um id a partir do carimbo temporal); um instantâneo retido de forma especial (por exemplo, uma consolidação semanal) tem de passar aqui o seu id, uma vez que a retenção baseada em ids lista ids físicos de cópias de segurança, e não manifestos.

Durante uma fusão, os tombstones aplicam-se por ordem temporal: uma eliminação só remove uma linha capturada quando o `Timestamp` da linha não for posterior ao `DeletedAt` do tombstone. Uma chave eliminada no início da janela e recriada mais tarde tem um tombstone e uma captura ativa, e a linha recriada sobrevive à consolidação. Os tombstones antigos sem `DeletedAt` removem incondicionalmente.

## Docker {#docker}

A ferramenta de cópia de segurança inclui um Dockerfile (`tools/Authagonal.Backup/Dockerfile`) para ser executada em CI ou sem instalar o SDK do .NET:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

A ferramenta de restauro não tem imagem; execute-a com o SDK do .NET (`dotnet run --project tools/Authagonal.Restore`).

## Agendar cópias de segurança {#scheduling-backups}

Em produção, execute a ferramenta de cópia de segurança de forma agendada (por exemplo, completa diária + incremental horária):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Os anfitriões que incorporam a biblioteca executam normalmente incrementais horárias com o caminho do registo de alterações ativado, uma varredura completa de salvaguarda diária e consolidações periódicas para limitar a cadeia de incrementais.
