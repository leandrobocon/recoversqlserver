# Plano de Recuperação — Base SQL Server Corrompida

> **Status**: PLANEJAMENTO — ainda não executar.
> **Objetivo**: reparar base(s) corrompida(s) (`.mdf` + `.ldf`) usando ferramentas **livres**.

---

## 1. Contexto e Ambiente

### Base de dados a recuperar
Os dados de cada base são informados **localmente via `.env`** (não versionado; ver `.env.example`):
- Nome da base, nomes e tamanhos de `.mdf`/`.ldf`, caminho de origem.
- Um exemplo de carregamento simples:
  ```bash
  set -a; source /var/sqlserver/.env; set +a
  ```
- O fluxo deste plano é **genérico** e vale para qualquer base/preenchimento do `.env`. Preencher por base (ex.: `DB_*` para a primeira, `DB2_*` para a segunda, etc.).

### Ambiente de execução
- SO: Linux (Ubuntu), sem SQL Server nativo instalado.
- **Docker disponível** e em funcionamento.
- Recursos: CPUs/RAM modestos; espaço em disco suficiente para uma cópia de trabalho.
- Computação via imagem oficial **Microsoft SQL Server para Linux** (gratuita/Developer).

### Decisão de arquitetura
Como não há SQL Server nativo, usaremos a **imagem oficial da Microsoft** `mcr.microsoft.com/mssql/server` em um contêiner Docker para:
1. Anexar (attach) a base.
2. Rodar `DBCC CHECKDB` (ferramenta oficial, gratuita, da própria Microsoft).
3. Aplicar reparos graduais (REPAIR_REBUILD / REPAIR_ALLOW_DATA_LOSS) — último recurso.
4. Exportar/backup do banco recuperado.

---

## 2. Etapas de Execução

### Etapa 0 — Pré-requisitos
- [ ] Garantir espaço em disco para o contêiner e para a cópia de trabalho (os `.mdf`/`.ldf` originais NUNCA devem ser alterados direto).
- [ ] **Sempre trabalhar sobre uma CÓPIA** dos arquivos originais.
- [ ] Obter imagem oficial: `docker pull mcr.microsoft.com/mssql/server:2022-latest`
  - Alternativa para menor consumo de RAM: usar edição Express (`mssql/server:2022-latest` já é suficiente p/ base de 2.7 GB, mas verificar o limite de 10 GB do Express — a base cabe).

### Etapa 1 — Criar o contêiner
> Usa variáveis do `.env` (`CONTAINER_NAME`, `MSSQL_*`, `SRC_DATA_DIR`, `WORK_DATA_DIR`, `MSSQL_PORT`).
```bash
set -a; source /var/sqlserver/.env; set +a

mkdir -p "$WORK_DATA_DIR"

docker run -d --name "$CONTAINER_NAME" \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" \
  -e "MSSQL_PID=$MSSQL_PID" \
  -p "$MSSQL_PORT:1433" \
  -v "$SRC_DATA_DIR:/mnt/data:ro" \
  -v "$WORK_DATA_DIR:/var/opt/mssql" \
  "$MSSQL_IMAGE"
```
> Os arquivos originais são montados **somente-leitura** de `$SRC_DATA_DIR` em `/mnt/data` para inspeção e cópia.

### Etapa 2 — Copiar os arquivos para dentro do ambiente de trabalho
Como o contêiner não deve editar os originais, copiar para o volume de trabalho antes de alterar:
```bash
docker exec "$CONTAINER_NAME" bash -c "cp /mnt/data/$DB_MDF /var/opt/mssql/data/ && cp /mnt/data/$DB_LDF /var/opt/mssql/data/"
```
> Isso garante traçabilidade e preserva o original intocado.

### Etapa 3 — Anexar a base (attach)
Usando `sqlcmd` dentro do contêiner:
```bash
docker exec -it "$CONTAINER_NAME" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "
CREATE DATABASE [$DB_NAME]
  ON (FILENAME = '/var/opt/mssql/data/$DB_MDF'),
     (FILENAME = '/var/opt/mssql/data/$DB_LDF')
  FOR ATTACH;"
```

### Etapa 4 — Primeira inspeção (sem alterar)
```bash
docker exec -it "$CONTAINER_NAME" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "DBCC CHECKDB ([$DB_NAME]) WITH NO_INFOMSGS, TABLERESULTS;"
```
Análise dos resultados: tipo e quantidade de erros (alocação, consistência, lógica).

### Etapa 5 — Estratégia de reparo (gradual e por prioridade)
> **Importante**: tentar SEMPRE o reparo menos destrutivo primeiro.

1. **`REPAIR_REBUILD`** — reparo rápido, via backup de log ou índice, sem perda de dados:
   ```sql
   ALTER DATABASE [<DB_NAME>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DBCC CHECKDB ([<DB_NAME>], REPAIR_REBUILD) WITH NO_INFOMSGS;
   ALTER DATABASE [<DB_NAME>] SET MULTI_USER;
   ```
2. **`REPAIR_ALLOW_DATA_LOSS`** — ÚLTIMO recurso (pode perder dados); requer backup antes:
   ```sql
   ALTER DATABASE [<DB_NAME>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DBCC CHECKDB ([<DB_NAME>], REPAIR_ALLOW_DATA_LOSS) WITH NO_INFOMSGS;
   ALTER DATABASE [<DB_NAME>] SET MULTI_USER;
   ```

