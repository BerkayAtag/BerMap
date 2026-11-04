#!/usr/bin/env bash
# Installs or upgrades the BerMap server as a systemd service.
#
#   sudo ./install.sh --db-host localhost --db-user bermap            # prompts for the password
#   sudo BERMAP_DB_PASSWORD=... ./install.sh --db-host pg01 --db-user bermap --port 8080
#
# PostgreSQL 16+ must already be reachable; the database is created and migrated
# on first start when the user may create databases. Running the script again
# upgrades in place and keeps the existing configuration, vault and database
# unless new database values are given.
set -euo pipefail

INSTALL_DIR=/opt/bermap/server
DATA_DIR=/var/lib/bermap
CONFIG_DIR=/etc/bermap
SERVICE=bermap-server
PORT=8080
DB_HOST=""
DB_PORT=5432
DB_NAME=bermap
DB_USER=""

usage() {
  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
  echo "Options: --db-host --db-port --db-name --db-user --port --install-dir --data-dir"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db-host) DB_HOST="$2"; shift 2 ;;
    --db-port) DB_PORT="$2"; shift 2 ;;
    --db-name) DB_NAME="$2"; shift 2 ;;
    --db-user) DB_USER="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --data-dir) DATA_DIR="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

PACKAGE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ ! -x "$PACKAGE_DIR/server/BerMap.Api" ]]; then
  echo "server/BerMap.Api was not found next to this script. Run it from the extracted release package." >&2
  exit 1
fi

CONFIG_FILE="$CONFIG_DIR/appsettings.Production.json"

if ! id bermap >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin bermap
fi

# Configuration: new on first install or when database values are given.
if [[ ! -f "$CONFIG_FILE" || -n "$DB_HOST" || -n "$DB_USER" ]]; then
  [[ -n "$DB_HOST" ]] || read -r -p "PostgreSQL host: " DB_HOST
  [[ -n "$DB_USER" ]] || read -r -p "PostgreSQL user: " DB_USER
  DB_PASSWORD="${BERMAP_DB_PASSWORD:-}"
  if [[ -z "$DB_PASSWORD" ]]; then
    read -r -s -p "Password for PostgreSQL user '$DB_USER': " DB_PASSWORD
    echo
  fi

  TOKEN=""
  if [[ -f "$CONFIG_FILE" ]]; then
    TOKEN="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["BerMap"]["Collectors"]["EnrollmentToken"])' "$CONFIG_FILE" 2>/dev/null || true)"
  fi
  [[ -n "$TOKEN" ]] || TOKEN="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"

  mkdir -p "$CONFIG_DIR"
  # The file holds the database password: written without world access.
  (umask 027
  DB_HOST="$DB_HOST" DB_PORT="$DB_PORT" DB_NAME="$DB_NAME" DB_USER="$DB_USER" DB_PASSWORD="$DB_PASSWORD" \
  TOKEN="$TOKEN" PORT="$PORT" DATA_DIR="$DATA_DIR" python3 - "$CONFIG_FILE" <<'PY'
import json, os, sys

def quote(value):
    # Npgsql connection string value quoting.
    return '"' + value.replace('"', '""') + '"' if any(c in value for c in ';="\' ') else value

env = os.environ
connection = ";".join([
    f"Host={quote(env['DB_HOST'])}", f"Port={env['DB_PORT']}", f"Database={quote(env['DB_NAME'])}",
    f"Username={quote(env['DB_USER'])}", f"Password={quote(env['DB_PASSWORD'])}",
])
config = {
    "ConnectionStrings": {"DefaultConnection": connection},
    "BerMap": {
        "CredentialVault": {"Directory": os.path.join(env["DATA_DIR"], "vault")},
        "Collectors": {"EnrollmentToken": env["TOKEN"]},
    },
    "Kestrel": {"Endpoints": {"Default": {"Url": f"http://0.0.0.0:{env['PORT']}"}}},
}
with open(sys.argv[1], "w") as handle:
    json.dump(config, handle, indent=2)
PY
  )
  chown root:bermap "$CONFIG_FILE"
  chmod 640 "$CONFIG_FILE"
fi

PORT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Kestrel"]["Endpoints"]["Default"]["Url"].rsplit(":",1)[1])' "$CONFIG_FILE")"
TOKEN="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["BerMap"]["Collectors"]["EnrollmentToken"])' "$CONFIG_FILE")"

if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  systemctl stop "$SERVICE"
fi

echo "==> Copying server files to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
rm -rf "${INSTALL_DIR:?}/"*
cp -a "$PACKAGE_DIR/server/." "$INSTALL_DIR/"
ln -sf "$CONFIG_FILE" "$INSTALL_DIR/appsettings.Production.json"
chown -R root:root "$INSTALL_DIR"

mkdir -p "$DATA_DIR/vault"
chown -R bermap:bermap "$DATA_DIR"
chmod 700 "$DATA_DIR/vault"

sed -e "s#@INSTALL_DIR@#$INSTALL_DIR#g" -e "s#@DATA_DIR@#$DATA_DIR#g" \
  "$PACKAGE_DIR/bermap-server.service" > "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl start "$SERVICE"

echo "==> Waiting for http://localhost:$PORT/healthz/ready"
for _ in $(seq 1 90); do
  if curl -fsS "http://localhost:$PORT/healthz/ready" >/dev/null 2>&1; then
    echo
    echo "BerMap is running: http://$(hostname -f 2>/dev/null || hostname):$PORT"
    if [[ -f "$DATA_DIR/vault/initial-admin-password.txt" ]]; then
      echo "Initial administrator (delete the file after the first sign-in):"
      sed 's/^/  /' "$DATA_DIR/vault/initial-admin-password.txt"
      echo "  file: $DATA_DIR/vault/initial-admin-password.txt"
    fi
    echo "Collector enrollment token: $TOKEN"
    echo "The console is served over plain HTTP; publish it through a TLS reverse proxy (nginx, HAProxy)."
    exit 0
  fi
  sleep 2
done

echo "BerMap did not become ready. See: journalctl -u $SERVICE -n 100" >&2
exit 1
