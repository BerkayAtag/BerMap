#!/usr/bin/env bash
# Builds a self-contained BerMap package for one runtime.
#
#   deploy/package.sh <version> <rid> <out-dir> [server|collector]
#       rid: win-x64 | linux-x64 | linux-arm64 | osx-arm64; component defaults to server
#
# server:    <out-dir>/BerMap-Server-<version>-win-x64.zip or bermap-server-<version>-<rid>.tar.gz
#            (API + web console). The web console must already be built (src/BerMap.Web/dist),
#            e.g. with: (cd src/BerMap.Web && npm ci && npm run build)
# collector: <out-dir>/BerMap-Collector-<version>-win-x64.zip or bermap-collector-<version>-<rid>.tar.gz
set -euo pipefail

VERSION="${1:?version}"
RID="${2:?runtime identifier}"
OUT="${3:?output directory}"
COMPONENT="${4:-server}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
case "$COMPONENT" in
  server)
    PROJECT="$ROOT/src/BerMap.Api/BerMap.Api.csproj"
    BINARY=BerMap.Api
    WEB_DIST="$ROOT/src/BerMap.Web/dist"
    [[ -f "$WEB_DIST/index.html" ]] || { echo "Web console is not built: $WEB_DIST/index.html missing" >&2; exit 1; }
    ;;
  collector)
    PROJECT="$ROOT/src/BerMap.Collector/BerMap.Collector.csproj"
    BINARY=BerMap.Collector
    ;;
  *) echo "Unknown component: $COMPONENT (server or collector)" >&2; exit 2 ;;
esac

if [[ "$RID" == win-* ]]; then
  TITLE="$(tr '[:lower:]' '[:upper:]' <<< "${COMPONENT:0:1}")${COMPONENT:1}"
  NAME="BerMap-$TITLE-$VERSION-$RID"
else
  NAME="bermap-$COMPONENT-$VERSION-$RID"
fi

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/$NAME"
mkdir -p "$STAGE"

dotnet publish "$PROJECT" \
  -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=None -p:GenerateDocumentationFile=false \
  -o "$STAGE/$COMPONENT" --nologo -v q

# Development settings (dev enrollment token, localhost URLs) never ship.
rm -f "$STAGE/$COMPONENT/appsettings.Development.json"
cp "$ROOT/CHANGELOG.md" "$STAGE/CHANGELOG.md"

if [[ "$COMPONENT" == server ]]; then
  rm -rf "$STAGE/server/wwwroot"
  cp -R "$WEB_DIST" "$STAGE/server/wwwroot"
  cp "$ROOT/deploy/KURULUM.md" "$STAGE/KURULUM.md"
  SCRIPTS_WINDOWS="$ROOT/deploy/windows"
  SCRIPTS_LINUX=("$ROOT/deploy/linux/install.sh" "$ROOT/deploy/linux/uninstall.sh" "$ROOT/deploy/linux/bermap-server.service")
else
  cp "$ROOT/deploy/KURULUM-COLLECTOR.md" "$STAGE/KURULUM-COLLECTOR.md"
  SCRIPTS_WINDOWS="$ROOT/deploy/collector/windows"
  SCRIPTS_LINUX=("$ROOT/deploy/collector/linux/install.sh" "$ROOT/deploy/collector/linux/uninstall.sh" "$ROOT/deploy/collector/linux/bermap-collector.service")
fi

if [[ "$RID" == win-* ]]; then
  cp "$SCRIPTS_WINDOWS/"*.ps1 "$STAGE/"
  (cd "$WORK" && zip -qr "$OUT/$NAME.zip" "$NAME")
  echo "$OUT/$NAME.zip"
else
  cp "${SCRIPTS_LINUX[@]}" "$STAGE/"
  chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh" "$STAGE/$COMPONENT/$BINARY"
  # No extended attributes: archives made on macOS otherwise warn when unpacked on Linux.
  COPYFILE_DISABLE=1 tar --no-xattrs -C "$WORK" -czf "$OUT/$NAME.tar.gz" "$NAME"
  echo "$OUT/$NAME.tar.gz"
fi
