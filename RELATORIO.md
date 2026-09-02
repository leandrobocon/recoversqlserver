# Relatório de Diagnóstico — Extração offline de dados de MDF corrompido

> Data: 2026-08-27 → 2026-09-02
> Resumo: recuperação de dados de base SQL Server que não pode ser anexada via Engine, usando parsing direto do arquivo `.mdf`.

---

## 1. O que foi feito

Pipeline de extração executado com **Docker** + imagem oficial `mcr.microsoft.com/mssql/server`:

1. Contêiner SQL Server 2022 criado e iniciado ✓
2. Arquivos `.mdf` e `.ldf` copiados para o volume de trabalho ✓
3. `CREATE DATABASE ... FOR ATTACH` → FALHOU com erro 824 (problemas de integridade/headers)

## 2. Causa raiz

```
Msg 824, Level 24: SQL Server detected a logical consistency-based I/O error:
```

A base apresenta **corrupção de metadata** (catálogos `sysrowsets`, `syscolpars`, partitions) que impede o anexo via SQL Server Engine. As páginas de dados (registros reais) estão majoritariamente íntegras e acessíveis via parsing direto do arquivo.

## 3. Abordagem de recuperação

Usando parsing direto do `.mdf` com **OrcaSql** (fork customizado):

| Etapa | Status |
|-------|--------|
| Diagnóstico do catálogo corrompido | ✅ |
| Mapeamento tabela → OID físico (via contagem de slots + Stellar log) | ✅ 64/65 tabelas com dados |
| Extração física via `ScanTableByObjectId` | ✅ Funcional |
| Extração via catálogo (tabelas com partitions válidos) | ✅ 34 tabelas |

## 4. Ferramentas

Ferramenta CLI: [`orcacli`](orcacli/) — wrapper sobre OrcaSql que implementa:

- **`AUTOMAP`**: Mapeamento automático tabela → OID físico usando contagem de slots por página como fingerprint e contagens do Stellar como ground truth
- **`EXPORTPHYS`**: Extração via scan físico (sem depender do catálogo SQL Server)
- Modos de diagnóstico: `DIAGSCAN`, `DIAGPHYSOID`, `DIAGSLOT`, `DIAGCOL`, `DIAGIAM`

## 5. Estrutura do catálogo corrompido

O catálogo `sysschobjs.id`/`syscolpars.id` apresenta **numeração desalinhada** com os IDs físicos das páginas (header offset 24-27). Exemplo:

| Tabela | ID no catálogo | OID físico (page header) | Status |
|--------|---------------|-------------------------|--------|
| Tabela A | 14623095 | 593 | ✅ Extraída via OID físico |
| Tabela B | 2062630391 | 1020 | ✅ Extraída via OID físico |

## 6. Resultado

- **64 tabelas** com dados extraídas via mapeamento automático (OID físico)
- **34 tabelas** extraídas via catálogo (partitions válidos)
- Arquivos CSV + SQL INSERT gerados em `extract/`
- 1 tabela (maior) sem candidato OID mapeado — investigação pendente

## 7. Próximos passos

- Resolver mapeamento da tabela restante
- Validar integridade dos dados exportados contra produção
- Automatizar pipeline completo
