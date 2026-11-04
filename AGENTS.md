# AGENTS.md — JunimoBridge 开发与验收指南

> 面向 AI Agent：改这个仓库前先读完。面向人类的完整说明在 [`README.md`](README.md)，
> 逐版本变更在 `release/notes-v*.md`。本文件只写「动手时必须知道」和「容易翻车」的部分。

## 这是什么

星露谷物语 SMAPI mod（**F# / net6.0**），在进程内开一个 `HttpListener`，监听
**`http://localhost:7850`**，把游戏内部状态与操作暴露成 HTTP 端点。它是
[Junimo-MCP](https://github.com/mlkgrnt/Junimo-MCP) 的默认执行器：

```
Junimo-MCP (OCaml, 41 个意图工具) ──HTTP 7850──> JunimoBridge (本仓库) ──> 游戏进程
```

- 报文形状与 NagiBridge-Remix（7842）对齐，**回执字段名 = 契约**，别自创名字。
- 只实现自己真正用到的端点，不追平上游（上游 174 个端点）。
- 许可 MIT；不打包任何上游 mod 代码。

## 环境与构建

```bash
export DOTNET=/e/dotnet/dotnet          # SDK 不在 PATH 里时指定；本机是这个路径
./build.sh build       # 只编译，产物 bin/native/
./build.sh install     # 编译并拷进 <GameDir>/Mods/JunimoBridge/
./build.sh package     # 编译并打 release/JunimoBridge.zip + .sha256
```

- `<GameDir>` 在 `JunimoBridge.fsproj`，默认 `D:\SteamLibrary\steamapps\common\Stardew Valley`。
- 编译需要游戏本体的 `Stardew Valley.dll` / `StardewModdingAPI.dll` / `xTile.dll`
  与 `smapi-internal` 里的 `SMAPI.Toolkit*.dll`、`0Harmony.dll` ⇒ **CI 不构建 mod**，
  只跑 `test/json` 断言、脚本 shellcheck、manifest 与 fsproj 编译顺序校验。
- ⚠️ `./build.sh install` 前**先关掉游戏**：否则 `JunimoBridge.dll` 被占用，报
  `Device or resource busy`。

## 六条铁律（违反必翻车）

1. **主线程永不 sleep。** 等待一律写成「每帧检查谓词」（`Frame.post` / `Ops` 的 step 闭包），
   HTTP 线程只投递任务并等结果。上游 `/key hold` 在主线程 `Thread.Sleep` 是反面教材。
2. **动作 = 操作 + 后置条件。** 挥工具要等生效帧（约 550–660ms）过去，再**回读地块到终态**
   才返回 `ok`。发出去就报成功 = 假成功。
3. **虚拟输入只补 `InputState.UpdateStates`**（写 `_currentMouseState` /
   `_currentKeyboardState`），不要改 `oldMouseState` / `oldKBState`（会抹掉「刚按下」边沿），
   不要靠 Win32 `keybd_event` 注入。
4. **零第三方依赖。** JSON 用 `Json.fs` 手写（别引 `System.Text.Json`，会与游戏自带版本打架），
   HTTP 用 BCL `HttpListener`，Harmony 用 SMAPI 自带那份。
5. **`FSharp.Core.dll` 必须随包**（`CopyLocalLockFileAssemblies=true`）；缺了 SMAPI 会整包跳过。
6. **新增 `.fs` 必须同时加进 `JunimoBridge.fsproj` 的 `<Compile Include>`**（F# 编译顺序敏感，
   CI 会逐个文件检查是否漏引）。

## 新增端点的清单

1. 在对应模块实现（只读 → `Compat.fs` / `Probe.fs`；动作 → `Actions.fs` / `Extra.fs`；
   子系统 → 新建 `Xxx.fs`）；
2. `Server.fs` 加路由；需要每帧 tick 的在 `Mod.fs` 的 `UpdateTicking` 里挂上；
3. 回执字段名照上游抄，null 值要兜（`NetString.Value` 未设过是 **null**，背包空格也是）；
4. `test/smoke.sh` 加断言（`test/ref/` 放采样报文）；
5. `README.md` 的端点表 + 目录树同步；
6. 改行为 ⇒ 真机复核 + smoke 全绿；涉及 MCP 工具语义的还要同步 Junimo-MCP 的 mock 断言。

## 硬结论（别重新踩）

- **工具落地必须自己调 `Tool.DoFunction`**，再按掉动画那一发（见 `Patches.fs` 的
  `SuppressSecondLandingPatch`）。
- `/position` 是瞬移，读坐标用 `/state`；`/warp` 后要等位置稳定 **3 帧**；
  `/click` 的难点是**淡入时机**不是坐标；`TitleMenu.subMenu` 是 **static**。
- `/key` 语义名要解析到 `Game1.options` 实际绑定：方向 → W/S/A/D（**箭头键默认没绑**）、
  menu → E/Escape。`GET /debug/move` 一次看全 `CanMove` / 碰撞 / 绑定。
- **焦点/后台**：`GET /debug/focus` 看现状，`POST /background` 开关。正解是每帧 pin
  **`GameRunner.instance.Platform._isActive`**（`Background.fs`）；别 pin `Game1.game1`；
  给两三行 IL 的小 getter 打 Harmony 会被 **JIT 内联绕过**；关开关时要主动把字段还原成
  真实前台状态。别拿 `Game1.IsActive` 判焦点（可能被 pin 成恒 true）。
- **钓鱼**：判定抛竿只认 `isFishing`；别手动设 `rod.hit`；别调 `BeginUsingTool()`；
  提竿直接调 `DoFunction`；别清 `UsingTool` / `pullingOutOfWater` / `fishCaught`。
  收线交付靠 `Fishing.tickFinish()` 每帧把 `UsingTool` 按回 true（收线 sprite 在
  **`FishingRod.animations`**，不在地图 temporarySprites）。
  **`reelOnce` 里绝不能 `forceCanMove()`**：casting 期 `CanMove=false` 是正常动画锁，
  强推会打断状态机，导致「连续第二竿空转」。`doneFishing(who,true)` 只 Fire 事件，不是解。
  回执分 `won` / `delivered` / `directCatch`，**只有 `delivered` 算成果**；产出垃圾是游戏特性。
- `ItemRegistry.Create` 对未知 id 返回 `"Error Item"` 而**不是 null**，调试端点要显式拒。
- 编译坑：`helper.Monitor` 在 SMAPI 4 已删 ⇒ 用 `this.Monitor`；F# 写 Harmony 用静态成员
  + `[<HarmonyPatch>]`。

## 验收

```bash
dotnet run --project test/json     # 不需要游戏，CI 跑这个
./test/smoke.sh                   # 需要游戏在跑，59 项断言
./test/load_save.sh               # 三步进档：/focus → /click Load → /click 存档槽
curl -s --noproxy '*' http://localhost:7850/status
```

- `curl` 一律 `--noproxy '*'`；需要 Content-Length 的端点用 `-d ''`；**用 `localhost` 不是
  `127.0.0.1`**（HTTP.sys 会拒）。
- smoke 在窗口**最小化**状态下也能跑完前 17 项（后台操控默认开）。
- 改过游戏语义的改动必须真机复核；只读改动至少跑 smoke。

## 发版格式（照旧，别改文件名）

1. 只改 `manifest.json` 的 `Version` —— `Runtime.modVersion` 由 `Mod.fs` 启动时从
   `this.ModManifest.Version` 读取，**不要手改 `Runtime.fs`**；
2. 写 `release/notes-vX.Y.Z.md`（中文一律用 UTF-8 文件传给 `--notes-file`）；
3. `./build.sh package` ⇒ `JunimoBridge.zip` + `JunimoBridge.zip.sha256`
   （`<hash>␣␣JunimoBridge.zip`，**两空格、不带 GNU 的 `*`**）；
4. `gh release create vX.Y.Z <两资产> --title "JunimoBridge vX.Y.Z" --notes-file <文件> --target main`；
5. **资产文件名不带版本号**，保住 `releases/latest/download/` 固定链接；
6. 三重一致性：本地 hash == release digest == 公开下载实测 hash。

发版后同步更新 `README.md` 的版本示例、端点表与测试数，
以及 Junimo-MCP / dsh-junimo 两仓库里引用到的执行器版本号。
