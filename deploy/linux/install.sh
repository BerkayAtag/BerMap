#!/usr/bin/env bash
# Installs or upgrades the BerMap server as a systemd service.
#
#   sudo ./install.sh --db-host localhost --db-user bermap            # prompts for the password
#   sudo BERMAP_DB_PASSWORD=... ./install.sh --db-host pg01 --db-user bermap --port 8080
#
# PostgreSQL 16+ must already be reachable; the database is created and migrated
# on first start when the user may create databases. Running the script again
# upgrades in place and keeps the existing configuration, vault and database:
# only the values given change (--db-*, --port; BERMAP_DB_PASSWORD alone rotates
# the database password). Settings added by hand are kept.
set -euo pipefail

INSTALL_DIR=/opt/bermap/server
DATA_DIR=/var/lib/bermap
CONFIG_DIR=/etc/bermap
SERVICE=bermap-server
UNIT_FILE="/etc/systemd/system/$SERVICE.service"
# Written into the install directory; only a directory carrying it is ever cleared.
MARKER=.bermap-server
# Empty means "not given": an upgrade keeps the configured value.
PORT=""
DB_HOST=""
DB_PORT=""
DB_NAME=""
DB_USER=""
INSTALL_DIR_SET=false
DATA_DIR_SET=false

usage() {
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
  echo "Options: --db-host --db-port --db-name --db-user --port --install-dir --data-dir"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db-host) DB_HOST="$2"; shift 2 ;;
    --db-port) DB_PORT="$2"; shift 2 ;;
    --db-name) DB_NAME="$2"; shift 2 ;;
    --db-user) DB_USER="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; INSTALL_DIR_SET=true; shift 2 ;;
    --data-dir) DATA_DIR="$2"; DATA_DIR_SET=true; shift 2 ;;
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

for value in "$PORT" "$DB_PORT"; do
  if [[ -n "$value" ]] && ! [[ "$value" =~ ^[0-9]+$ && "$value" -ge 1 && "$value" -le 65535 ]]; then
    echo "Invalid port: $value" >&2
    exit 2
  fi
done

# An upgrade without --install-dir/--data-dir keeps the directories the unit uses.
UNIT_INSTALL_DIR=""
if [[ -f "$UNIT_FILE" ]]; then
  UNIT_INSTALL_DIR="$(sed -n 's/^WorkingDirectory=//p' "$UNIT_FILE" | head -n 1)"
  if ! $INSTALL_DIR_SET && [[ -n "$UNIT_INSTALL_DIR" ]]; then
    INSTALL_DIR="$UNIT_INSTALL_DIR"
  fi
  if ! $DATA_DIR_SET; then
    current="$(sed -n 's/^ReadWritePaths=//p' "$UNIT_FILE" | head -n 1)"
    [[ -z "$current" ]] || DATA_DIR="$current"
  fi
fi

for dir in "$INSTALL_DIR" "$DATA_DIR"; do
  [[ "$dir" == /?* ]] || { echo "Directories must be absolute paths: $dir" >&2; exit 2; }
done
case "${DATA_DIR%/}/" in "${INSTALL_DIR%/}/"*) overlap=true ;; *) overlap=false ;; esac
case "${INSTALL_DIR%/}/" in "${DATA_DIR%/}/"*) overlap=true ;; esac
if $overlap; then
  echo "--install-dir and --data-dir must not contain each other: $INSTALL_DIR, $DATA_DIR" >&2
  exit 2
fi

# The install directory is cleared on every upgrade: refuse anything that is not
# empty and was not created by this script (for example --install-dir /opt). The
# directory the installed unit runs from counts as created by this script.
if [[ -d "$INSTALL_DIR" && ! -f "$INSTALL_DIR/$MARKER" && "$INSTALL_DIR" != "$UNIT_INSTALL_DIR" \
      && -n "$(ls -A "$INSTALL_DIR")" ]]; then
  echo "$INSTALL_DIR is not empty and is not a BerMap server directory ($MARKER is missing)." >&2
  echo "Choose a new or empty directory with --install-dir, for example $INSTALL_DIR/bermap-server." >&2
  exit 1
fi

CONFIG_FILE="$CONFIG_DIR/appsettings.Production.json"

