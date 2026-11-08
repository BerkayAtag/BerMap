#!/usr/bin/env bash
# Exercises the directory rules of the Linux server and collector installers without root,
# systemd or a real package: the scripts run against a sandbox in which /etc, /opt and
# /var/lib are redirected and systemctl, useradd, chown, id and curl are stubs.
#
#   deploy/tests/linux-install-dirs.sh
#
# Checked: only a directory the installer creates, one it installed into before, or the
# empty default directory is used (never an empty /opt); the marker of a pre-existing
# directory is written only after a successful install; uninstall removes only what install
# created; the collector never takes the server's data directory for its own; a server URL
# with a path prefix is kept.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT
FS="$SANDBOX/fs"
BIN="$SANDBOX/bin"
mkdir -p "$FS/etc/systemd/system" "$FS/opt" "$FS/var/lib" "$BIN"
failures=0

# --- Stubs -------------------------------------------------------------------------------
cat > "$BIN/systemctl" <<EOF
#!/usr/bin/env bash
# start: the collector "registers" (writes its id) unless BERMAP_TEST_NO_REGISTER is set.
service="\${@: -1}"
case "\$1" in
  start)
    touch "$SANDBOX/active-\$service"
    unit="$FS/etc/systemd/system/\$service.service"
    if [[ "\$service" == bermap-collector && -z "\${BERMAP_TEST_NO_REGISTER:-}" ]]; then
      data="\$(sed -n 's/^ReadWritePaths=//p' "\$unit" | head -n 1)"
      echo 0f8fad5b-d9cb-469f-a165-70867728950e > "\$data/collector-id"
    fi ;;
  is-active) [[ -f "$SANDBOX/active-\$service" ]] ;;
  stop) rm -f "$SANDBOX/active-\$service" ;;
  disable) rm -f "$SANDBOX/active-\$service" ;;
