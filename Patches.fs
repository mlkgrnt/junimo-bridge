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
