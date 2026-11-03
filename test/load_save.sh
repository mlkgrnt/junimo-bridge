#!/usr/bin/env bash
# 标题界面 → 进档（点 Load → 点存档格 → 等 worldReady）。
#
# 真机踩出来的三条，缺一条就"点了没反应"：
#   ① **标题动画没走完时 `receiveLeftClick` 直接 return** —— `ShouldAllowInteraction()`
#      里 `titleInPosition` 为 false 就 false。所以必须先等它 true，不是死等几秒。
#   ② 读档界面藏在 `TitleMenu` 的 **static** `subMenu` 里（`get_subMenu` 是静态属性访问器），
#      `Game1.activeClickableMenu` 永远只报 `TitleMenu` —— "界面开没开"要读 `subMenu`。
#   ③ 存档格中心 = **(640, 129 + slot*166)**（1280×720 UI）。
#
# 用法: ./test/load_save.sh [存档槽]        环境: STARDEW_API_URL
set -euo pipefail

B="${STARDEW_API_URL:-http://localhost:7850}"
SLOT="${1:-0}"

get() { curl -s --noproxy '*' -m 4 "$B$1"; }
post() { curl -s --noproxy '*' -m 6 -X POST "$B$1" -H 'Content-Type: application/json' -d "${2:-{\}}"; }

json() { python -c "import sys,json;d=json.load(sys.stdin);print($1)"; }

echo "→ 等 /status 可达"
for _ in $(seq 1 60); do
  if get /status | grep -q '"ok"'; then break; fi
  sleep 2
done

if get /status | grep -q '"worldReady":true'; then
  echo "  已在档内，跳过"
  exit 0
fi

echo "→ 等标题动画走完（titleInPosition）"
for _ in $(seq 1 60); do
  v=$(get /menu | json "d['probe']['fields'].get('titleInPosition')" 2>/dev/null || echo None)
  [ "$v" = "True" ] && break
  sleep 1
done

post /focus >/dev/null || true

echo "→ 点 Load(517,609)"
post /click '{"x":517,"y":609}' >/dev/null

echo "→ 等读档界面（subMenu = LoadGameMenu）"
ok=""
for _ in $(seq 1 30); do
  t=$(get /menu | json "(d.get('subMenu') or {}).get('type','')" 2>/dev/null || echo "")
  [ "$t" = "LoadGameMenu" ] && { ok=1; break; }
  sleep 0.5
done
[ -n "$ok" ] || { echo "✗ 读档界面没打开" >&2; exit 1; }

y=$((129 + SLOT * 166))
echo "→ 点存档槽 $SLOT（640,$y）"
post /click "{\"x\":640,\"y\":$y}" >/dev/null

echo "→ 等 worldReady"
for _ in $(seq 1 40); do
  if get /status | grep -q '"worldReady":true'; then echo "✓ 进档成功"; exit 0; fi
  sleep 1.5
done

echo "✗ 进档超时" >&2
exit 1
