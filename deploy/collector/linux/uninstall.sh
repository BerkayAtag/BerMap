#!/usr/bin/env bash
# Removes the BerMap collector service and program files. /etc/bermap/collector.json
# (configuration, integrations) and the data directory (collector id, offline queue)
# are kept unless --remove-data is given, so a reinstall continues under the same
# collector id. The collector entry on the server is removed from the console
# (Collectorlar page).
set -euo pipefail

INSTALL_DIR=/opt/bermap/collector
DATA_DIR=/var/lib/bermap/collector
CONFIG_FILE=/etc/bermap/collector.json
SERVICE=bermap-collector
UNIT_FILE="/etc/systemd/system/$SERVICE.service"
MARKER=.bermap-collector
DATA_MARKER=.bermap-collector-data
REMOVE_DATA=false
[[ "${1:-}" == "--remove-data" ]] && REMOVE_DATA=true

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

# The unit records the directories install.sh used (--install-dir, --data-dir).
UNIT_INSTALL_DIR=""
if [[ -f "$UNIT_FILE" ]]; then
  UNIT_INSTALL_DIR="$(sed -n 's/^WorkingDirectory=//p' "$UNIT_FILE" | head -n 1)"
  [[ -z "$UNIT_INSTALL_DIR" ]] || INSTALL_DIR="$UNIT_INSTALL_DIR"
  current="$(sed -n 's/^ReadWritePaths=//p' "$UNIT_FILE" | head -n 1)"
  [[ -z "$current" ]] || DATA_DIR="$current"
fi

systemctl disable --now "$SERVICE" 2>/dev/null || true
rm -f "$UNIT_FILE"
systemctl daemon-reload

# remove_dir DIR MARKER: removes what install.sh created. A directory it created (marker
# "created", or an older empty marker) goes entirely; an empty directory it found in place
# (marker "existing") is emptied and kept.
remove_dir() {
  if [[ -f "$1/$2" && "$(head -n 1 "$1/$2")" == existing ]]; then
    find "$1" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    echo "Emptied $1 (it existed before the installation)."
  else
    rm -rf "$1"
  fi
}

if [[ -f "$INSTALL_DIR/$MARKER" ]]; then
  remove_dir "$INSTALL_DIR" "$MARKER"
elif [[ -d "$INSTALL_DIR" && "$INSTALL_DIR" == "$UNIT_INSTALL_DIR" ]]; then
  # The unit ran from here, but the installation never completed: remove the program
  # files only.
  find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
elif [[ -d "$INSTALL_DIR" ]]; then
  echo "Left $INSTALL_DIR in place: it is not a BerMap collector directory ($MARKER is missing)." >&2
fi

if $REMOVE_DATA; then
  # Only a collector data directory is removed, never the server's (credential vault).
  if [[ ! -d "$DATA_DIR" ]]; then
    :
  elif [[ -e "$DATA_DIR/vault" || -e "$DATA_DIR/.bermap-server-data" ]]; then
    echo "Left $DATA_DIR in place: it is the BerMap server's data directory." >&2
  elif [[ -f "$DATA_DIR/$DATA_MARKER" || -e "$DATA_DIR/collector-id" || -e "$DATA_DIR/offline_queue" ]]; then
    remove_dir "$DATA_DIR" "$DATA_MARKER"
  else
    echo "Left $DATA_DIR in place: it is not a BerMap collector data directory ($DATA_MARKER is missing)." >&2
  fi
  rm -f "$CONFIG_FILE"
  userdel bermap-collector 2>/dev/null || true
  echo "Removed the collector data in $DATA_DIR and $CONFIG_FILE. Secret files referenced by the configuration were not touched."
else
  echo "Kept $DATA_DIR (collector id, offline queue) and $CONFIG_FILE (configuration)."
fi
echo "BerMap collector removed. Remove its entry on the Collectorlar page of the console if it is retired."
