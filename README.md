# JunimoBridge

Junimo-MCP 自用的本地 HTTP 桥，用来**替换掉对本项目不必要的那部分 NagiBridge-Remix 依赖**。

只实现我们自己真正用到的能力，不追平上游的 174 个端点。

- 语言：**F#**（.NET 6 / SMAPI 4.x）
- 端口：**7850**（NagiBridge 占 7842 / 7843，两者可同时运行）
- 许可：MIT（本项目自有代码；不含任何上游源码）

---

## 为什么不用现成的

NagiBridge-Remix 是别人的二改版（PolyForm NC，由原作者授权），能用但有三类问题：

1. **缺原语**。它只有「点一下」和「某一帧按一下」，没有「按住 N 毫秒」和「连续轴输入」。
   钓鱼小游戏、抛竿蓄力、弹弓、祝尼魔卡丁车、草原之王全都卡在这上面。
2. **读不到内部状态**。小游戏和很多菜单的关键字段是 private，它没有通用探针，
   `/minigame_state` 只硬编码了赌场两个游戏。
3. **它自己的实现有坑**。比如 `/key` 的 `hold` 参数是在游戏主线程回调用 `Thread.Sleep` 实现的，
   等于把游戏循环冻住 —— 松开发生在「没有任何帧」的间隙里，游戏根本观察不到这次按住。

我们要的不是更多端点，是**更对的执行模型**。

## 设计原则

1. **主线程永不 sleep**。所有等待都表达成游戏时间条件（帧数 / 谓词），
   在 `UpdateTicking` 里推进。HTTP 线程只负责把工作投进队列然后 await。
2. **一个请求 = 一次语义操作 + 后置条件**。不做「发出去就当成功」，
   效果在进程内回读，避免 550ms 猜谜那类 bug。
3. **虚拟输入走 Harmony 注入 `InputState`**，而不是 Win32 `keybd_event`：
   不抢焦点、不依赖用户键位、不阻塞主线程，一次补丁覆盖菜单/小游戏/世界。
4. **零第三方依赖**。JSON 手写（游戏目录自带 `System.Text.Json`，多引一份就有版本冲突风险），
   HTTP 用 BCL 的 `HttpListener`，Harmony 用 SMAPI 自带的那份。

## 已确认的技术事实（spike 实测，2026-11-03）

- F# 编出来的 dll 能被 SMAPI 4.5.2 正常加载；
- `GameLoop.UpdateTicking` 每帧触发，实测严格 60fps；
- F# 可直接读 `Game1.activeClickableMenu` / `Game1.oldMouseState` / `player.CurrentTool`；
- **`helper.Monitor` 在 SMAPI 4 已删除**，必须用 `this.Monitor`；
- **`FSharp.Core.dll` 必须随包**，否则报 `Failed to resolve assembly` 后整包被跳过；
- 还要手工引 `smapi-internal\SMAPI.Toolkit.CoreInterfaces.dll` 与 `SMAPI.Toolkit.dll`（`IManifest` 在那儿）。

### 为什么虚拟输入必须用 Harmony

反编译 `Game1.Update` 的顺序是：

```
currentMouseState = input.GetMouseState();   // 真·操作系统状态
active_menu.update(gameTime);                // 菜单读的是 oldMouseState（上一帧的值）
oldMouseState = currentMouseState;           // 菜单跑完才回写
```

所以「在 pre-Update 钩子里改 `Game1.oldMouseState`」会被最后一句覆盖，菜单看不到。
正确做法是补 `InputState.GetMouseState()` / `GetKeyboardState()`（两个前缀），
把合成出来的状态直接返回给游戏 —— 之后 `oldMouseState` 自然会跟着变。

## 端点

### M1

| 端点 | 说明 |
|---|---|
| `GET /status` | 版本、端口、帧计数、是否进档、输入观测值 |
| `GET /state` | 玩家 / 地点 / 时间 / 菜单 / 钓鱼 / 输入的当前快照 |

### M2

| 端点 | body | 说明 |
|---|---|---|
| `POST /input/hold` | `{"button":"use","ms":200}` 或 `{"buttons":["w","left"],"ms":300}` | 按住若干键/鼠标键，到时自动松开 |
| `POST /input/release` | — | 立刻全部松开 |
| `GET /input/state` | — | 当前按住什么、还剩多少毫秒、游戏实际读到的输入 |

