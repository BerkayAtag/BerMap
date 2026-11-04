#!/usr/bin/env bash
# Builds a self-contained BerMap server package (API + web console) for one runtime.
#
#   deploy/package.sh <version> <rid> <out-dir>      rid: win-x64 | linux-x64 | linux-arm64 | osx-arm64
#
# Produces <out-dir>/BerMap-Server-<version>-win-x64.zip or
# <out-dir>/bermap-server-<version>-<rid>.tar.gz. The web console must already be
# built (src/BerMap.Web/dist), e.g. with: (cd src/BerMap.Web && npm ci && npm run build)
set -euo pipefail

VERSION="${1:?version}"
RID="${2:?runtime identifier}"
OUT="${3:?output directory}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WEB_DIST="$ROOT/src/BerMap.Web/dist"
[[ -f "$WEB_DIST/index.html" ]] || { echo "Web console is not built: $WEB_DIST/index.html missing" >&2; exit 1; }

if [[ "$RID" == win-* ]]; then
  NAME="BerMap-Server-$VERSION-$RID"
else
  NAME="bermap-server-$VERSION-$RID"
fi

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/$NAME"
mkdir -p "$STAGE"

dotnet publish "$ROOT/src/BerMap.Api/BerMap.Api.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=None -p:GenerateDocumentationFile=false \
  -o "$STAGE/server" --nologo -v q

# Development settings (dev enrollment token, localhost CORS) never ship.
rm -f "$STAGE/server/appsettings.Development.json"
rm -rf "$STAGE/server/wwwroot"
cp -R "$WEB_DIST" "$STAGE/server/wwwroot"

cp "$ROOT/deploy/KURULUM.md" "$STAGE/KURULUM.md"
cp "$ROOT/CHANGELOG.md" "$STAGE/CHANGELOG.md"
if [[ "$RID" == win-* ]]; then
  cp "$ROOT/deploy/windows/"*.ps1 "$STAGE/"
  (cd "$WORK" && zip -qr "$OUT/$NAME.zip" "$NAME")
  echo "$OUT/$NAME.zip"
else
  cp "$ROOT/deploy/linux/install.sh" "$ROOT/deploy/linux/uninstall.sh" "$ROOT/deploy/linux/bermap-server.service" "$STAGE/"
  chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh" "$STAGE/server/BerMap.Api"
  tar -C "$WORK" -czf "$OUT/$NAME.tar.gz" "$NAME"
  echo "$OUT/$NAME.tar.gz"
fi
