#!/usr/bin/env bash
# Removes the BerMap server service and program files. The PostgreSQL database
# and /var/lib/bermap (credential vault) are kept unless --remove-data is given.
set -euo pipefail

INSTALL_DIR=/opt/bermap/server
DATA_DIR=/var/lib/bermap
SERVICE=bermap-server
REMOVE_DATA=false
[[ "${1:-}" == "--remove-data" ]] && REMOVE_DATA=true

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

systemctl disable --now "$SERVICE" 2>/dev/null || true
rm -f "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
rm -rf "$INSTALL_DIR"

if $REMOVE_DATA; then
  rm -rf "$DATA_DIR" /etc/bermap
  echo "Removed $DATA_DIR and /etc/bermap. The PostgreSQL database was not touched."
else
  echo "Kept $DATA_DIR (credential vault), /etc/bermap and the PostgreSQL database."
fi
echo "BerMap server removed."
