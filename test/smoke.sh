#!/usr/bin/env bash
# 端到端冒烟：需要游戏在跑（mod 已加载）。
#   ./test/smoke.sh [端口]
# 断言：/status 活着、/state 结构对、按住/键盘能被游戏读到、未知端点 404。
set -uo pipefail

PORT="${1:-7850}"
BASE="http://localhost:${PORT}"
pass=0
fail=0

# 别用 eval 拼命令：`--noproxy '*'` 里的 * 一旦被 shell 展开，curl 就会拿到一堆文件名
get() { curl -s --noproxy '*' --max-time 5 "${BASE}$1"; }
post() { curl -s --noproxy '*' --max-time 5 -X POST -d "$2" "${BASE}$1"; }

check() {
  local what="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "  ok   ${what}"
    pass=$((pass + 1))
  else
    echo "  FAIL ${what}"
    fail=$((fail + 1))
  fi
}

echo "== 1. /status =="
STATUS=$(get /status)
echo "  ${STATUS}"
check "返回 ok:true" grep -q '"ok":true' <<<"$STATUS"
check "带 tick" grep -q '"tick":' <<<"$STATUS"
check "带 mod 名" grep -q '"server":"JunimoBridge"' <<<"$STATUS"

echo "== 2. /state =="
STATE=$(get /state)
echo "  ${STATE:0:220}..."
check "返回 ok:true" grep -q '"ok":true' <<<"$STATE"
check "有 player 块" grep -q '"player":{' <<<"$STATE"
check "有 time 块" grep -q '"time":{' <<<"$STATE"

echo "== 3. 鼠标按住注入（关键：observedLeft 必须真的变 true）=="
post /input/hold '{"button":"use","ms":1200}' >/dev/null
sleep 0.3
HELD=$(get /input/state)
echo "  ${HELD}"
check "按住期间 observedLeft=true" grep -q '"observedLeft":true' <<<"$HELD"
sleep 1.3
RELEASED=$(get /input/state)
echo "  ${RELEASED}"
check "到期后 observedLeft=false" grep -q '"observedLeft":false' <<<"$RELEASED"

echo "== 4. 键盘注入 =="
post /input/hold '{"buttons":["w"],"ms":800}' >/dev/null
sleep 0.25
KEYS=$(get /input/state)
echo "  ${KEYS}"
check "observedKeys 里出现 W" grep -q 'observedKeys":"[^"]*W' <<<"$KEYS"
post /input/release '{}' >/dev/null

echo "== 4b. /key 语义名解析（方向键在 SDV 里默认没绑）=="
BIND=$(get /debug/move)
echo "  bindings=$(python -c "import sys,json;print(json.load(sys.stdin)['bindings'])" <<<"$BIND" 2>/dev/null || echo "$BIND")"
check "debug/move 给出 cancel 绑定" grep -q '"cancel":"[A-Za-z]' <<<"$BIND"
DIRK=$(post /key '{"key":"left"}')
echo "  ${DIRK:0:220}"
# 关键：resolvedKeys 必须是 W/A/S/D 之一（跟随 options），不能是箭头键
check "left 解析到游戏内实际绑定" grep -q '"resolvedKeys":"[A-Za-z]' <<<"$DIRK"
check "left 注入真的落地" grep -q '"observedKeys":"[^"]' <<<"$DIRK"
BOGUS=$(post /key '{"key":"nonsense"}')
check "不认识键名如实报错" grep -q '"ok":false' <<<"$BOGUS"
# cancel 走一遍：先开菜单，再用 cancel 关掉，结束时保持干净
post /key '{"key":"menu"}' >/dev/null
sleep 0.5
CANCEL=$(post /key '{"key":"cancel"}')
echo "  ${CANCEL:0:180}"
check "cancel 被接受（不再报不认识）" grep -q '"ok":true' <<<"$CANCEL"
sleep 0.5

echo "== 5. M3 探针 =="
PROBE=$(get /probe/active_menu)
echo "  ${PROBE:0:160}..."
check "探针可用" grep -q '"ok":true' <<<"$PROBE"

echo "== 6. M5 背包 =="
INV=$(get /inventory)
echo "  ${INV:0:160}..."
check "背包有物品" grep -q '"count":[1-9]' <<<"$INV"
check "带 currentSlot" grep -q '"currentSlot":' <<<"$INV"

echo "== 7. M4 转向" 
FACE=$(post /face '{"direction":"down"}')
echo "  ${FACE}"
check "转向前成功" grep -q '"ok":true' <<<"$FACE"
FACE_BAD=$(post /face '{"direction":"sideways"}')
check "非法方向被拒" grep -q '"ok":false' <<<"$FACE_BAD"

