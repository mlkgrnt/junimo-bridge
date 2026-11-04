# JunimoBridge

星露谷物语的**本地 HTTP 桥**，为 [Junimo-MCP](https://github.com/mlkgrnt/Junimo-MCP) 提供"让 AI 操作角色"的端点。

用 **F#** 写，只实现我们自己真正用到的那部分能力，报文形状与
[NagiBridge-Remix](https://github.com/anqinou-art/NagiBridge) 对齐——客户端改一个端口就能切过来。

[![CI](https://github.com/mlkgrnt/junimo-bridge/actions/workflows/ci.yml/badge.svg)](https://github.com/mlkgrnt/junimo-bridge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![F#](https://img.shields.io/badge/F%23-.NET%206-378bba)

---

## 为什么不用现成的

NagiBridge 系列能用，但有三类问题挡住了我们要做的事：

| 问题 | 具体表现 |
|---|---|
| **缺原语** | 只有"点一下"和"某一帧按一下"，没有**按住 N 毫秒**和连续轴输入。钓鱼小游戏、抛竿蓄力、卡丁车全卡在这里 |
| **读不到内部状态** | 小游戏与多数菜单的关键字段是 private，上游没有通用探针，`/minigame_state` 只硬编码了赌场两个游戏 |
| **执行模型有坑** | 上游 `/key` 的 `hold` 是在**游戏主线程**回调用 `Thread.Sleep` 实现：按住期间游戏一帧都没跑，松开时早已结束——游戏从未观察到这次按键 |

另外它的许可（PolyForm NC + 二改授权）意味着我们既不能改、也不该依赖它的语义。
所以这个仓库从零写起：**同样的能力，但执行模型是对的，并且完全可控。**

## 设计原则

1. **主线程永不 sleep。** 所有等待都表达成"每帧检查一次谓词"（`Ops` / `Frame`），
   HTTP 线程只负责投递任务并等待结果。
2. **动作 = 操作 + 后置条件。** 挥一次工具会等到生效帧（40 帧 ≈ 660ms）过去，
   再**回读目标格**才返回。不做"发出去就报 ok"。
3. **虚拟输入补在 `InputState.UpdateStates()`。** 而不是 Win32 `keybd_event`：
   不抢焦点、不依赖用户键位、不阻塞主线程，一次补丁覆盖菜单 / 小游戏 / 世界输入。
4. **零第三方依赖。** JSON 手写（不引 `System.Text.Json`，避免与游戏自带的那份版本打架），
   HTTP 用 BCL 的 `HttpListener`，Harmony 用 SMAPI 自带的那份。

## 端点

按用途分组列出（含 `/move` `/tool` 这类别名），全部实测过。

### 只读

| 端点 | 说明 |
|---|---|
| `GET /status` | 版本、端口、帧数、运行时长、待处理队列 |
| `GET /state` | 玩家 / 地点 / 时间 / 菜单 / 事件 / 背包 / 钓鱼（字段名与上游一致） |
| `GET /map` | 当前地图的传送点、建筑、NPC |
| `GET /surroundings?radius=N` | 周围**不可通行**的格子（`look` 用） |
| `GET /scan_chests` | 本图所有箱子及内容 |
| `GET /friendship?npc=xx` | 好感度、本周/今日送礼数 |
| `GET /find_npc?name=xx` | 全地图按名字找 NPC（含当前台词） |
| `GET /menu` | 菜单类型、对话正文、选项 |
| `GET /festival` | 当前节日事件（无事件时按上游约定返回 `ok:false`） |
| `GET /probe/active_menu` | **通用菜单探针**：把任意菜单摊成标量字段 + 可点击控件 |
| `GET /screen` | **窗口/视口探针**：物理像素窗口矩形、鼠标位置、焦点状态、菜单结构（菜单部分与 `/probe/active_menu` 同源，一次拿到省一次往返） |

### `GET /screen` 的返回

一次拿到「窗口在哪、鼠标在哪、有没有焦点、现在开着什么菜单」——**排查"点了没反应"时先看它**。实测返回：

```json
{"ok":true,
 "uiViewport":{"w":1280,"h":720},                    // 游戏 UI 逻辑尺寸
 "viewport":{"x":1696,"y":456,"w":1280,"h":720},     // 窗口在屏幕上的***物理像素***矩形
 "mouse":{"x":1030,"y":891},                         // 物理像素
 "handle":2296838,"foregroundHandle":2296838,        // 游戏窗口 / 前台窗口的 HWND
 "foreground":true,"isActive":true,                  // SDV 失焦会暂停，这两个是直接判据
 "menu":{"open":false,"type":"","components":[],"subMenu":null}}
```

要点：

- **`viewport` / `mouse` 是物理像素**，不是逻辑坐标。本机 DPI 175%：`GetWindowRect` 这类 Win32 调用（尤其在
  DPI-unaware 的进程里）给的是逻辑坐标，而这里给的是物理像素，**同一组数值差 1.75 倍**。拿逻辑坐标去裁剪录屏
  或换算点击点会整体错位——做换算时统一用这里的 `viewport`。
- `uiViewport` 才是游戏自己的 1280×720 坐标系（`/click` 用的就是这套）。
- `foreground` / `isActive` 为 `false` 时，SDV 会暂停，任何"点了没反应"都不是坐标问题。
- `menu` 与 `/probe/active_menu` 同源；开着菜单时 `type` 是菜单类名（如 `DialogueBox`、`Chest`、`ShopMenu`），
  `components` 是该菜单的可点控件，`subMenu` 是二级菜单。

### 移动

| 端点 | body | 说明 |
|---|---|---|
| `POST /walk_to` | `{x,y,location?,timeoutMs?}` | BFS 寻路（用游戏自己的 `isTilePassable`），一帧一格；跨图前自动 warp |
| `POST /move` | 同上 | `walk_to` 的别名 |
| `POST /warp` | `{location,x?,y?}` | 跨图传送；落点先校验，**并等位置真的变了才返回** |
| `POST /position` | `{x,y}` | 瞬移（兜底用，落点不可站会拒绝） |
| `POST /face` | `{direction}` | 转向，`1` 或 `"right"` 都吃 |
| `POST /stop` | — | 中止当前跨帧操作，并**当场结账**等待中的请求 |

### 动作

| 端点 | body | 说明 |
|---|---|---|
| `POST /use`、`/tool` | — | 挥一次手里的工具，等到生效帧再回读目标格 |
| `POST /interact` | — | 对面前那格 `checkAction`（开门 / 说话 / 开箱） |
| `POST /key` | `{key,count}` | 注入一次按键。语义名 `confirm`/`action`/`ok`、`skip`、`escape`/`menu`/`esc`、`cancel`/`back`、`up`/`down`/`left`/`right`，以及字面键名 `w/a/s/d`、`space`、`enter`、`0-9`、`f1-f12` 等。**方向名和 cancel 会解析成 `Game1.options` 里实际绑定的键**（默认 up/down/left/right = `W`/`S`/`A`/`D`，cancel = `V`，menu = `E`+`Escape`）——箭头键在 SDV 里默认没绑定，直接注入等于按空气。回执带 `resolvedKeys` / `observedKeys` 以便核对。不认识的键名**如实报错**，不静默回 ok |
| `POST /sleep` | `{stay?}` | 走游戏自己的 `answerDialogueAction("Sleep_Yes")`，真实跨日。⚠️ **跨日 ≠ 已存盘**：白天强行触发时游戏会推进日期却跳过存盘（实测），调用方要自己核对存档文件 mtime —— junimo-mcp v3.2.4 起已内置这层核对 |
| `POST /pause`、`/resume` | — | 冻结 / 恢复游戏时间 |
| `POST /refill` | — | 灌满水壶 |
| `POST /eat` | — | 吃手里的东西 |
| `POST /harvest?radius=N` | — | 收半径内成熟作物 |
| `POST /tool_area` | `{operation,x1,y1,x2,y2}` | 矩形批量锄/浇；会先选对工具、站到目标旁、挥完回读校验 |
| `POST /click` | `{x,y,no_move?}` | **先把 OS 光标挪过去**再 `receiveLeftClick`（标题菜单的 Load 按钮要求光标真的在按钮上） |
| `POST /focus` | — | 把游戏窗口顶到前台；成败看 `Game1.game1.IsActive`，不看 Win32 返回值 |

### 钓鱼

| 端点 | body | 说明 |
|---|---|---|
| `GET /fishing` | — | 竿 / 手上有没有 / 浮标格 / 等咬钩还是已咬钩 / 搏鱼实时局面（进度、绿条、鱼位置、难度） |
| `GET /water?radius=N` | — | 附近哪几格有水，以及「站哪格、往哪抛」的钓点列表 |
| `POST /cast` | `{ms}` | 按住「使用」蓄力再松手抛竿（`ms` 越大越远，失败自动重试一次） |
| `POST /reel` | — | 咬钩窗口内提竿（优先直接调 `FishingRod.DoFunction`） |
| `POST /fish` | `{timeoutMs,lookahead,deadband}` | 全自动一条：抛竿 → 等咬钩 → 提竿 → 预测式 PD 控绿条 → 结算。回执带 `won`（打赢小游戏）与 `delivered`（物品真的入包）、`trace`（每轮过程） |
| `GET /debug/bar` | — | 搏鱼时实时读 `BobberBar` 字段 |

### 收线交付（v0.3.1，主路径已修）

v0.2.0 的「赢了小游戏但鱼不进包」根因已定位并修好，真机多次验证
`won=true / delivered=true / caughtItems=[Sunfish]`。

整条链（全部来自 IL）：

```
BobberBar.update 成功 → rod.pullFishFromWater(...)         只 Fire 一个 NetEvent
→ doPullFishFromWater                                      设 pullingOutOfWater=true，
                                                           把收线 sprite 放进 FishingRod.animations
→ sprite.endFunction → playerCaughtFishEndFunction          清 pullingOutOfWater、置 fishCaught
→ tickUpdate 的 fishCaught 分支等一次 use/点击 → doneHoldingFish   createItemDebris / addItemToInventoryBool
```

**卡点**：`FishingRod.tickUpdate` IL_0177 开头要求
`who.CurrentTool is FishingRod && who.UsingTool`，否则直接 `ret`。
收线期 `UsingTool` 掉了 ⇒ 每帧早退 ⇒ `FishingRod.animations` 里的 sprite 永不推进
⇒ 回调不跑 ⇒ `pullingOutOfWater=true` 永久挂着、鱼不发。
此时按 use 会在 `fishCaught=false` 的 `DoFunction` 里走**抛竿分支**，把待结算的鱼清掉
—— 这正是「输入一进去就变新抛竿」。

**修法**：`Fishing.tickFinish()` 每帧（只在 `/fish` 期间生效）把 `UsingTool` 按回 true，
让游戏自己把动画跑完；等 `fishCaught=true` 再替玩家点那一下 `doneHoldingFish`。
全程不碰 `whichFish`、不造物品，发鱼仍走游戏自己的 `createItemDebris`。
`POST /debug/fishchain {"action":"none"}` 可把整条链的状态一次摊开。

### 连续钓（v0.3.2，不再空转）

四节点快照（cast 前 / cast 后 / 咬钩后 / 提竿后）定位到：`reelOnce` 里那句
`if not p.CanMove then p.forceCanMove ()` 是元凶 —— 抛竿/等咬钩期间
`CanMove=false` 是**游戏正常状态**（casting 动画锁），强行恢复会打断 Farmer
状态机，导致 `DoFunction` 之后 `isFishing`/`isNibbling` 被一起清掉、
`BobberBar` 开不出来。删掉之后，连续钓每一竿都能正常走完提竿并发放物品。

另外修了回执口径：**没开 BobberBar ≠ 没钓到**。垃圾类物品
（Joja Cola / Driftwood / Broken CD / Soggy Newspaper …）走 `DoFunction` 的
直接发放路径、不进小游戏；旧回执一律报「三轮都没开成小游戏」。
现在以**背包增量**为准，回执分三种：

| 字段 | 含义 |
|---|---|
| `won=true / delivered=true` | 打赢小游戏且物品入包（正常鱼） |
| `won=false / directCatch=true / delivered=true` | 直接收获（垃圾等，无需小游戏） |
| `delivered=false` | 真没钓到，可直接重试 |

真机：连续 4 竿 `ok=true / delivered=true`（1 条 Sunfish + 3 轮垃圾）。

⚠️ **仍存限制**：后续竿拿到垃圾的比例偏高（农场池塘 `getFish` 本身如此，
换钓点/加鱼饵可改善），偶尔也会空竿；需要稳定钓正常鱼时仍建议重新进档后钓第一竿，
或用 `cast` + `fishing` + `reel` 分步自己驱动。回执始终分开报 `won` / `delivered`，
不拿进度冒充成果。`/fish` 的 `trace` 每轮带四节点 `snap`，排查可直接看。

### 小游戏

`IMinigame` 有直接入口 `receiveKeyPress/receiveKeyRelease/forceQuit`，不用赌键盘状态。
键名走**字面物理键**（小游戏不看 `options` 绑定）。

| 端点 | body | 说明 |
|---|---|---|
| `GET /minigame?limit=N` | — | 有没有小游戏在跑、id / 类型、关键数值字段（反射摊开） |
| `POST /minigame/key` | `{key,holdMs}` | 直接喂 `receiveKeyPress`，到点再 `receiveKeyRelease` |
| `POST /minigame/quit` | — | `forceQuit()`，卡住时唯一的出路 |

实时反应型游戏靠一帧一帧喂键很吃节奏，能操作但不保证成绩。

### 后台操控（失焦 / 最小化）

默认开启：窗口**最小化之后照样能被 MCP 完整操控**，不用再 `/focus`、不用把窗口摆在最前面。

做法：每帧把 `GameRunner.instance.Platform._isActive` 按回 `true`（`Background.fs`）。
这是 `Game.get_IsActive()` → `GamePlatform.get_IsActive()` 最终读的私有字段，
也是 `IsActiveNoOverlay` 的数据源；**给那些小 getter 打 Harmony 会被 JIT 内联绕过**，
改字段才能让所有读取点（含内联副本）一起生效。配合原有的 `HasKeyboardFocus` 补丁即可。

真机对照（同一个进档、同一发调用）：

| | 最小化（未开） | 最小化（开启） | 前台 |
|---|---|---|---|
| tick / 游戏时间 | 正常 | 正常 | 正常 |
| `press_key left` 朝向 | 不变 | `1→3`、`observedKeys=A` | `1→3` |
| `key menu` | `open:false` | `GameMenu` | `GameMenu` |

开关：
- 运行时 `POST /background {"enabled":false}` 关 / `true` 开；
- 启动前设 `STARDEW_FOREGROUND_ONLY=1` 则默认关；
- `GET /debug/focus` 一次摊开 Win32 前台、`minimized`、`isActiveNoOverlay`、
  `platformPinned`、`pinnedFrames` 等，排查焦点问题不用再猜。

注意：开启后最小化时游戏时间**照常流逝**（`pauseWhenOutOfFocus` 本来就是 false），
挂机前该睡一觉落盘就睡一觉。

### 调试端点（不进 MCP 工具层）

| 端点 | body | 说明 |
|---|---|---|
| `POST /debug/give` | `{id,count}` | 直接塞物品进背包，不扣钱。只用于回归（比如给测试档一根 `(T)BambooPole`）——不是正常玩法 |
| `GET /debug/move` | — | 一次摊开 `CanMove` / `pauseTime` / `locationRequest` / 四向碰撞 / 按键绑定 |
| `POST /debug/fishchain` | `{action}` | 摊开收线交付整条链（rod 标志 / 角色状态 / `FishingRod.animations` / 背包）；`action` 另有 `endfn` / `caught` / `holding` / `tick` 供排查 |

### 物品与经济

| 端点 | body | 说明 |
|---|---|---|
| `POST /select` | `{name}` 或 `{slot}` | 选物品；超过 12 格会先换到第 0 格 |
| `GET /inventory` | — | 背包全量（含手持工具与水量） |
| `POST /buy` | `{id,quantity}` | 按售价 ×2 直接进货 |
| `POST /sell` | `{all,name?}` | 丢进农场出货箱 |
| `POST /gift` | `{target,item}` | 送礼，返回口味评价（love/like/neutral/dislike/hate） |
| `POST /chest` | `{x,y}` | 读指定箱子 |
| `POST /store` | `{x,y,name?,keepTools?}` | 背包整叠放进箱子 |
| `POST /chest_take` | `{x,y,name?,slot?,count?}` | 从箱子取一格 |
| `POST /chest_take_list` | `{items:[...]}` | 按清单一次取多件 |

### 菜单与节日

| 端点 | body | 说明 |
|---|---|---|
| `POST /menu/click` | `{component}` / `{x,y}` / `{option}` | `option` 走游戏自己的 `selectedResponse` + `Enter` |
| `POST /menu_close` | — | 关菜单（**钓鱼小游戏也是菜单，关掉＝放跑那条鱼**） |
| `POST /festival/interact` | `{name?}` | 节日里与 NPC / 摊位交互 |
| `POST /festival/answer` | `{answer}` | 节日问答选第 N 项 |

### 虚拟输入（本仓库独有）

| 端点 | body | 说明 |
|---|---|---|
| `POST /input/hold` | `{button \| buttons, ms}` | 按住鼠标/键若干毫秒，到时自动松开 |
| `POST /input/release` | — | 立刻全松 |
| `GET /input/state` | — | 当前按住什么，以及**游戏实际读到的输入**（补丁生效的证据） |

## 快速开始

### 前置

- Stardew Valley **1.6.x** 与 SMAPI **4.x**
- .NET SDK 8（只用于编译；mod 目标框架是 `net6.0`）
- 游戏目录里有 `StardewModdingAPI.exe`

### 下载安装（不用编译）

从 [Releases](https://github.com/mlkgrnt/junimo-bridge/releases/latest) 下载
`JunimoBridge.zip`，解压到游戏的 `Mods/` 目录，最终应当是：

```
Stardew Valley/
└── Mods/
    └── JunimoBridge/
        ├── JunimoBridge.dll
        ├── FSharp.Core.dll      ← 必须在，缺了 SMAPI 会整包跳过
        └── manifest.json
```

`FSharp.Core.dll` 是随包带的依赖，**不要**只挑 `JunimoBridge.dll` 出来放。

### 构建与安装

```bash
git clone https://github.com/mlkgrnt/junimo-bridge
cd junimo-bridge

export DOTNET=/path/to/dotnet     # SDK 不在 PATH 里时指定
./build.sh install                # 编译并拷进 Mods/JunimoBridge/
./build.sh package                # 或者：只打包成 release/JunimoBridge.zip
```

`JunimoBridge.fsproj` 里的 `<GameDir>` 默认指向
`D:\SteamLibrary\steamapps\common\Stardew Valley`，换位置改它即可。

启动游戏后 SMAPI 日志里应该出现：

```
[ALERT JunimoBridge] JunimoBridge 0.1.0 已启动，HTTP 监听 http://localhost:7850/
[INFO  JunimoBridge] JunimoBridge 已应用 1 个 Harmony 补丁
```

### 试一下

```bash
curl -s --noproxy '*' http://localhost:7850/status
curl -s --noproxy '*' http://localhost:7850/state
curl -s --noproxy '*' -X POST -d '{"x":40,"y":32}' http://localhost:7850/walk_to
curl -s --noproxy '*' -X POST -d '{"button":"use","ms":800}' http://localhost:7850/input/hold
```

## 与 NagiBridge 共存 / 迁移

|项目|NagiBridge-Remix|JunimoBridge|
|---|---|---|
| 端口 | 7842 / 7843 | **7850** |
| 许可 | PolyForm NC（二改版） | MIT |
| 端点 | 174 | 45 |

两个 mod 可以同时装，互不干扰。`Junimo-MCP` 里把 `STARDEW_API_URL` 指到哪个端口就用哪个：

```bash
STARDEW_API_URL=http://localhost:7850 junimo-mcp
```

**报文字段与上游对齐**（`/state` 的 `player.currentTool`、`time.dayOfMonth`、
`location.mapWidth`、`inventory[].category` 等）。`test/ref/` 里存着同一台机器上从上游采下来的
真实响应，可以直接 diff。

### 已知差异（结果一致，实现深度不同）

| 差异 | 影响 |
|---|---|
| `/tool_area` 一格一挥（约 0.66s/格） | 矩形越大越慢；**结果一致**（挥完回读校验） |
| 节日问答用 `selectedResponse` 启发式 | 没有真节日时无法验证 |
| `status.caps` 是空对象 | 客户端不读 |

## 目录

```
JunimoBridge.fsproj   net6.0；引用游戏 dll（Private=false）+ smapi-internal 三个 dll
manifest.json         SMAPI 清单，UniqueID mlkgrnt.JunimoBridge
Json.fs               手写 JSON（值类型 / 渲染 / 解析）
Runtime.fs            版本、端口、帧计数、时间冻结
Frame.fs              主线程工作队列（post / drain / pending）
Ops.fs                每帧操作引擎（跨帧动作的 step 闭包）
Input.fs              按住状态 + 到期释放 + 输入观测
Patches.fs            Harmony：InputState.UpdateStates 后置改写
Probe.fs              菜单探针（标量字段 + 可点击控件）
Walk.fs               BFS 寻路 + 逐格走位
Actions.fs            转向 / 用工具 / 交互 / 休眠 / 菜单
World.fs              背包 / 选择 / 箱子
Compat.fs             只读端点（/state /map /surroundings…），报文对齐上游
Extra.fs              瞬移 / 按键 / 暂停 / 吃 / 打水 / 收菜 / 买卖 / 送礼
Farm.fs               矩形批量农活
Festival.fs           节日
Server.fs             HttpListener（localhost:7850，keep-alive）
Mod.fs                SMAPI 入口
build.sh              build | install
test/smoke.sh         端到端冒烟（需要游戏在跑）
test/json/            纯 JSON 断言（CI 跑的就是它）
test/ref/             从上游采样的参考报文（回归资产）
```

## 测试

```bash
dotnet run --project test/json    # 不需要游戏，CI 跑这个
./test/smoke.sh                   # 需要游戏在跑，39 项断言
```

`test/smoke.sh` 覆盖：服务活着、`/state` 结构、按住输入是否被游戏读到（`observedLeft`）、
走位、跨图 warp、`/stop` 结账、兼容层键名、各类失败路径。

## 为什么 mod 本身没有 CI 构建

编译需要 `Stardew Valley.dll` / `StardewModdingAPI.dll` / `xTile.dll`，这些是游戏本体文件，
不能进仓库。所以 Actions 只跑不依赖游戏的部分（JSON 断言、脚本检查、清单校验），
端到端回归必须在装了游戏的机器上跑。

## 许可

[MIT](LICENSE)。不含任何上游源码；端点语义参考上游行为与游戏反编译结果，实现为本仓库原创。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
