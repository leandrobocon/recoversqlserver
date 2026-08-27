#!/usr/bin/env bash
#
# recover.sh — Pipeline de recuperação de base SQL Server corrompida.
#
# Uso:
#   scripts/recover.sh pull          # baixa a imagem oficial
#   scripts/recover.sh start         # cria/subir o contêiner
#   scripts/recover.sh copy          # copia .mdf/.ldf p/ volume de trabalho
#   scripts/recover.sh import-cert   # importa certificado TDE (ver .env TDE_*)
#   scripts/recover.sh attach        # anexa o banco
#   scripts/recover.sh check         # DBCC CHECKDB (diagnóstico, não altera)
#   scripts/recover.sh repair        # tenta REPAIR_REBUILD (sem perda)
#   scripts/recover.sh repair-loss   # tenta REPAIR_ALLOW_DATA_LOSS (perda)
#   scripts/recover.sh validate      # re-checa o banco após reparo
#   scripts/recover.sh backup        # exporta .bak recuperado
#   scripts/recover.sh stop          # derruba o contêiner
#
# Config via arquivo .env na raiz do projeto (rever .env.example).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$ROOT_DIR/.env"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "ERRO: arquivo $ENV_FILE não encontrado. Copie .env.example para .env e preencha." >&2
  exit 1
fi

set -a; source "$ENV_FILE"; set +a

SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
# Preferir sqlcmd "go" se disponível no host, senão usar dentro do contêiner.
HOST_SQLCMD="$(command -v sqlcmd || true)"

require_container() {
  if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    echo "ERRO: contêiner '$CONTAINER_NAME' não está rodando. Execute: $0 start" >&2
    exit 1
  fi
}

run_sql() {
  # $1 = comando T-SQL
  docker exec -i "$CONTAINER_NAME" "$SQLCMD" \
    -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "$1"
}

cmd_pull() {
  docker pull "$MSSQL_IMAGE"
}

cmd_start() {
  mkdir -p "$WORK_DATA_DIR/backups"
  docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
  docker run -d --name "$CONTAINER_NAME" \
    -e "ACCEPT_EULA=Y" \
    -e "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD" \
    -e "MSSQL_PID=$MSSQL_PID" \
    -p "$MSSQL_PORT:1433" \
    -v "$SRC_DATA_DIR:/mnt/data:ro" \
    -v "$WORK_DATA_DIR:/var/opt/mssql" \
    "$MSSQL_IMAGE"
  echo "Aguardando SQL Server iniciar..."
  for i in $(seq 1 30); do
    if docker exec "$CONTAINER_NAME" "$SQLCMD" -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
      echo "SQL Server pronto."
      return 0
    fi
    sleep 2
  done
  echo "ERRO: SQL Server não respondeu a tempo." >&2
  exit 1
}

cmd_copy() {
  require_container
  docker exec "$CONTAINER_NAME" bash -c "cp -n /mnt/data/$DB_MDF /var/opt/mssql/data/ && cp -n /mnt/data/$DB_LDF /var/opt/mssql/data/" || true
  echo "Arquivos copiados para o volume de trabalho."
}

cmd_attach() {
  require_container
  run_sql "
CREATE DATABASE [$DB_NAME]
  ON (FILENAME = '/var/opt/mssql/data/$DB_MDF'),
     (FILENAME = '/var/opt/mssql/data/$DB_LDF')
  FOR ATTACH;
"
  echo "Base anexada: $DB_NAME"
}

cmd_check() {
  require_container
  run_sql "DBCC CHECKDB ([$DB_NAME]) WITH NO_INFOMSGS, TABLERESULTS;"
}

cmd_repair() {
  require_container
  run_sql "
ALTER DATABASE [$DB_NAME] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
BEGIN TRY
  DBCC CHECKDB ([$DB_NAME], REPAIR_REBUILD) WITH NO_INFOMSGS;
  ALTER DATABASE [$DB_NAME] SET MULTI_USER;
  PRINT 'REPAIR_REBUILD OK';
END TRY
BEGIN CATCH
  PRINT 'REPAIR_REBUILD FALHOU: ' + ERROR_MESSAGE();
  ALTER DATABASE [$DB_NAME] SET MULTI_USER;
END CATCH
"
}

cmd_repair_loss() {
  require_container
  run_sql "
ALTER DATABASE [$DB_NAME] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
BEGIN TRY
  DBCC CHECKDB ([$DB_NAME], REPAIR_ALLOW_DATA_LOSS) WITH NO_INFOMSGS;
  ALTER DATABASE [$DB_NAME] SET MULTI_USER;
  PRINT 'REPAIR_ALLOW_DATA_LOSS OK';
END TRY
BEGIN CATCH
  PRINT 'REPAIR_ALLOW_DATA_LOSS FALHOU: ' + ERROR_MESSAGE();
  ALTER DATABASE [$DB_NAME] SET MULTI_USER;
END CATCH
"
}

cmd_validate() {
  require_container
  run_sql "DBCC CHECKDB ([$DB_NAME]) WITH NO_INFOMSGS;"
}

# Importa o certificado TDE (e chave privada) necessário para abrir o banco.
# Usa variáveis do .env: TDE_CERT_NAME, TDE_CERT_FILE, TDE_CERT_KEY, TDE_CERT_PASSWORD
cmd_import_cert() {
  require_container
  : "${TDE_CERT_NAME:?defina TDE_CERT_NAME no .env}"
  : "${TDE_CERT_FILE:?defina TDE_CERT_FILE no .env}"
  : "${TDE_CERT_KEY:?defina TDE_CERT_KEY (path .pvk) no .env}"
  : "${TDE_CERT_PASSWORD:?defina TDE_CERT_PASSWORD no .env}"
  docker exec "$CONTAINER_NAME" bash -c \
    "cp /mnt/data/$TDE_CERT_FILE /var/opt/mssql/data/ && cp /mnt/data/$TDE_CERT_KEY /var/opt/mssql/data/"
  run_sql "
IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = '$TDE_CERT_NAME')
  DROP CERTIFICATE [$TDE_CERT_NAME];
CREATE CERTIFICATE [$TDE_CERT_NAME]
  FROM FILE = '/var/opt/mssql/data/$TDE_CERT_FILE'
  WITH PRIVATE KEY (FILE = '/var/opt/mssql/data/$TDE_CERT_KEY',
                    DECRYPTION BY PASSWORD = '$TDE_CERT_PASSWORD');
PRINT 'Certificado TDE importado: $TDE_CERT_NAME';
"
}

cmd_backup() {
  require_container
  run_sql "BACKUP DATABASE [$DB_NAME] TO DISK='/var/opt/mssql/backups/${DB_NAME}_recuperado.bak' WITH INIT;"
  echo "Backup gerado em $WORK_DATA_DIR/backups/${DB_NAME}_recuperado.bak"
}

cmd_stop() {
  docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
  echo "Contêiner '${CONTAINER_NAME}' removido (dados preservados em $WORK_DATA_DIR)."
}

case "${1:-}" in
  pull)           cmd_pull ;;
  start)          cmd_start ;;
  copy)           cmd_copy ;;
  import-cert)    cmd_import_cert ;;
  attach)         cmd_attach ;;
  check)          cmd_check ;;
  repair)         cmd_repair ;;
  repair-loss)    cmd_repair_loss ;;
  validate)       cmd_validate ;;
  backup)         cmd_backup ;;
  stop)           cmd_stop ;;
  *) echo "Uso: $0 {pull|start|copy|attach|check|repair|repair-loss|validate|backup|stop}"; exit 1 ;;
esac