esac
EOF
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/id"
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/useradd"
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/userdel"
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/chown"
printf '#!/usr/bin/env bash\nexit 0\n' > "$BIN/sleep"
# The server's readiness probe fails while BERMAP_TEST_NOT_READY is set.
printf '#!/usr/bin/env bash\n[[ -z "${BERMAP_TEST_NOT_READY:-}" ]]\n' > "$BIN/curl"
chmod +x "$BIN"/*

# --- Packages: the scripts with the fixed system paths redirected into the sandbox ------------
# package NAME SOURCE_DIR BINARY UNIT SCRIPTS...
package() {
  local name="$1" dir="$SANDBOX/pkg-$1" source="$2" binary="$3" unit="$4"; shift 4
  mkdir -p "$dir/$name"
  printf '#!/bin/sh\n' > "$dir/$name/$binary"
  chmod +x "$dir/$name/$binary"
  cp "$source/$unit" "$dir/"
  for script in "$@"; do
    sed -e "s#/etc/#$FS/etc/#g" -e "s#/opt/#$FS/opt/#g" -e "s#/var/lib/#$FS/var/lib/#g" \
        -e 's/\[\[ \$EUID -ne 0 \]\]/[[ 0 -ne 0 ]]/' "$source/$script" > "$dir/$script"
    chmod +x "$dir/$script"
  done
}
package server "$ROOT/deploy/linux" BerMap.Api bermap-server.service install.sh uninstall.sh
package collector "$ROOT/deploy/collector/linux" BerMap.Collector bermap-collector.service install.sh uninstall.sh

run() { # run LOG COMMAND...: runs a script with the stubs first on PATH
  local log="$1"; shift
  env PATH="$BIN:$PATH" BERMAP_DB_PASSWORD=secret BERMAP_ENROLLMENT_TOKEN=0123456789abcdef \
    BERMAP_REGISTER_TIMEOUT=4 BERMAP_READY_TIMEOUT=4 "$@" > "$SANDBOX/$log" 2>&1
}
server_install() { run server.log "$SANDBOX/pkg-server/install.sh" --db-host db --db-user bermap "$@"; }
server_uninstall() { run server-uninstall.log "$SANDBOX/pkg-server/uninstall.sh" "$@"; }
collector_install() { run collector.log "$SANDBOX/pkg-collector/install.sh" --server-url https://bermap.corp.local "$@"; }
collector_uninstall() { run collector-uninstall.log "$SANDBOX/pkg-collector/uninstall.sh" "$@"; }

ok() { echo "ok   $1"; }
fail() { echo "FAIL $1" >&2; [[ -z "${2:-}" ]] || sed 's/^/     /' "$SANDBOX/$2" >&2; failures=$((failures + 1)); }
check() { # check DESCRIPTION LOG CONDITION...
  local description="$1" log="$2"; shift 2
  if "$@"; then ok "$description"; else fail "$description" "$log"; fi
}
marker_is() { [[ -f "$1" && "$(head -n 1 "$1")" == "$2" ]]; }
reset() { rm -rf "$FS"; mkdir -p "$FS/etc/systemd/system" "$FS/opt" "$FS/var/lib"; rm -f "$SANDBOX"/active-*; }

SERVER_DIR="$FS/opt/bermap/server"
COLLECTOR_DIR="$FS/opt/bermap/collector"
COLLECTOR_DATA="$FS/var/lib/bermap/collector"

# --- Server ------------------------------------------------------------------------------
reset
if server_install; then ok "server: fresh install"; else fail "server: fresh install" server.log; fi
check "server: a directory the installer creates is marked 'created'" server.log marker_is "$SERVER_DIR/.bermap-server" created
check "server: the data directory carries the server marker" server.log test -f "$FS/var/lib/bermap/.bermap-server-data"
if server_install; then ok "server: upgrade in place"; else fail "server: upgrade in place" server.log; fi
check "server: upgrade keeps the marker" server.log marker_is "$SERVER_DIR/.bermap-server" created
server_uninstall
check "server: uninstall removes the directory it created" server-uninstall.log test ! -e "$SERVER_DIR"

reset
mkdir -p "$FS/opt/empty"
if server_install --install-dir "$FS/opt/empty"; then fail "server: an empty non-default --install-dir is refused" server.log
else ok "server: an empty non-default --install-dir is refused"; fi
check "server: nothing is written into the refused directory" server.log test -z "$(ls -A "$FS/opt/empty")"
mkdir -p "$FS/opt/vendor/x"
if server_install --install-dir "$FS/opt/vendor"; then fail "server: a non-empty foreign --install-dir is refused" server.log
else ok "server: a non-empty foreign --install-dir is refused"; fi
check "server: the foreign directory is untouched" server.log test -d "$FS/opt/vendor/x"

reset
mkdir -p "$SERVER_DIR"
if BERMAP_TEST_NOT_READY=1 server_install; then fail "server: a failed start fails the install" server.log
else ok "server: a failed start fails the install"; fi
check "server: a pre-existing default directory is not marked after a failed install" server.log test ! -e "$SERVER_DIR/.bermap-server"
if server_install; then ok "server: the install can be repeated after a failure" ; else fail "server: the install can be repeated after a failure" server.log; fi
check "server: the pre-existing default directory is marked 'existing' after success" server.log marker_is "$SERVER_DIR/.bermap-server" existing
server_uninstall
check "server: uninstall empties a pre-existing directory and keeps it" server-uninstall.log \
  test -d "$SERVER_DIR" -a -z "$(ls -A "$SERVER_DIR" 2>/dev/null)"

# --- Collector ---------------------------------------------------------------------------
reset
if collector_install --server-url https://proxy.corp.local/bermap/; then ok "collector: fresh install"; else fail "collector: fresh install" collector.log; fi
check "collector: a server URL path prefix is kept" collector.log \
  python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["BerMap"]["ServerUrl"] == "https://proxy.corp.local/bermap"' "$FS/etc/bermap/collector.json"
check "collector: the install directory it creates is marked 'created'" collector.log marker_is "$COLLECTOR_DIR/.bermap-collector" created
check "collector: the data directory it creates is marked 'created'" collector.log marker_is "$COLLECTOR_DATA/.bermap-collector-data" created
collector_uninstall --remove-data
check "collector: uninstall --remove-data removes what install created" collector-uninstall.log \
  test ! -e "$COLLECTOR_DIR" -a ! -e "$COLLECTOR_DATA" -a ! -e "$FS/etc/bermap/collector.json"

reset
mkdir -p "$FS/opt/empty"
if collector_install --install-dir "$FS/opt/empty"; then fail "collector: an empty non-default --install-dir is refused" collector.log
else ok "collector: an empty non-default --install-dir is refused"; fi
mkdir -p "$FS/srv"
if collector_install --data-dir "$FS/srv"; then fail "collector: an empty non-default --data-dir is refused" collector.log
else ok "collector: an empty non-default --data-dir is refused"; fi
check "collector: nothing is written into the refused directories" collector.log test -z "$(ls -A "$FS/opt/empty")$(ls -A "$FS/srv")"

# The server's data directory is never the collector's, even when it is passed explicitly.
reset
server_install
if collector_install --data-dir "$FS/var/lib/bermap"; then fail "collector: the server's data directory is refused" collector.log
else ok "collector: the server's data directory is refused"; fi
if collector_install --data-dir "$FS/var/lib"; then fail "collector: a directory containing the server's data directory is refused" collector.log
else ok "collector: a directory containing the server's data directory is refused"; fi
check "collector: the refusal names the server's data directory" collector.log grep -q "server's data directory" "$SANDBOX/collector.log"
if collector_install; then ok "collector: the default data directory below the server's is accepted"; else fail "collector: the default data directory below the server's is accepted" collector.log; fi
# A unit pointing the collector at the server's directory (written by hand) is not followed by uninstall.
sed -i.bak "s#^ReadWritePaths=.*#ReadWritePaths=$FS/var/lib/bermap#" "$FS/etc/systemd/system/bermap-collector.service"
collector_uninstall --remove-data
check "collector: uninstall --remove-data leaves the server's data directory alone" collector-uninstall.log \
  test -f "$FS/var/lib/bermap/.bermap-server-data" -a -d "$FS/var/lib/bermap/vault"

# A pre-existing empty default data directory: marked after success, emptied (not removed) on uninstall.
reset
mkdir -p "$COLLECTOR_DATA" "$COLLECTOR_DIR"
if BERMAP_TEST_NO_REGISTER=1 collector_install; then fail "collector: a failed registration fails the install" collector.log
else ok "collector: a failed registration fails the install"; fi
check "collector: pre-existing default directories are not marked after a failed install" collector.log \
  test ! -e "$COLLECTOR_DIR/.bermap-collector" -a ! -e "$COLLECTOR_DATA/.bermap-collector-data"
if collector_install; then ok "collector: the install can be repeated after a failure"; else fail "collector: the install can be repeated after a failure" collector.log; fi
check "collector: pre-existing default directories are marked 'existing' after success" collector.log \
  marker_is "$COLLECTOR_DATA/.bermap-collector-data" existing
check "collector: ... the install directory too" collector.log marker_is "$COLLECTOR_DIR/.bermap-collector" existing
collector_uninstall --remove-data
check "collector: uninstall empties pre-existing directories and keeps them" collector-uninstall.log \
  test -d "$COLLECTOR_DATA" -a -d "$COLLECTOR_DIR" -a -z "$(ls -A "$COLLECTOR_DATA")$(ls -A "$COLLECTOR_DIR")"

# A data directory without any collector marker is left in place by uninstall --remove-data.
reset
collector_install
rm -f "$COLLECTOR_DATA/.bermap-collector-data" "$COLLECTOR_DATA/collector-id"
touch "$COLLECTOR_DATA/unrelated"
collector_uninstall --remove-data
check "collector: uninstall --remove-data leaves an unmarked directory in place" collector-uninstall.log test -f "$COLLECTOR_DATA/unrelated"

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi
echo "All Linux installer directory checks passed."