### Etapa 6 — Validação pós-reparo
- Rodar `DBCC CHECKDB ([<DB_NAME>]) WITH NO_INFOMSGS;` novamente até zerar erros.
- Testar consulta de sanidade em algumas tabelas e contagem de linhas.

### Estratégia principal escolhida
1. **Tentar DBCC CHECKDB (Docker)** primeiro — ferramenta oficial, gratuita, melhor resultado para corrupção estrutural recuperável.
2. **Se o DBCC não conseguir anexar/reparar** (corrupção severa de páginas de dados/alocação), usar as ferramentas livres de parsing direto do MDF:
   - **OrcaMDF/OrcaSql**: extrair tabelas e gerar scripts de correção por página.
   - **SQLServerForensics (MSSQLParser)**: ler e exportar dados (CSV/para SQL) mesmo de arquivo corrompido.
3. **Nunca rodar REPAIR_ALLOW_DATA_LOSS** no original — sempre em cópia e como último recurso.

### Etapa 7 — Exportar a base recuperada
```bash
docker exec -it "$CONTAINER_NAME" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE [$DB_NAME] TO DISK='/var/opt/mssql/backups/${DB_NAME}_recuperado.bak' WITH INIT;"
```
Copiar o `.bak` para um destino seguro.

---

## 3. Ferramentas Livres Consideradas

### Oficiais / recomendadas (prioridade)
| Ferramenta | Tipo | Uso |
|------------|------|-----|
| **SQL Server Developer (Docker)** | Imagem oficial MS (gratuita) | Ambiente para attach + CHECKDB + reparo |
| **`sqlcmd`** | CLI oficial MS (gratuita) | Executar T-SQL no contêiner |
| **`DBCC CHECKDB`** | Comando nativo do SQL Server | Detectar e reparar corrupção |
| **`DBCC CHECKTABLE`** | Comando nativo | Inspeção por tabela |
| **Backup/Restore nativo** | Comando nativo | Exportar base limpa |
| **`slow_recovery.tsql`** (requisito de referência) | Script T-SQL (Base do repo `Bobirmirzo/sql_server_recovery`) | Monitorar progresso de recovery via Extended Events — útil para bases muito grandes onde o recovery pode ser lento/moroso |

### Terceiros (referência para casos específicos)
| Repositório | O que resolve | Notas |
|-------------|---------------|-------|
| `wojtulab/sqlserver-hack` | Descoberta + recuperar ACESSO a instância (senha/permissão sysadmin) | Não é o objetivo aqui; guardado como referência caso o problema seja de acesso e não corrupção |
| `Bobirmirzo/sql_server_recovery` (`slow_recovery.tsql`) | Monitorar recuperação lenta (Extended Events) | Útil p/ bases muito grandes |
| `ycherkes/OrcaSql` (fork do OrcaMDF) | **Parser C# de arquivos MDF sem SQL Server**. Lê tabelas/metadados/índices; gera script para corrigir página corrompida; exporta dados da base para SQL Server | Excelente para extrair dados quando o attach/CHECKDB falha por corrupção severa |
| `aarsakian/SQLServerForensics` (MSSQLParser) | Ferramenta forense que lê MDF/LDF/BAK **diretamente, sem SQL Server**; processa tabelas, faz *carving* de registros deletados, correlação com log | Leitura read-only; útil para extrair o máximo de dados antes/em vez do REPAIR destrutivo |
| `leandrobocon/recoversqlserver` | **Repositório de destino deste projeto** (vazio no momento) | Guarda o plano, ferramentas e scripts |

---

## 4. Riscos e Mitigações

| Risco | Mitigação |
|-------|-----------|
| Perda de dados com `REPAIR_ALLOW_DATA_LOSS` | Sempre backup/inspeção antes; usar `REPAIR_REBUILD` sempre que possível; registrar o que foi perdido |
| Alterar arquivo original | Trabalhar apenas sobre cópias no volume de trabalho; original montado read-only |
| Log corrompido impossibilita attach normal | Tentar attach **somente MDF** (`CREATE DATABASE ... FOR ATTACH_REBUILD_LOG`) que recria o log |
| Consumo de RAM (bases muito grandes) | Para bases de dezenas/centenas de GB usar monitoramento (`slow_recovery.tsql`) e possivelmente edição maior/Express |
| Porta 1433 em uso | Alterar mapeamento `-p` do contêiner para porta custom (ex.: `-p 14333:1433`) |

---

## 5. Próximos Passos (quando autorizado a executar)
1. `docker pull` da imagem (download grande, ~1.5 GB).
2. Subir contêiner e anexar a base definida no `.env` (`$DB_NAME`).
3. `DBCC CHECKDB` diagnóstico em modo somente leitura/inspeção.
4. Aplicar `REPAIR_REBUILD`; se houver erro estrutural grave, avaliar `REPAIR_ALLOW_DATA_LOSS`.
5. Validar + exportar `.bak` recuperado.
6. Repetir o fluxo para outras bases (ex.: `DB2_*` no `.env`) em fase posterior.
