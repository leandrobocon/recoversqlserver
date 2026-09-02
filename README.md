# recoversqlserver

Ferramentas livres para **recuperação offline de dados** de bases SQL Server que não podem ser anexadas via Engine (corrupção de metadata, partitions inválidos, headers corrompidos).

> **Status**: funcional para extração de dados.

---

## O que faz

Parsing direto do arquivo `.mdf` (sem SQL Server) para extrair registros de tabelas, quando o catálogo interno do banco está corrompido e o `ATTACH` falha.

## Componentes

| Pasta | Descrição |
|-------|-----------|
| [`orcacli/`](orcacli/) | CLI que implementa mapeamento automático e extração física de tabelas |
| [`OrcaSql/src/OrcaSql.Core/`](OrcaSql/src/OrcaSql.Core/) | Fork do [ycherkes/OrcaSql](https://github.com/ycherkes/OrcaSql) com extensões para scan físico por OID |
| [`scripts/`](scripts/) | Scripts de setup Docker |

## Como funciona

Quando o catálogo `sysschobjs`/`syscolpars` está corrompido, a ferramenta mapeia cada tabela ao seu **Object ID físico** usando:

1. **Contagem de slots** por página (header offset 22) como fingerprint
2. **Ground truth** do Stellar ou outro dump para validar mapeamentos
3. **Scan direto** das páginas tipo 1/2 (leaf) com o schema da tabela

## Modo de uso

```bash
# Build
cd orcacli && dotnet build -c Release

# Extração via catálogo (partitions válidos)
dotnet run -c Release --no-build -- /caminho/banco.mdf /caminho/saida

# Mapeamento automático (requer log de contagens como ground truth)
AUTOMAP=1 dotnet run -c Release --no-build -- /caminho/banco.mdf /caminho/saida

# Exportação via mapeamento físico (rode AUTOMAP antes)
EXPORTPHYS=1 dotnet run -c Release --no-build -- /caminho/banco.mdf /caminho/saida

# Modo diagnóstico
DIAGSLOT=1 dotnet run -c Release --no-build -- /caminho/banco.mdf /caminho/saida TABELA
DIAGPHYSOID=1 PHYS_OID=593 dotnet run -c Release --no-build -- /caminho/banco.mdf /caminho/saida TABELA
```

## Extensões ao OrcaSql (neste fork)

- `ScanTableByObjectId(name, oid, withText)` — extrai linhas de uma tabela por OID físico
- `SlotCountsByObjectId()` — mapeia OID → (soma slots, páginas) usando page header
- `AutoMapTables(tableNames, stellarCounts)` — mapeamento automático gulosa por confiança
- `PageLeafObjectIds()` — lista oids de páginas tipo 1/2 leaf
- Correção de `ScanLinkedDataPages` (yield + try-catch)

## Referências

- [ycherkes/OrcaSql](https://github.com/ycherkes/OrcaSql) — parser de MDF sem SQL Server
- [aarsakian/SQLServerForensics](https://github.com/aarsakian/SQLServerForensics) — leitura forense de MDF/LDF
