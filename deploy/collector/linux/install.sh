#!/usr/bin/env bash
# Installs or upgrades the BerMap collector as a systemd service.
#
#   sudo BERMAP_ENROLLMENT_TOKEN=... ./install.sh --server-url https://bermap.corp.local --name dc2-collector
#   sudo ./install.sh --server-url https://bermap.corp.local      # prompts for the enrollment token
#
# The collector connects outbound to the BerMap server only. The enrollment token
# is printed by the server installer (BerMap:Collectors:EnrollmentToken); prefer
# BERMAP_ENROLLMENT_TOKEN over --enrollment-token, which other users can see in ps.
# Running the script again upgrades in place and keeps /etc/bermap/collector.json:
# only the values given change, integrations and settings added by hand are kept.
set -euo pipefail

INSTALL_DIR=/opt/bermap/collector
DATA_DIR=/var/lib/bermap/collector
CONFIG_DIR=/etc/bermap
CONFIG_FILE="$CONFIG_DIR/collector.json"
SERVICE=bermap-collector
SERVICE_USER=bermap-collector
UNIT_FILE="/etc/systemd/system/$SERVICE.service"
# Written into the install directory; only a directory carrying it is ever cleared.
MARKER=.bermap-collector
# Seconds to wait for the collector to register with the server.
REGISTER_TIMEOUT="${BERMAP_REGISTER_TIMEOUT:-120}"
# Empty means "not given": an upgrade keeps the configured value.
SERVER_URL=""
TOKEN="${BERMAP_ENROLLMENT_TOKEN:-}"
NAME=""
INSTALL_DIR_SET=false
DATA_DIR_SET=false

usage() {
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
  echo "Options: --server-url --enrollment-token --name --install-dir --data-dir"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-url) SERVER_URL="$2"; shift 2 ;;
    --enrollment-token) TOKEN="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
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
if [[ ! -x "$PACKAGE_DIR/collector/BerMap.Collector" ]]; then
  echo "collector/BerMap.Collector was not found next to this script. Run it from the extracted release package." >&2
  exit 1
fi
command -v python3 >/dev/null || { echo "python3 is required." >&2; exit 1; }

# An upgrade without --install-dir/--data-dir keeps the directories the unit uses.
UNIT_INSTALL_DIR=""
UNIT_DATA_DIR=""
if [[ -f "$UNIT_FILE" ]]; then
  UNIT_INSTALL_DIR="$(sed -n 's/^WorkingDirectory=//p' "$UNIT_FILE" | head -n 1)"
  UNIT_DATA_DIR="$(sed -n 's/^ReadWritePaths=//p' "$UNIT_FILE" | head -n 1)"
  if ! $INSTALL_DIR_SET && [[ -n "$UNIT_INSTALL_DIR" ]]; then
    INSTALL_DIR="$UNIT_INSTALL_DIR"
  fi
  if ! $DATA_DIR_SET && [[ -n "$UNIT_DATA_DIR" ]]; then
    DATA_DIR="$UNIT_DATA_DIR"
  fi
fi

# First install: ask for what is missing (an upgrade keeps the configured values).
if [[ ! -f "$CONFIG_FILE" ]]; then
  [[ -n "$SERVER_URL" ]] || read -r -p "BerMap server URL (e.g. https://bermap.corp.local): " SERVER_URL
  if [[ -z "$TOKEN" ]]; then
    read -r -s -p "Collector enrollment token: " TOKEN
    echo
  fi
fi

# Configuration: the existing file is merged, never rebuilt. Only the values given
# above are written; integrations and settings added by hand are kept. The merged
# file is staged next to the real one and moved into place after every check passed.
# Prints the effective server URL, data directory, masked token and whether the
# collector has to enroll again (server URL, token or name changed).
mkdir -p "$CONFIG_DIR"
STAGED_CONFIG="$(mktemp "$CONFIG_DIR/.collector.json.XXXXXX")"
METADATA="$(mktemp)"
trap 'rm -f "$STAGED_CONFIG" "$METADATA"' EXIT
SERVER_URL="$SERVER_URL" TOKEN="$TOKEN" NAME="$NAME" DATA_DIR="$DATA_DIR" DATA_DIR_SET="$DATA_DIR_SET" \
  python3 - "$CONFIG_FILE" "$STAGED_CONFIG" <<'PY' > "$METADATA"
import json, os, re, sys

path, staged = sys.argv[1], sys.argv[2]
env = os.environ
config = {}
if os.path.exists(path):
    with open(path, encoding="utf-8-sig") as handle:
        config = json.load(handle)
    if not isinstance(config, dict):
        sys.exit(f"{path} is not a JSON object.")
created = not config

def fail(message):
    print(message, file=sys.stderr)
    sys.exit(2)

def key_of(parent, name):
    # .NET configuration keys are case-insensitive.
    return next((key for key in parent if key.lower() == name.lower()), name)

def section(parent, name):
    key = key_of(parent, name)
    if not isinstance(parent.get(key), dict):
        parent[key] = {}
    return parent[key]

def normalize_url(value):
    return (value or "").strip().rstrip("/")

bermap = section(config, "BerMap")
collector = section(bermap, "Collector")
url_key = key_of(bermap, "ServerUrl")
token_key = key_of(collector, "EnrollmentToken")
name_key = key_of(collector, "Name")
data_key = key_of(collector, "DataDirectory")
before = (normalize_url(bermap.get(url_key)), (collector.get(token_key) or "").strip(), (collector.get(name_key) or "").strip())

if env.get("SERVER_URL"):
    url = normalize_url(env["SERVER_URL"])
    if not re.fullmatch(r"https?://[^\s/?#]+(/[^\s?#]*)?", url):
        fail(f"Invalid server URL: {env['SERVER_URL']} (expected https://host[:port])")
    bermap[url_key] = url