语义按钮：`use`（= 鼠标左键，游戏里"使用工具"的默认绑定之一）、`mouse_left`、`mouse_right`，
其余按键盘名给：`w a s d up down left right space c x e`。

### M3 探针

| 端点 | 说明 |
|---|---|
| `GET /probe/active_menu` | 把当前菜单摊平：`type` / `fields`（标量全取，坐标与矩形展开）/ `components`（可点击控件的 name+bounds） |

改坏之前先在真机验过：`GameMenu` 返回 `currentTab`、`xPositionOnScreen=200`、`width=880` 等 11 个字段。

### M4 动作

| 端点 | body | 说明 |
|---|---|---|
| `POST /walk_to` | `{"x":5,"y":6,"timeoutMs":12000}` | BFS 求路径（用游戏自己的 `isTilePassable`），再一帧走一格。7 格实测 0.75s |
| `POST /face` | `{"direction":"down"}` | 转向，回读面前那格 |
| `POST /use`（别名 `/tool`） | `{}` | 挥一次手里的工具。**等到生效帧（40 帧≈660ms）再回读目标格** |
| `POST /move` | `{"x":..,"y":..}` | 和 `walk_to` 同语义（寻路走过去），兼容老客户端叫法 |
| `POST /stop` | `{}` | 立刻中止当前跨帧操作，**并把等待中的请求当场结账** |
| `POST /warp` | `{"location":"Farm","x":40,"y":32}` | 跨图传送；落点先过目标地图的 `isTilePassable`，**并等位置真的变了才返回** |
| `POST /interact` | `{}` | 对面前那格 `checkAction`（开门/说话/开箱都走这里） |
| `GET /menu` | — | 菜单摘要：类型 / 对话正文 / 选项 / 探针全量 |
| `POST /menu/click` | `{"component":"name"}` 或 `{"x":..,"y":..}` | 按控件名或坐标点菜单 |
| `POST /menu_close` | — | 关菜单（**钓鱼小游戏也是菜单，关掉＝放跑那条鱼**） |

### M5 世界

| 端点 | body | 说明 |
|---|---|---|
| `GET /inventory` | — | 背包全量（slot / name / count / category / quality / isTool）+ `currentSlot` |
| `POST /select` | `{"name":"Axe"}` 或 `{"slot":2}` | 选中物品；超过第 12 格会先和第 0 格换位 |
| `GET /chest` | — | 读面前箱子的内容 |
| `POST /chest_take` | `{"slot":0,"count":0}` | 从面前箱子取一格（0=整叠）；背包放不下会原样放回 |
| `POST /store` | `{"name":"Axe"}` 或 `{"slot":3}` | 把背包整叠放进面前箱子（只做整叠进空格，不做部分合并） |
| `POST /sleep` | `{"stay":true}` | 就地睡觉：走游戏自己的 `answerDialogueAction("Sleep_Yes")`，会真实跨日 + 存档 |

## 目录

```
JunimoBridge.fsproj   net6.0；直引游戏 dll（Private=false）与 smapi-internal 的三个 dll
Json.fs               手写 JSON（值类型 + 渲染 + 解析）
Runtime.fs            版本/端口/帧计数等全局小状态
Frame.fs              主线程工作队列（post / drain / pending）
Input.fs              虚拟输入：状态 + 两个 Harmony 前缀 + 到期自动释放
Snapshot.fs           /status 与 /state 的 JSON 组装
Server.fs             HttpListener（localhost:7850，keep-alive）
Mod.fs                SMAPI 入口：起服务、打补丁、每帧排空队列
build.sh              ./build.sh build | install
```

## 构建

```bash
./build.sh build     # 产物在 bin/native/
./build.sh install   # 编译并拷进 Mods/JunimoBridge/（含 FSharp.Core.dll）
```

SDK 装在 `E:\dotnet`（8.0.427）。目标框架必须是 `net6.0` —— 游戏自带 coreclr 6.0.32。

## 里程碑

| 步 | 内容 | 状态 |
|---|---|---|
| M1 | Server + `/status` `/state` + Frame | 完成（真机验过） |
| M2 | Input（UpdateStates 补丁）+ `/input/hold` | 完成（真机验过） |
| M3 | Probe（菜单字段反射）+ `/probe/active_menu` | 完成（真机验过） |
| M4 | Actions（walk_to/face/use/interact/menu） | 完成（真机验过） |
| M5 | World（背包 / 选择 / 箱子读+取） | 完成（真机验过） |
| M6 | 欠账第一批：`/move` `/stop` `/warp` `/sleep` `/store` + GameMenu 控件 | 完成（真机验过） |
| M7 | **补齐客户端用到的 38 个端点 + 对齐 NagiBridge 报文形状** | 完成（真机验过） |

