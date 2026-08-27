# recoversqlserver

Repositório para planejamento e execução da recuperação de bases **SQL Server corrompidas** usando ferramentas **livres**.

> Status: planejamento/em construção — recuperação da base `db_acervo` (corrompida).


## Abordagem
1. **DBCC CHECKDB** via imagem oficial `mcr.microsoft.com/mssql/server` em **Docker** (gratuita).
2. **Ferramentas livres de parsing direto do MDF** (OrcaMDF/OrcaSql, SQLServerForensics) quando o CHECKDB não conseguir recuperar.
3. Reparo menos destrutivo primeiro; sempre em **cópia** do arquivo original.

## Documentação
- [`PLANO.md`](PLANO.md) — plano completo: ambiente, etapas, ferramentas, riscos e próximos passos.

## Ferramentas livres de referência
- [ycherkes/OrcaSql](https://github.com/ycherkes/OrcaSql) — parser de MDF sem SQL Server
- [aarsakian/SQLServerForensics](https://github.com/aarsakian/SQLServerForensics) — leitura forense de MDF/LDF/BAK
- [wojtulab/sqlserver-hack](https://github.com/wojtulab/sqlserver-hack) — recuperação de acesso à instância (referência)
- [Bobirmirzo/sql_server_recovery](https://github.com/Bobirmirzo/sql_server_recovery) — monitorar recuperação lenta (referência)
