#!/usr/bin/env bash
# Builds and publishes the launcher for both platforms.
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
PROJECT=src/DusmpLauncher
UPDATER=src/DusmpUpdater
PUBLISH_FLAGS=(-c Release --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false)

case "${1:-build}" in
  build)   "$DOTNET" build "$PROJECT" ;;
  run)     DUSMP_API_MODE="${DUSMP_API_MODE:-live}" "$DOTNET" run --project "$PROJECT" ;;
  linux)
    "$DOTNET" publish "$PROJECT" "${PUBLISH_FLAGS[@]}" -r linux-x64 -o dist/linux/launcher
    "$DOTNET" publish "$UPDATER" "${PUBLISH_FLAGS[@]}" -r linux-x64 -o dist/linux/updater ;;
  windows)
    "$DOTNET" publish "$PROJECT" "${PUBLISH_FLAGS[@]}" -r win-x64 -o dist/win/launcher
    "$DOTNET" publish "$UPDATER" "${PUBLISH_FLAGS[@]}" -r win-x64 -o dist/win/updater ;;
  all)     "$SCRIPT_DIR/build.sh" linux && "$SCRIPT_DIR/build.sh" windows ;;
  *)       echo "usage: $0 {build|run|linux|windows|all}" >&2; exit 2 ;;
esac