echo "== 8. M4 用工具（等生效帧回读）=="
USE=$(post /use '{}')
echo "  ${USE}"
check "挥工具成功" grep -q '"ok":true' <<<"$USE"
check "带回读的目标格" grep -q '"target":{' <<<"$USE"

echo "== 9. M4 走位（原地 = 零步）=="
POS=$(get /state)
PX=$(sed -n 's/.*"x":\([0-9]*\),"y":\([0-9]*\).*/\1/p' <<<"$POS" | head -1)
PY=$(sed -n 's/.*"x":\([0-9]*\),"y":\([0-9]*\).*/\2/p' <<<"$POS" | head -1)
WALK=$(post /walk_to "{\"x\":${PX},\"y\":${PY}}")
echo "  ${WALK}"
check "原地走位返回 arrived" grep -q '"arrived":true' <<<"$WALK"

echo "== 10. M3 菜单读取 =="
MENU=$(get /menu)
echo "  ${MENU}"
check "菜单端点可用" grep -q '"open":' <<<"$MENU"

echo "== 11. 跨图 warp（必须等落地）=="
WARP=$(post /warp '{"location":"Farm","x":40,"y":32}')
echo "  ${WARP}"
check "warp 成功" grep -q '"ok":true' <<<"$WARP"
check "带回落地帧数" grep -q '"frames":' <<<"$WARP"
LOC=$(get /state | grep -o '"location":{"name":"[^"]*"')
check "确实到了 Farm" grep -q 'Farm' <<<"$LOC"

echo "== 12. 走位 + 中途 /stop（必须立刻结账）=="
(post /walk_to '{"x":72,"y":58,"timeoutMs":20000}' > /tmp/junimo_smoke_walk.txt) &
sleep 0.6
post /stop '{}' >/dev/null
wait
WALK=$(cat /tmp/junimo_smoke_walk.txt)
echo "  ${WALK}"
check "被中止并明确回报" grep -q '"aborted":true' <<<"$WALK"

echo "== 13. store 失败路径 =="
curl -s --noproxy '*' --max-time 6 -X POST -d '{}' "$BASE/menu_close" >/dev/null
STORE=$(post /store '{"name":"Axe"}')
echo "  ${STORE}"
check "没箱子时如实报错" grep -q '"ok":false' <<<"$STORE"

echo "== 14. 兼容层：/state 形状（客户端要的键都得在）=="
ST=$(get /state)
for k in '"currentTool"' '"facingDirection"' '"isMoving"' '"mapWidth"' '"dayOfMonth"' '"inventory"'; do
  check "有 ${k}" grep -q "$k" <<<"$ST"
done
echo "== 15. 兼容层：/map /scan_chests /surroundings =="
check "map 有 warps" grep -q '"warps":' <<<"$(get /map)"
check "scan_chests 有 count" grep -q '"count":' <<<"$(get /scan_chests)"
check "surroundings 有 tiles" grep -q '"tiles":' <<<"$(get '/surroundings?radius=2')"
echo "== 16. 兼容层：动作端点键名 =="
check "pause 回 frozenAt" grep -q '"frozenAt":' <<<"$(post /pause '{}')"
check "resume 回 resumed" grep -q '"resumed"' <<<"$(post /resume '{}')"
check "position 回 positioned" grep -q '"positioned"' <<<"$(post /position '{"x":9,"y":9}')"
check "harvest 回 harvested" grep -q '"harvested":' <<<"$(post '/harvest?radius=1' '{}')"
check "refill 回 water" grep -q '"water":' <<<"$(post /refill '{}')"
check "tool_area 非法地图会拒绝" grep -q '"ok":false' <<<"$(post /tool_area '{"operation":"till","x1":0,"y1":0,"x2":0,"y2":0}')"

echo "== 17. 插件依赖的两个端点（/focus /click）=="
check "focus 可用" grep -q '"ok":true' <<<"$(post /focus '{}')"
CLICK=$(post /click '{"x":640,"y":360,"no_move":true}')
echo "  ${CLICK}"
check "click 走通（菜单或动作键）" grep -q '"action":"\(menu_click\|action_button\|event_advance\)"' <<<"$CLICK"

echo "== 18. 未知端点 =="
CODE=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' --max-time 5 "${BASE}/nope")
check "返回 404" test "$CODE" = "404"

echo
echo "通过 ${pass} / 失败 ${fail}"
[ "$fail" -eq 0 ]