### M1+M2 的真机验收（2026-11-03）

`./test/smoke.sh` 10 项全过：

```
{"ok":true,"mod":"JunimoBridge","version":"0.1.0","api":"4.5.2.0","port":7850,
 "worldReady":true,"tick":11641,"uptimeMs":201128,"pending":0,...}

POST /input/hold {"button":"use","ms":1200}
  → {"holding":true,"msLeft":747,"observedLeft":true}      ← 游戏真的读到了按住
  → 到期后 {"holding":false,"observedLeft":false}

POST /input/hold {"buttons":["w"],"ms":800}
  → {"observedKeys":"W"}                                    ← 键盘注入也通
```

`/state` 在存档里返回：`player.name=DeepSeek`、`location=FarmHouse 12×12`、
`time=fall 23 年 1 06:10`、`fishing=null`（手里拿斧头）。

### 虚拟输入的最终实现（换过一次方案）

F# 不允许给 byref 参数赋值（`FS0027`），所以 Harmony 的 `ref __result` 写不进去。
改成补 **`InputState.UpdateStates()` 的 postfix**：它每帧在 `Game1.Update` 第一行被调用，
把真实设备状态存进 `_currentMouseState` / `_currentKeyboardState` 两个私有字段；
postfix 用反射改写这两个字段即可，发生在所有游戏逻辑之前，零帧延迟，也不需要 Unsafe。

## 与 NagiBridge 共存

两个 mod 各监听自己的端口，互不干扰。客户端通过 `JUNIMO_BACKEND=nagibridge|own|hybrid`
按域路由，迁移过程可以随时停在任何一步。

## 兼容层（M7）：客户端能直接切过来

**结论：客户端 `game_api.ml` 用到的 38 个端点全部实现，`/state` 等只读报文按位对齐。**

| 类别 | 端点 |
|---|---|
| 只读 | `status` `state` `map` `surroundings` `scan_chests` `friendship` `find_npc` `menu` `festival` |
| 移动 | `move` `walk_to`（带 location / 跨图前自动 warp）`stop` `position` `warp` `face`（吃整数或字符串） |
| 动作 | `tool` `use` `interact` `key` `pause` `resume` `refill` `eat` `harvest` `sleep` |
| 物品 | `select` `buy` `sell` `gift` `inventory` |
| 箱子 | `chest` `store` `chest_take` `chest_take_list`（都按 x/y 定位，和 NagiBridge 一致） |
| 农活 | `tool_area`（矩形批量锄/浇，逐格挥） |
| 菜单/节日 | `menu/click`（含 option）`menu_close` `festival/interact` `festival/answer` |
| 我们独有 | `probe/active_menu` `input/hold` `input/release` `input/state` |

### 形状对齐的证明（同一次开机，两个 mod 各跑一遍）

```
NagiBridge /map    {"ok":true,"player":{"x":9,"y":9},"location":{"name":"FarmHouse","width":12,"height":12},"buildings":[],"warps":[{"x":3,"y":12,"targetLocation":"Farm","targetX":64,"targetY":15}],...}
JunimoBridge /map  {"ok":true,"player":{"x":9,"y":9},"location":{"name":"FarmHouse","width":12,"height":12},"buildings":[],"warps":[{"x":3,"y":12,"targetLocation":"Farm","targetX":64,"targetY":15}],...}

/find_npc?name=Abigail 两边逐字段相同（含 displayName「阿比盖尔」与当前台词）
/state 的 player/location/time 键名与 NagiBridge 一致；inventory 里的 category 用游戏自己的
        getCategoryName()，所以「工具/种子/资源」这些中文串天然对齐
```

### 行为差别：这一轮修掉的

