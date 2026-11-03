#!/usr/bin/env bash
# 构建 + 装到 Mods + 打包发版。三个子命令：
#   ./build.sh build     只编译，产物在 bin/native/
#   ./build.sh install   编译并拷进 Mods/JunimoBridge/
#   ./build.sh package   编译并打成 release/JunimoBridge.zip（+ .sha256），用来发 GitHub Release
#
# 包内结构 = `JunimoBridge/{JunimoBridge.dll, FSharp.Core.dll, manifest.json}`
#   ⇒ 用户「解压到 Mods/」即可，不用自己建目录、不用改文件名。
#   ⚠️ FSharp.Core.dll **必须**在包里：SMAPI 加载时会去 mod 目录找依赖，找不到就整包跳过。
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
  package)
    "$DOTNET" build -c Release -v quiet --nologo
    rm -rf release/stage
    mkdir -p release/stage/JunimoBridge
    cp "$OUT/JunimoBridge.dll" "$OUT/FSharp.Core.dll" manifest.json release/stage/JunimoBridge/
    rm -f release/JunimoBridge.zip release/JunimoBridge.zip.sha256

    # 打 zip：系统有 zip 就用它，否则退回 python 的 zipfile（仓库测试本来就依赖 python）。
    # 别为"打个包"多装一个工具。
    if command -v zip >/dev/null 2>&1; then
      (cd release/stage && zip -q -r ../JunimoBridge.zip JunimoBridge)
    elif command -v python >/dev/null 2>&1; then
      (cd release/stage && python -c "
import zipfile, os
with zipfile.ZipFile('../JunimoBridge.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk('JunimoBridge'):
        for f in files:
            z.write(os.path.join(root, f))
")
    else
      echo "打包需要一个 zip 工具：装 zip，或保证 python 在 PATH 里" >&2
      exit 2
    fi

    # 校验和格式照 Junimo-MCP 的规矩：`<hash>␣␣文件名`（两个空格、不带 GNU 的 *）
    (cd release && python -c "
import hashlib
h = hashlib.sha256(open('JunimoBridge.zip','rb').read()).hexdigest()
open('JunimoBridge.zip.sha256','w',newline='').write(h + '  JunimoBridge.zip\n')
print('sha256 =', h)
")
    echo "产物: $HERE/release/JunimoBridge.zip"
    ;;
  *)
    echo "用法: $0 [build|install|package]" >&2
    exit 2
    ;;
esac
