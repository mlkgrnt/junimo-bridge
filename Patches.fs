namespace JunimoBridge

open System.Reflection
open HarmonyLib

/// 虚拟输入的 Harmony 补丁。
///
/// 为什么补在 `InputState.UpdateStates()` 而不是别处：
/// 反编译 `Game1.Update` 的第一行就是 `input.UpdateStates();`，它把
/// `Keyboard.GetState()` / `Mouse.GetState()` 的结果存进 `_currentKeyboardState` /
/// `_currentMouseState` 两个私有字段；之后 `GetMouseState()` 只是把字段读出来。
/// 所以「UpdateStates 的 postfix 改写这两个字段」就等于从源头换掉这一帧的输入，
/// 且**发生在所有游戏逻辑之前**，菜单 / 小游戏 / 世界输入一视同仁。
///
/// 为什么不用更直接的写法：
///  - 改 `Game1.oldMouseState`：`Game1.Update` 在菜单跑完之后会重新赋值，覆盖我们的；
///  - 给 `GetMouseState` 写返回值的 prefix：要写 `__result`（byref 参数），F# 不允许给
///    byref 参数赋值（FS0027），硬写只能上 Unsafe；
///  - Win32 `keybd_event`：抢焦点，而且一旦在主线程 sleep 等按住时长，游戏那几百毫秒
///    一帧都不跑，松开时早就晚了（NagiBridge 的 `/key hold` 就是这个坑）。
module Patches =

    // 不 `open Microsoft.Xna.Framework.Input`：那个命名空间叫 `Input`，会把本项目的
    // `Input` 模块遮住。用类型缩写代替。
    type MState = Microsoft.Xna.Framework.Input.MouseState
    type KState = Microsoft.Xna.Framework.Input.KeyboardState
    type BState = Microsoft.Xna.Framework.Input.ButtonState

    [<HarmonyPatch(typeof<StardewValley.InputState>, "UpdateStates")>]
    type internal UpdateStatesPatch =
        static let flags = BindingFlags.NonPublic ||| BindingFlags.Instance

        static let mouseField =
            typeof<StardewValley.InputState>.GetField("_currentMouseState", flags)

        static let keyboardField =
            typeof<StardewValley.InputState>.GetField("_currentKeyboardState", flags)

        static let keyStateField =
            typeof<StardewValley.InputState>.GetField("_keyState", flags)

        static let lastKeyTickField =
            typeof<StardewValley.InputState>.GetField("_lastKeyStateTick", flags)

        [<HarmonyPostfix>]
        static member Postfix(__instance: StardewValley.InputState) : unit =
            let state = Input.current ()

            // 鼠标：只改按住的键，坐标/滚轮保留真值
            if state.MouseLeft || state.MouseRight then
                match mouseField with
                | null -> ()
                | field ->
                    let real = unbox<MState> (field.GetValue __instance)

                    let left =
                        if state.MouseLeft then BState.Pressed else real.LeftButton

                    let right =
                        if state.MouseRight then BState.Pressed else real.RightButton

                    let synthesized =
                        MState(
                            real.X,
                            real.Y,
                            real.ScrollWheelValue,
                            left,
                            real.MiddleButton,
                            right,
                            real.XButton1,
                            real.XButton2
                        )

                    field.SetValue(__instance, box synthesized)
                    Runtime.injectedMouse <- Runtime.injectedMouse + 1

            // 键盘：在真实按键之外**追加**（不吞真输入，用户还能自己操作）
            if not state.Keys.IsEmpty then
                match keyboardField with
                | null -> ()
                | field ->
                    let real = unbox<KState> (field.GetValue __instance)

                    let all =
                        (state.Keys @ (real.GetPressedKeys() |> Array.toList))
                        |> List.distinct
                        |> Array.ofList

                    field.SetValue(__instance, box (KState(all)))
                    Runtime.injectedKeys <- Runtime.injectedKeys + 1

                    // `GetKeyboardState()` 会按 `Game1.ticks` 缓存 `_keyState`，
                    // 中间还夹着 `_ignoredKeys` 等分支。只写 `_current*` 在失焦时
                    // 实测读不到（getter 被 JIT 内联，单独给它打 postfix 也没用），
                    // 所以把缓存一并写对：任何走缓存的路径都直接拿到合成状态。
                    match keyStateField, lastKeyTickField with
                    | null, _ | _, null -> ()
                    | kf, tf ->
                        try
                            // 对 `Nullable<T>` 字段，SetValue 传一个装箱的 T 即可
                            kf.SetValue(__instance, box (KState(all)))
                            tf.SetValue(__instance, box StardewValley.Game1.ticks)
                        with _ ->
                            ()

    /// 按掉「动画自带的那一发」工具落地。
    ///
    /// 背景：我们调 `BeginUsingTool()` 放挥舞动画（拟人观感要），动画自己在 frame68 会调一次
    /// `Tool.DoFunction`（固定"面前 1 格"）；而**范围/落点可控的那一发是我们自己调的**。
    /// 两发都落 = 浇水双扣水双扣体力（真机量过：一挥手水 -2、体力 -4，正好翻倍）。
    ///
    /// 为什么不按调用者分辨、只数次数：`Farmer.useTool` 那条路实测**不是**它的调用点
    /// （挂上去只等到"标记超时自动解除"），所以这里不猜调用栈，就按"下一发"。
    /// 只在标记置起时生效、生效即解除、外加 tick 超时兜底 —— **绝不长期劫持**。
    [<HarmonyPatch(typeof<StardewValley.Tool>, "DoFunction")>]
    type internal SuppressSecondLandingPatch =
        [<HarmonyPrefix>]
        static member Prefix(who: StardewValley.Farmer) : bool =
            if not Runtime.suppressToolUse then
                true
            elif not (obj.ReferenceEquals(who, StardewValley.Game1.player)) then
                true
            else
                Runtime.suppressToolUse <- false
                false

    /// 让 `Game1.HasKeyboardFocus()` 恒为真。
    ///
    /// 为什么必须补：`InputState.GetKeyboardState()` 开头就是
    ///   `if (!IsMainInstance()) return default;  if (!HasKeyboardFocus()) return default;`
    /// —— **没有键盘焦点时它直接返回空状态**，我们往 `_currentKeyboardState` 里注入什么都读不到。
    /// 而自动化跑的时候，焦点基本都在终端/编辑器上，所以：
    ///   ① 按住方向键只改朝向不走（朝向是别处直接算的，位移要读键盘）；
    ///   ② 注入 Esc 永远开不出菜单；
    ///   ③ `/input/state` 的 `observedKeys` 恒为空 —— 它采样的正是这个被闸掉的 getter。
    /// 这就是"能改朝向但不走"的真因。
    ///
    /// 只接管**键盘焦点这一个判据**，不动 `IsActive`/暂停逻辑；而且只有在真的没焦点时才被问到，
    /// 有焦点时返回值本来也是 true，等于对正常游玩无影响。
    [<HarmonyPatch(typeof<StardewValley.Game1>, "HasKeyboardFocus")>]
    type internal ForceKeyboardFocusPatch =
        [<HarmonyPostfix>]
        static member Postfix(__result: bool byref) : unit = __result <- true

    /// 让 `Game1.IsActiveNoOverlay` 恒为真 —— **失焦 / 最小化也能操控**的关键补丁。
    ///
    /// 为什么非补不可（真机对照 + IL）：
    ///  - `Game1._update` IL_11F6：`IsActiveNoOverlay` 为 false 且没有 inputSimulator 时
    ///    **整个 `UpdateControlInput` 被跳过** —— 注入的键进了 `InputState`，却没人消费。
    ///  - `Game1.updateActiveMenu` IL_001C：为 false 时走短路，菜单只 `update()`
    ///    **不读鼠标/键盘状态、不派发点击**。
    ///  - 实测：窗口最小化后 `press_key left` 连朝向都不变、`key menu` 开不出 GameMenu；
    ///    窗口一恢复，同样两发立刻生效。而 tick 与时间本来就在正常跑
    ///    （每 6s +422 帧、游戏内 10 分钟），说明**冻结的是输入消费，不是游戏循环**。
    ///
    /// 为什么补这个而不是 `Game.IsActive`：`IsActiveNoOverlay` 是 SDV 自己汇总的判据
    /// （`InstanceGame.IsActive && !sdk.HasOverlay`），改它一刀到位，且不动 XNA 的
    /// 窗口状态、不影响渲染 / 其它 mod 的焦点判断。NagiBridge 当年是补 `Game.IsActive`
    /// 才做到后台可操控的 —— 自研 mod 少了这一环，这就是差异。
    ///
    /// 可用 `Runtime.backgroundControl` 关掉（默认开）。
    [<HarmonyPatch(typeof<StardewValley.Game1>, "get_IsActiveNoOverlay")>]
    type internal ForceActiveNoOverlayPatch =
        [<HarmonyPostfix>]
        static member Postfix(__result: bool byref) : unit =
            if Runtime.backgroundControl then __result <- true

    /// 兜底：直接改两个输入 **getter 的返回值**。
    ///
    /// 只改 `UpdateStates` 的字段还不够 —— 真机实测窗口失焦时
    /// `injectedMouse` 一直涨（字段确实被写了）、`isActiveNoOverlay` 也被顶成 true，
    /// 可 `observedKeys` / `observedLeft` 还是空：要么 `GetKeyboardState` 的
    /// `_keyState` 按 `Game1.ticks` 缓存住了旧值，要么 getter 在失焦路径里被短路。
    /// 与其继续猜中间层，**在返回值这一层再兜一次**最稳：
    /// 游戏任何地方拿到的键鼠状态都一定含我们注入的键。
    /// 只在真的有注入状态时才动，正常游玩零开销。
    [<HarmonyPatch(typeof<StardewValley.InputState>, "GetKeyboardState")>]
    type internal ForceInjectedKeyboardPatch =
        [<HarmonyPostfix>]
        static member Postfix(__result: KState byref) : unit =
            let s = Input.current ()

            if not s.Keys.IsEmpty then
                let all =
                    (s.Keys @ (__result.GetPressedKeys() |> Array.toList))
                    |> List.distinct
                    |> Array.ofList

                __result <- KState(all)

    [<HarmonyPatch(typeof<StardewValley.InputState>, "GetMouseState")>]
    type internal ForceInjectedMousePatch =
        [<HarmonyPostfix>]
        static member Postfix(__result: MState byref) : unit =
            let s = Input.current ()

            if s.MouseLeft || s.MouseRight then
                let real = __result

                let left =
                    if s.MouseLeft then BState.Pressed else real.LeftButton

                let right =
                    if s.MouseRight then BState.Pressed else real.RightButton

                __result <-
                    MState(
                        real.X,
                        real.Y,
                        real.ScrollWheelValue,
                        left,
                        real.MiddleButton,
                        right,
                        real.XButton1,
                        real.XButton2
                    )