| 原来的差别 | 修法 |
|---|---|
| `/tool_area` 只挥不落地（FarmHouse 里 `swings:4` 其实一刀没落） | 三重修复：只认 `"Diggable"` 属性、**先把锄头/水壶选到手上**、**先站到目标格四邻再面向它**。现在 2 格 1 秒、`still_missing: []` |
| `/gift` 只说「成功」 | 加口味评价：`getGiftTasteForThisItem` → love / like / neutral / dislike / hate，再 `receiveGift` 真送 |
| `/festival/answer` 靠数字键，点不中 | 对话类菜单走 `selectedResponse = n` + `Enter`（游戏自己的键盘选法），失败才退数字键与控件点击 |
| `isInBed` 残留 true | 读档后 30 帧内清一次；`/sleep` 后 15 秒再清一次 |

**试过但放弃的方案**：用虚拟输入按住"使用"键 0.7s，让游戏自己蓄力挥一片。
真机结果：60 秒里一格没锄出来——没有连续真实输入时游戏会中途取消这次使用。
所以 `tool_area` 保持「逐格 Begin/End + 38 帧等生效帧」，慢但确定。

### 真的还差什么

| 差别 | 影响 |
|---|---|
| `tool_area` 一格一挥（约 0.66s/格），上游是蓄力一次一片 | 矩形越大越慢，结果一致 |
| 节日问答仍是启发式（`selectedResponse`） | 没真节日时验不了 |
| `status.caps` 是空对象 | 客户端不读 |

## 真机验收（2026-11-03 深夜，存档 Fall 23）## 真机验收（2026-11-03 深夜，存档 Fall 23）## 真机验收（2026-11-03 深夜，存档 Fall 23）

`./test/smoke.sh` 19 项全过。几条关键输出：

```
/probe/active_menu  → {"open":true,"menu":{"type":"GameMenu","fields":{"currentTab":0,
                       "xPositionOnScreen":200,"yPositionOnScreen":20,"width":880,"height":680,...}}}
/inventory          → 11 件，slot 0 = Axe
/face {"direction":"down"} → target {x:5,y:7,object:"",hasTerrainFeature:false}
/use  {}            → {"frames":40,"stillUsing":false,"target":{...}}     ← 等满 40 帧才回读
/walk_to {5,6}      → {"arrived":true,"steps":7}  实测 0.75s
/menu_close         → {"closed":"GameMenu"}
```

`/chest` 的失败路径也验了：面前不是箱子时返回 `{"ok":false,"error":"(x,y) 上不是箱子"}`。

## M6 硬测记录（2026-11-03 凌晨，新档「测试用」spring 1 年）

`./test/smoke.sh` **24 项全过**。这一轮真机抓到两个设计问题，都修了：

1. **`/warp` 没等落地**。`Game1.warpFarmer` 只是开始过场，下一帧 `currentLocation` 还没换；
   紧接着调 `walk_to` 会拿**旧地图**算路径 —— 实测从 Farm 传送后 BFS 用 12×12 的 FarmHouse
   算出「2 步到 (10,10)」，人其实没动。现在 warp 会等到 `player.currentLocation.Name` 变了才返回
   （实测 `frames:39` ≈ 0.65s）。
2. **`/stop` 不结账**。原来只清队列不完成 TCS，被中止的 `walk_to` 会一直傻等到自己的超时
   （实测 `stop` 之后又等了 25s 才回「没完成」）。现在 abort 立刻回
   `{"ok":false,"aborted":true,"error":"被 /stop 中止"}`（实测 0.3s 内返回）。

功能验证（都是真机跑出来的，不是"应该能行"）：

```
/select {"name":"Hoe"} → slot:1
/face down + /use      → target {x:22,y:11,hasTerrainFeature:true}    ← 真的锄出了地
                          同时 stamina 270→268
/warp Farm x40 y32     → frames:39，随后 /state 的 location=Farm
/walk_to 10,10         → steps:52，4.4s，/state 坐标一致
/stop（走位途中）      → aborted:true，人停在 (22,10) 不再动
/sleep {"stay":true}   → day 1 → 2，timeOfDay 600，体力回满 270，位置回到床边
/probe/active_menu     → GameMenu 钻到 pages 里，捞出 167 个控件
```

## 为什么没有 CI

构建需要 `Stardew Valley.dll` / `StardewModdingAPI.dll` / `xTile.dll` 这些**游戏本体文件**，
它们不能进仓库。所以这里没有 GitHub Actions：真正的回归靠 `./test/smoke.sh`（需要一台装了游戏、
开着 Mod 的机器）。

静态可跑的部分（JSON、协议、F# 模块编译）已经在本地 `build.sh` 里覆盖。
