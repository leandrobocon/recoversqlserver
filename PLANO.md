# Plano de Recuperação — Base `db_acervo` (SQL Server)

> **Status**: PLANEJAMENTO — ainda não executar.
> **Objetivo**: reparar a base corrompida `db_acervo` (`db_acervo.mdf` + `db_acervo_log.ldf`).

---

## 1. Contexto e Ambiente

### Base de dados a recuperar
| Arquivo | Tamanho | Caminho |
|---------|---------|---------|
| `db_acervo.mdf` (dados) | 2.889.875.456 bytes (~2.7 GB) | `/var/tmp/db_acervo.mdf` |
| `db_acervo_log.ldf` (log) | 224.526.336 bytes (~214 MB) | `/var/tmp/db_acervo_log.ldf` |

> Obs.: existe também a base `FACEAR_ICO` (105 GB) em `/var/tmp`, fora do escopo desta primeira tentativa (ficará para uma fase posterior).

### Ambiente de execução
- SO: Linux (Ubuntu), sem SQL Server nativo instalado.
- **Docker disponível** e em funcionamento.
- Recursos: 4 CPUs, ~7.7 GB RAM (5 GB disponíveis), ~145 GB livres em `/var`.
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
```bash
mkdir -p /var/sqlserver/data

docker run -d --name mssql-acervo \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=SuaSenha@Segura123" \
  -e "MSSQL_PID=Developer" \
  -p 1433:1433 \
  -v /var/tmp:/mnt/data:ro \
  -v /var/sqlserver/data:/var/opt/mssql \
  mcr.microsoft.com/mssql/server:2022-latest
```
> Os arquivos originais são montados **somente-leitura** de `/var/tmp` em `/mnt/data` para inspeção e cópia.

### Etapa 2 — Copiar os arquivos para dentro do ambiente de trabalho
Como o contêiner não deve editar os originais, copiar para o volume de trabalho antes de alterar:
```bash
docker exec mssql-acervo bash -c "cp /mnt/data/db_acervo.mdf /var/opt/mssql/data/ && cp /mnt/data/db_acervo_log.ldf /var/opt/mssql/data/"
```
> Isso garante traçabilidade e preserva o original intocado.

### Etapa 3 — Anexar a base (attach)
Usando `sqlcmd` dentro do contêiner:
```bash
docker exec -it mssql-acervo /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'SuaSenha@Segura123' -C -Q "
CREATE DATABASE db_acervo
  ON (FILENAME = '/var/opt/mssql/data/db_acervo.mdf'),
     (FILENAME = '/var/opt/mssql/data/db_acervo_log.ldf')
  FOR ATTACH;"
```

### Etapa 4 — Primeira inspeção (sem alterar)
```bash
docker exec -it mssql-acervo /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'SuaSenha@Segura123' -C -Q "DBCC CHECKDB (db_acervo) WITH NO_INFOMSGS, TABLERESULTS;"
```
Análise dos resultados: tipo e quantidade de erros (alocação, consistência, lógica).

### Etapa 5 — Estratégia de reparo (gradual e por prioridade)
> **Importante**: tentar SEMPRE o reparo menos destrutivo primeiro.

1. **`REPAIR_REBUILD`** — reparo rápido, via backup de log ou índice, sem perda de dados:
   ```sql
   ALTER DATABASE db_acervo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DBCC CHECKDB (db_acervo, REPAIR_REBUILD) WITH NO_INFOMSGS;
   ALTER DATABASE db_acervo SET MULTI_USER;
   ```
2. **`REPAIR_ALLOW_DATA_LOSS`** — ÚLTIMO recurso (pode perder dados); requer backup antes:
   ```sql
   ALTER DATABASE db_acervo SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DBCC CHECKDB (db_acervo, REPAIR_ALLOW_DATA_LOSS) WITH NO_INFOMSGS;
   ALTER DATABASE db_acervo SET MULTI_USER;
   ```

### Etapa 6 — Validação pós-reparo
- Rodar `DBCC CHECKDB (db_acervo) WITH NO_INFOMSGS;` novamente até zerar erros.
- Testar consulta de sanidade em algumas tabelas e contagem de linhas.

### Estratégia principal escolhida
1. **Tentar DBCC CHECKDB (Docker)** primeiro — ferramenta oficial, gratuita, melhor resultado para corrupção estrutural recuperável.
2. **Se o DBCC não conseguir anexar/reparar** (corrupção severa de páginas de dados/alocação), usar as ferramentas livres de parsing direto do MDF:
   - **OrcaMDF/OrcaSql**: extrair tabelas e gerar scripts de correção por página.
   - **SQLServerForensics (MSSQLParser)**: ler e exportar dados (CSV/para SQL) mesmo de arquivo corrompido.
3. **Nunca rodar REPAIR_ALLOW_DATA_LOSS** no original — sempre em cópia e como último recurso.

### Etapa 7 — Exportar a base recuperada
```bash
docker exec -it mssql-acervo /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'SuaSenha@Segura123' -C \
  -Q "BACKUP DATABASE db_acervo TO DISK='/var/opt/mssql/backups/db_acervo_recuperado.bak' WITH INIT;"
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
| **`slow_recovery.tsql`** (requisito de referência) | Script T-SQL (Base do repo `Bobirmirzo/sql_server_recovery`) | Monitorar progresso de recovery via Extended Events — útil para `FACEAR_ICO` (105 GB) onde o recovery pode ser lento/moroso |

### Terceiros (referência para casos específicos)
| Repositório | O que resolve | Notas |
|-------------|---------------|-------|
| `wojtulab/sqlserver-hack` | Descoberta + recuperar ACESSO a instância (senha/permissão sysadmin) | Não é o objetivo aqui; guardado como referência caso o problema seja de acesso e não corrupção |
| `Bobirmirzo/sql_server_recovery` (`slow_recovery.tsql`) | Monitorar recuperação lenta (Extended Events) | Útil p/ base de 105 GB (`FACEAR_ICO`) |
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
| Consumo de RAM (base de 105 GB no futuro) | Para `FACEAR_ICO` usar monitoramento (`slow_recovery.tsql`) e possivelmente edição maior/Express |
| Porta 1433 em uso | Alterar mapeamento `-p` do contêiner para porta custom (ex.: `-p 14333:1433`) |

---

## 5. Próximos Passos (quando autorizado a executar)
1. `docker pull` da imagem (download grande, ~1.5 GB).
2. Subir contêiner e anexar `db_acervo`.
3. `DBCC CHECKDB` diagnóstico em modo somente leitura/inspeção.
4. Aplicar `REPAIR_REBUILD`; se houver erro estrutural grave, avaliar `REPAIR_ALLOW_DATA_LOSS`.
5. Validar + exportar `.bak` recuperado.
6. Repetir o fluxo para `FACEAR_ICO` (105 GB) em fase posterior.
