# Relatório de Diagnóstico — Base `db_acervo` (criptografia TDE)

> Data: 2026-08-27
> Resumo: a base não está tecnicamente "corrompida" de forma recuperável — está **criptografada com TDE** e falta a chave para abri-la.

---

## 1. O que foi feito

Pipeline de recuperação executado (Docker + imagem oficial `mcr.microsoft.com/mssql/server:2022-latest`):

1. `docker pull` da imagem oficial ✓
2. Contêiner SQL Server 2022 criado e iniciado ✓
3. Arquivos `db_acervo.mdf` + `db_acervo_log.ldf` copiados para o volume de trabalho ✓
4. **`CREATE DATABASE ... FOR ATTACH` → FALHOU** com erro 824

## 2. Erro observado (causa raiz)

```
Msg 824, Level 24: SQL Server detected a logical consistency-based I/O error:
unable to decrypt page due to missing DEK.
... during a read of page (0:0) in ... '/var/opt/mssql/data/db_acervo_log.ldf'.
```

**Interpretação:** o erro "missing DEK" (Database Encryption Key) indica que o banco foi criado com **TDE (Transparent Data Encryption)**. As páginas do `.mdf` e do `.ldf` estão criptografadas (AES) em repouso. Para anexar e ler, é obrigatório recompor a **cadeia de criptografia**:

```
Service Master Key (SMK)  [no master.mdf do servidor de origem]
      ↓
Database Master Key (DMK) [no master.mdf]
      ↓
Certificado TDE (chave privada .pvk)  [no master.mdf]
      ↓
Database Encryption Key (DEK)  [dentro deste banco]
      ↓
páginas de dados do .mdf/.ldf
```

`DBCC CHECKDB` e ferramentas de parsing offline (**OrcaMDF, SQLServerForensics, DBA_LogReader**) **não** conseguem extrair dados de banco TDE: os dados estão cifrados e dependem da chave. Esse não é um cenário de "corrupção reparável".

## 3. Arquivos disponíveis no ambiente

- `db_acervo.mdf` / `db_acervo_log.ldf` — banco criptografado (sem a chave → inacessível).
- `/var/tmp/MS_AgentSigningCertificate.cer` — **NÃO é o certificado TDE**. É o certificado de assinatura do **SQL Agent** (autoassinado, `CN=MS_AgentSigningCertificate`, validade 2018–2019). Além disso, é um `.cer` (somente chave **pública**); para TDE é necessária a **chave privada (`.pvk`)** do certificado que protege a DEK.
- **Não há** `master.bak`, `master.mdf`, `.pvk`, `.pfx` ou backup de SMK/DMK/certificado TDE no ambiente.

## 4. Vias de recuperação (por viabilidade e legitimidade)

| # | O que ter | Como usar | Viabilidade |
|---|-----------|-----------|-------------|
| 1 | **Certificado TDE (`.cer` + `.pvk`)** + senha da chave privada | `CREATE CERTIFICATE ... FROM FILE=... WITH PRIVATE KEY (FILE=..., DECRYPTION BY PASSWORD=...)` e então anexar/restaurar o banco | ✅ Total — caminho padrão e recomendado |
| 2 | **Backup do `master.mdf`** do servidor de origem + mesmo service account (domínio) | Restaurar `master`, reiniciar, `BACKUP CERTIFICATE` e usar como no item 1 | ✅ Se houver backup do master |
| 3 | **Backup do SMK** + senha | `RESTORE SERVICE MASTER KEY ... FORCE` → destrava toda a cadeia | ✅ Se houver backup do SMK |
| 4 | **Servidor de origem ainda ativo** | Executar `BACKUP CERTIFICATE ... WITH PRIVATE KEY` / `BACKUP SERVICE MASTER KEY` antes de perder acesso | ✅ Se o servidor existir |
| 5 | **Forçar senha fraca de DMK antiga** (tipo ESKP = MD5+sal) com Hashcat/John | Só se tiver o hash extraído do `master` e a senha for fraca | ⚠️ Improvável |

> **Sem nenhuma dessas peças, os dados estão criptograficamente inacessíveis.** Não existe atalho/ferramenta que decifre um `.mdf` TDE sem (parte de) a cadeia de chaves.

## 5. Conclusão e próximos passos

- O pipeline de recuperação está **pronto e funcional** (`scripts/recover.sh`); ele anexa a base assim que a chave estiver disponível.
- **Bloqueio:** obtenção do certificado TDE + chave privada (ou backup do `master`/SMK), do servidor de origem legítimo.
- Assim que o usuário fornecer o certificado `.cer`/`.pvk` e a senha, executar:
  ```bash
  # 1) importar o certificado
  CREATE CERTIFICATE <nome> FROM FILE='/var/opt/mssql/data/<cert>.cer'
    WITH PRIVATE KEY (FILE='/var/opt/mssql/data/<cert>.pvk',
                      DECRYPTION BY PASSWORD = '<senha>');
  # 2) anexar a base
  scripts/recover.sh attach
  ```
- A infraestrutura (contêiner) está preservada em `/var/sqlserver/data`.