if env.get("TOKEN"):
    token = env["TOKEN"].strip()
    if not token or re.search(r"\s", token):
        fail("The enrollment token must not contain whitespace.")
    collector[token_key] = token
if env.get("NAME", "").strip():
    collector[name_key] = env["NAME"].strip()
if env["DATA_DIR_SET"] == "true" or not (collector.get(data_key) or "").strip():
    collector[data_key] = env["DATA_DIR"]
if created:
    collector.setdefault(key_of(collector, "Integrations"), [])

url = normalize_url(bermap.get(url_key))
token = (collector.get(token_key) or "").strip()
if not url:
    fail(f"BerMap:ServerUrl is not configured in {path}; pass --server-url.")
if not token:
    fail(f"BerMap:Collector:EnrollmentToken is not configured in {path}; pass BERMAP_ENROLLMENT_TOKEN or --enrollment-token.")
data_dir = collector[data_key]
if not data_dir.startswith("/"):
    fail(f"BerMap:Collector:DataDirectory must be an absolute path: {data_dir}")
after = (url, token, (collector.get(name_key) or "").strip())
# A collector registered under other settings enrolls again (a new name included: the
# server keeps the name given at registration); the first install has nothing to compare.
reenroll = not created and before != after

with open(staged, "w", encoding="utf-8") as handle:
    json.dump(config, handle, indent=2, ensure_ascii=False)
    handle.write("\n")

print(url)
print(data_dir.rstrip("/") or "/")
print(f"{token[:4]}... ({len(token)} characters)")
print("true" if reenroll else "false")
PY
{ read -r SERVER_URL; read -r DATA_DIR; read -r MASKED_TOKEN; read -r REENROLL; } < "$METADATA"

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
  echo "$INSTALL_DIR is not empty and is not a BerMap collector directory ($MARKER is missing)." >&2
  echo "Choose a new or empty directory with --install-dir, for example $INSTALL_DIR/bermap-collector." >&2
  exit 1
fi
# The data directory is handed to the service user: refuse a foreign, non-empty one
# (one holding a collector id or offline queue is a collector data directory).
if [[ -d "$DATA_DIR" && -n "$(ls -A "$DATA_DIR")" && ! -e "$DATA_DIR/collector-id" \
      && ! -e "$DATA_DIR/offline_queue" && "$DATA_DIR" != "$UNIT_DATA_DIR" ]]; then
  echo "$DATA_DIR is not empty and is not a BerMap collector data directory. Choose a new or empty --data-dir." >&2
  exit 1
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# The file holds the enrollment token (and secret paths): root-owned, readable by the service only.
chown root:"$SERVICE_USER" "$STAGED_CONFIG"
chmod 640 "$STAGED_CONFIG"
mv -f "$STAGED_CONFIG" "$CONFIG_FILE"

if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  systemctl stop "$SERVICE"
fi

echo "==> Copying collector files to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
touch "$INSTALL_DIR/$MARKER"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name "$MARKER" -exec rm -rf {} +
cp -a "$PACKAGE_DIR/collector/." "$INSTALL_DIR/"
chown -R root:root "$INSTALL_DIR"

# Collector id and offline queue (discovered inventory awaiting delivery): service user only.
mkdir -p "$DATA_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
chmod 750 "$DATA_DIR"
ID_FILE="$DATA_DIR/collector-id"
if [[ "$REENROLL" == true ]]; then
  echo "==> Server URL, enrollment token or name changed: the collector enrolls again"
  rm -f "$ID_FILE"
fi
REGISTERED_BEFORE=false
[[ -s "$ID_FILE" ]] && REGISTERED_BEFORE=true

# The unit points the collector at $CONFIG_FILE (BERMAP_CONFIG_FILE).
sed -e "s#@INSTALL_DIR@#$INSTALL_DIR#g" -e "s#@DATA_DIR@#$DATA_DIR#g" \
  "$PACKAGE_DIR/bermap-collector.service" > "$UNIT_FILE"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl start "$SERVICE"

report() {
  echo
  echo "BerMap collector is running and registered with $SERVER_URL"
  echo "Configuration: $CONFIG_FILE"
  echo "Collector id:  $(cat "$ID_FILE")"
  echo "Enrollment token: $MASKED_TOKEN"
  if [[ "$SERVER_URL" == http://* ]]; then
    echo "The server is reached over plain HTTP; the enrollment token and inventory travel unencrypted. Use https://."
  fi
  echo "Add integrations to $CONFIG_FILE (see KURULUM-COLLECTOR.md), then: systemctl restart $SERVICE"
}

if $REGISTERED_BEFORE; then
  # Already enrolled under the same settings: the service only has to keep running.
  sleep 5
  if systemctl is-active --quiet "$SERVICE"; then report; exit 0; fi
  echo "The collector service stopped after the upgrade. See: journalctl -u $SERVICE -n 100" >&2
  exit 1
fi

echo "==> Waiting for the collector to register with $SERVER_URL"
for _ in $(seq 1 $((REGISTER_TIMEOUT / 2))); do
  if [[ -s "$ID_FILE" ]]; then report; exit 0; fi
  if ! systemctl is-active --quiet "$SERVICE"; then
    echo "The collector service is not running. See: journalctl -u $SERVICE -n 100" >&2
    exit 1
  fi
  sleep 2
done

echo "The collector did not register with $SERVER_URL within $REGISTER_TIMEOUT seconds." >&2
echo "Check that the server is reachable from this host, the server URL and the enrollment token" >&2
echo "(BerMap:Collectors:EnrollmentToken on the server). See: journalctl -u $SERVICE -n 100" >&2
exit 1