# Database password: asked on first install and when --db-user is given;
# BERMAP_DB_PASSWORD always wins, and on its own rotates the password.
DB_PASSWORD="${BERMAP_DB_PASSWORD:-}"
NEED_PASSWORD=false
if [[ ! -f "$CONFIG_FILE" ]]; then
  [[ -n "$DB_HOST" ]] || read -r -p "PostgreSQL host: " DB_HOST
  [[ -n "$DB_USER" ]] || read -r -p "PostgreSQL user: " DB_USER
  NEED_PASSWORD=true
elif [[ -n "$DB_USER" ]]; then
  NEED_PASSWORD=true
fi
if $NEED_PASSWORD && [[ -z "$DB_PASSWORD" ]]; then
  read -r -s -p "Password for PostgreSQL user '$DB_USER': " DB_PASSWORD
  echo
fi

if ! id bermap >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin bermap
fi

# Configuration: the existing file is merged, never rebuilt. Only the values given
# above are written; the vault directory only on first install or with --data-dir.
# Prints the local base URL and the collector enrollment token.
mkdir -p "$CONFIG_DIR"
# The file holds the database password: written without world access.
SETTINGS_FILE="$(mktemp)"
trap 'rm -f "$SETTINGS_FILE"' EXIT
(umask 027
  DB_HOST="$DB_HOST" DB_PORT="$DB_PORT" DB_NAME="$DB_NAME" DB_USER="$DB_USER" DB_PASSWORD="$DB_PASSWORD" \
  PORT="$PORT" DATA_DIR="$DATA_DIR" DATA_DIR_SET="$DATA_DIR_SET" python3 - "$CONFIG_FILE" <<'PY' > "$SETTINGS_FILE"
import json, os, re, secrets, sys

path = sys.argv[1]
env = os.environ
config = {}
if os.path.exists(path):
    with open(path, encoding="utf-8-sig") as handle:
        config = json.load(handle)
created = not config

def key_of(parent, name):
    # .NET configuration keys are case-insensitive.
    return next((key for key in parent if key.lower() == name.lower()), name)

def section(parent, name):
    key = key_of(parent, name)
    if not isinstance(parent.get(key), dict):
        parent[key] = {}
    return parent[key]

def parse(connection):
    # Npgsql/ADO.NET connection string: key=value pairs, values optionally quoted
    # with " or ' (the quote doubled inside).
    pairs, i, n = [], 0, len(connection)
    while i < n:
        while i < n and connection[i] in "; ":
            i += 1
        equals = connection.find("=", i)
        if equals < 0:
            break
        key, i = connection[i:equals].strip(), equals + 1
        while i < n and connection[i] == " ":
            i += 1
        if i < n and connection[i] in "\"'":
            quote, i, chars = connection[i], i + 1, []
            while i < n:
                if connection[i] == quote:
                    if connection[i + 1:i + 2] == quote:
                        chars.append(quote)
                        i += 2
                        continue
                    i += 1
                    break
                chars.append(connection[i])
                i += 1
            value = "".join(chars)
            end = connection.find(";", i)
        else:
            end = connection.find(";", i)
            value = (connection[i:] if end < 0 else connection[i:end]).strip()
        i = n if end < 0 else end + 1
        if key:
            pairs.append([key, value])
    return pairs

def quote(value):
    return '"' + value.replace('"', '""') + '"' if any(c in value for c in ';="\' ') else value

ALIASES = {
    "Host": {"host", "server"},
    "Port": {"port"},
    "Database": {"database", "db"},
    "Username": {"username", "user name", "user id", "userid", "user"},
    "Password": {"password", "psw", "pwd"},
}

def assign(pairs, name, value):
    positions = [i for i, (key, _) in enumerate(pairs) if key.lower() in ALIASES[name]]
    for i in reversed(positions[1:]):
        del pairs[i]
    if positions:
        pairs[positions[0]] = [name, value]
    else:
        pairs.append([name, value])

connections = section(config, "ConnectionStrings")
connection_key = key_of(connections, "DefaultConnection")
pairs = parse(connections.get(connection_key) or "")
if not pairs:
    pairs = [["Host", ""], ["Port", "5432"], ["Database", "bermap"], ["Username", ""]]
for name, variable in (("Host", "DB_HOST"), ("Port", "DB_PORT"), ("Database", "DB_NAME"),
                       ("Username", "DB_USER"), ("Password", "DB_PASSWORD")):
    if env.get(variable):
        assign(pairs, name, env[variable])
connections[connection_key] = ";".join(f"{key}={quote(value)}" for key, value in pairs)

bermap = section(config, "BerMap")
vault = section(bermap, "CredentialVault")
vault_key = key_of(vault, "Directory")
if env["DATA_DIR_SET"] == "true" or not vault.get(vault_key):
    vault[vault_key] = os.path.join(env["DATA_DIR"], "vault")
collectors = section(bermap, "Collectors")
token_key = key_of(collectors, "EnrollmentToken")
if not collectors.get(token_key):
    collectors[token_key] = secrets.token_hex(24)

endpoints = section(section(config, "Kestrel"), "Endpoints")
default_key = key_of(endpoints, "Default")
def first_url():
    return next((e.get(key_of(e, "Url")) for e in endpoints.values()
                 if isinstance(e, dict) and e.get(key_of(e, "Url"))), "")

port = env.get("PORT")
# A hand-written endpoint layout without "Default" is left alone unless --port is given.
if port or not first_url():
    endpoint = section(endpoints, default_key)
    url_key = key_of(endpoint, "Url")
    url = endpoint.get(url_key) or ""
    if port and re.search(r":\d+/?$", url):
        url = re.sub(r":\d+(/?)$", rf":{port}\1", url)
    elif port or not url:
        url = f"http://0.0.0.0:{port or 8080}"
    endpoint[url_key] = url
url = first_url()

temporary = path + ".tmp"
with open(temporary, "w", encoding="utf-8") as handle:
    json.dump(config, handle, indent=2, ensure_ascii=False)
    handle.write("\n")
os.replace(temporary, path)

base = re.sub(r"^(\w+)://(0\.0\.0\.0|\*|\+|\[::\])(?=[:/]|$)", r"\1://localhost", url.rstrip("/"))
print(base)
print(collectors[token_key])
PY
)
chown root:bermap "$CONFIG_FILE"
chmod 640 "$CONFIG_FILE"
{ read -r BASE_URL; read -r TOKEN; } < "$SETTINGS_FILE"

