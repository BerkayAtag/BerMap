#!/usr/bin/env bash
# Exercises the configuration merge of deploy/collector/linux/install.sh without root,
# systemd or a package: the embedded Python program is run against temporary files.
#
#   deploy/tests/collector-config.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# The program between "<<'PY'" and "PY" in the installer, as the installer runs it.
sed -n "/<<'PY'/,/^PY\$/p" "$ROOT/deploy/collector/linux/install.sh" | sed '1d;$d' > "$WORK/merge.py"
grep -q 'reenroll' "$WORK/merge.py" || { echo "Merge program not found in install.sh" >&2; exit 1; }

CONFIG="$WORK/collector.json"
failures=0

# merge [VAR=value ...]: runs the merge for $CONFIG, moves the result into place and
# leaves the printed metadata in $WORK/meta (url, data dir, masked token, reenroll).
merge() {
  env SERVER_URL= TOKEN= NAME= DATA_DIR=/var/lib/bermap/collector DATA_DIR_SET=false "$@" \
    python3 "$WORK/merge.py" "$CONFIG" "$WORK/staged.json" > "$WORK/meta" 2> "$WORK/err"
  mv "$WORK/staged.json" "$CONFIG"
}

check() {
  local description="$1"; shift
  if python3 - "$CONFIG" "$WORK/meta" "$@" <<'PY'; then echo "ok   $description"; else echo "FAIL $description" >&2; failures=$((failures + 1)); fi
import json, sys
config = json.load(open(sys.argv[1]))
meta = open(sys.argv[2]).read().splitlines()
bermap, collector = config["BerMap"], config["BerMap"]["Collector"]
assert eval(sys.argv[3]), (config, meta)
PY
}

expect_failure() {
  local description="$1" pattern="$2"; shift 2
  if env SERVER_URL= TOKEN= NAME= DATA_DIR=/var/lib/bermap/collector DATA_DIR_SET=false "$@" \
      python3 "$WORK/merge.py" "$CONFIG" "$WORK/staged.json" > /dev/null 2> "$WORK/err"; then
    echo "FAIL $description (accepted)" >&2; failures=$((failures + 1))
  elif grep -q "$pattern" "$WORK/err"; then
    echo "ok   $description"
  else
    echo "FAIL $description: $(cat "$WORK/err")" >&2; failures=$((failures + 1))
  fi
}

TOKEN_VALUE=0123456789abcdef0123456789abcdef0123456789abcdef

# First install: missing values are refused, given values are written.
expect_failure "first install needs a server URL" "pass --server-url" TOKEN="$TOKEN_VALUE"
expect_failure "first install needs a token" "EnrollmentToken is not configured" SERVER_URL=https://bermap.corp.local
expect_failure "server URL must be http(s)" "Invalid server URL" SERVER_URL=bermap.corp.local TOKEN="$TOKEN_VALUE"
expect_failure "token without whitespace" "whitespace" SERVER_URL=https://bermap.corp.local TOKEN="abc def"
merge SERVER_URL=https://bermap.corp.local/ TOKEN="$TOKEN_VALUE" NAME=dc2-collector
check "first install writes the given values" \
  'bermap["ServerUrl"] == "https://bermap.corp.local" and collector["EnrollmentToken"] == "'"$TOKEN_VALUE"'" and collector["Name"] == "dc2-collector" and collector["DataDirectory"] == "/var/lib/bermap/collector" and collector["Integrations"] == []'
check "first install prints a masked token and does not re-enroll" \
  'meta == ["https://bermap.corp.local", "/var/lib/bermap/collector", "0123... (48 characters)", "false"]'

# Settings and integrations added by hand survive an upgrade without parameters.
python3 - "$CONFIG" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
config["Logging"] = {"LogLevel": {"Default": "Warning"}}
collector = config["BerMap"]["Collector"]
collector["BatchSize"] = 250
collector["Integrations"] = [{
    "IntegrationId": "3f0c6a8e-6f2b-4d1e-9a51-0d7c2b9e4a10", "AdapterType": "VMware",
    "Configuration": {"server": "vcenter.corp.local", "username": "ro"},
    "SecretFile": "/etc/bermap/collector-secrets/vcenter"}]
json.dump(config, open(sys.argv[1], "w"), indent=2)
PY
merge
check "plain upgrade keeps hand-written settings and integrations" \
  'config["Logging"]["LogLevel"]["Default"] == "Warning" and collector["BatchSize"] == 250 and collector["Integrations"][0]["Configuration"]["server"] == "vcenter.corp.local" and collector["Integrations"][0]["SecretFile"].endswith("vcenter")'
check "plain upgrade keeps URL, token and name and does not re-enroll" \
  'bermap["ServerUrl"] == "https://bermap.corp.local" and collector["EnrollmentToken"] == "'"$TOKEN_VALUE"'" and collector["Name"] == "dc2-collector" and meta[3] == "false"'
merge SERVER_URL=https://bermap.corp.local TOKEN="$TOKEN_VALUE" NAME=dc2-collector
check "the same values again do not re-enroll" 'meta[3] == "false"'

# A changed URL, token or name re-enrolls; only that value changes.
merge SERVER_URL=https://bermap2.corp.local:8443
check "new server URL re-enrolls" 'bermap["ServerUrl"] == "https://bermap2.corp.local:8443" and collector["Name"] == "dc2-collector" and meta[3] == "true"'
merge TOKEN=fedcba9876543210
check "new token re-enrolls and is masked" 'collector["EnrollmentToken"] == "fedcba9876543210" and meta[2] == "fedc... (16 characters)" and meta[3] == "true"'
merge NAME=dc2-collector-b
check "new name re-enrolls" 'collector["Name"] == "dc2-collector-b" and meta[3] == "true" and len(collector["Integrations"]) == 1'

# Data directory: kept on upgrade, replaced with --data-dir, printed for the installer.
merge DATA_DIR=/srv/other
check "data directory is kept without --data-dir" 'collector["DataDirectory"] == "/var/lib/bermap/collector" and meta[1] == "/var/lib/bermap/collector"'
merge DATA_DIR=/srv/collector DATA_DIR_SET=true
check "--data-dir replaces the data directory" 'collector["DataDirectory"] == "/srv/collector" and meta[1] == "/srv/collector"'

# .NET configuration keys are case-insensitive: hand-written casing is reused, not duplicated.
cat > "$CONFIG" <<'JSON'
{ "bermap": { "serverurl": "https://old.corp.local", "collector": { "enrollmenttoken": "abcd1234", "integrations": [] } } }
JSON
merge SERVER_URL=https://new.corp.local
python3 - "$CONFIG" <<'PY' && echo "ok   lower-case keys are updated in place, not duplicated" || { echo "FAIL lower-case keys" >&2; failures=$((failures + 1)); }
import json, sys
config = json.load(open(sys.argv[1]))
assert list(config) == ["bermap"], config
bermap = config["bermap"]
assert sorted(bermap) == ["collector", "serverurl"] and bermap["serverurl"] == "https://new.corp.local", config
assert bermap["collector"]["enrollmenttoken"] == "abcd1234", config
assert sorted(bermap["collector"]) == ["DataDirectory", "enrollmenttoken", "integrations"], config
PY

# A broken file is reported, not overwritten.
echo '[1, 2]' > "$CONFIG"
expect_failure "a non-object configuration is refused" "not a JSON object" SERVER_URL=https://bermap.corp.local TOKEN="$TOKEN_VALUE"

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi
echo "All collector configuration checks passed."
