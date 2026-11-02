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
