#!/usr/bin/env bash
# Removes the BerMap server service and program files. The PostgreSQL database,
# /etc/bermap (configuration) and the data directory (credential vault) are kept
# unless --remove-data is given.
set -euo pipefail

INSTALL_DIR=/opt/bermap/server
DATA_DIR=/var/lib/bermap
CONFIG_DIR=/etc/bermap
SERVICE=bermap-server
UNIT_FILE="/etc/systemd/system/$SERVICE.service"
MARKER=.bermap-server
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

if [[ -f "$INSTALL_DIR/$MARKER" || "$INSTALL_DIR" == "$UNIT_INSTALL_DIR" ]]; then
  rm -rf "$INSTALL_DIR"
elif [[ -d "$INSTALL_DIR" ]]; then
  echo "Left $INSTALL_DIR in place: it is not a BerMap server directory ($MARKER is missing)." >&2
fi

if $REMOVE_DATA; then
  # A collector on the same host keeps its data ($DATA_DIR/collector) and configuration
  # ($CONFIG_DIR/collector*); its own uninstall.sh removes them.
  for dir in "$DATA_DIR" "$CONFIG_DIR"; do
    [[ -d "$dir" ]] || continue
    find "$dir" -mindepth 1 -maxdepth 1 ! -name 'collector*' -exec rm -rf {} +
    rmdir "$dir" 2>/dev/null || echo "Kept the BerMap collector files in $dir."
  done
  echo "Removed the server data in $DATA_DIR and $CONFIG_DIR. The PostgreSQL database was not touched."
else
  echo "Kept $DATA_DIR (credential vault), $CONFIG_DIR (configuration) and the PostgreSQL database."
fi
echo "BerMap server removed."
