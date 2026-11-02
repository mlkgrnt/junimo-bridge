#!/usr/bin/env bash
# 构建 + 装到 Mods。两个子命令：
#   ./build.sh build    只编译，产物在 bin/native/
#   ./build.sh install  编译并拷进 Mods/JunimoBridge/
set -euo pipefail

DOTNET="${DOTNET:-/e/dotnet/dotnet}"
GAME="D:/SteamLibrary/steamapps/common/Stardew Valley"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="bin/native"

cd "$HERE"

case "${1:-build}" in
  build)
    "$DOTNET" build -c Release -v quiet --nologo
    echo "产物: $HERE/$OUT/JunimoBridge.dll"
    ;;
  install)
    "$DOTNET" build -c Release -v quiet --nologo
    mkdir -p "$GAME/Mods/JunimoBridge"
    cp "$OUT"/*.dll "$GAME/Mods/JunimoBridge/"
    cp manifest.json "$GAME/Mods/JunimoBridge/"
    echo "已装入 $GAME/Mods/JunimoBridge/"
    ;;
  *)
    echo "用法: $0 [build|install]" >&2
    exit 2
    ;;
esac