if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  systemctl stop "$SERVICE"
fi

echo "==> Copying server files to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
touch "$INSTALL_DIR/$MARKER"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name "$MARKER" -exec rm -rf {} +
cp -a "$PACKAGE_DIR/server/." "$INSTALL_DIR/"
chown -R root:root "$INSTALL_DIR"

mkdir -p "$DATA_DIR/vault"
# The collector keeps its data in $DATA_DIR/collector under its own user (KURULUM-COLLECTOR.md).
find "$DATA_DIR" -path "$DATA_DIR/collector" -prune -o -exec chown bermap:bermap {} +
chmod 700 "$DATA_DIR/vault"

# The unit points the server at $CONFIG_FILE (BERMAP_CONFIG_FILE).
sed -e "s#@INSTALL_DIR@#$INSTALL_DIR#g" -e "s#@DATA_DIR@#$DATA_DIR#g" \
  "$PACKAGE_DIR/bermap-server.service" > "$UNIT_FILE"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl start "$SERVICE"

echo "==> Waiting for $BASE_URL/healthz/ready"
for _ in $(seq 1 90); do
  # -k: a certificate added by hand is issued for the server name, not localhost.
  if curl -fsSk "$BASE_URL/healthz/ready" >/dev/null 2>&1; then
    echo
    echo "BerMap is running: ${BASE_URL/localhost/$(hostname -f 2>/dev/null || hostname)}"
    if [[ -f "$DATA_DIR/vault/initial-admin-password.txt" ]]; then
      echo "Initial administrator (delete the file after the first sign-in):"
      sed 's/^/  /' "$DATA_DIR/vault/initial-admin-password.txt"
      echo "  file: $DATA_DIR/vault/initial-admin-password.txt"
    fi
    echo "Collector enrollment token: $TOKEN"
    if [[ "$BASE_URL" == http://* ]]; then
      echo "The console is served over plain HTTP; publish it through a TLS reverse proxy (nginx, HAProxy)."
    fi
    exit 0
  fi
  sleep 2
done

echo "BerMap did not become ready. See: journalctl -u $SERVICE -n 100" >&2
exit 1
