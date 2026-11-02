namespace JunimoBridge

open System
open System.Threading
open Microsoft.Xna.Framework.Input

/// 虚拟输入。
///
/// 两条路都试过：
///  1. Win32 `keybd_event`：会抢焦点，而且 NagiBridge 把它放在主线程回调里 sleep，
///     等于把游戏循环冻住 —— 松开发生在「没有任何帧」的间隙，游戏看不到。
///  2. 补 `InputState.GetMouseState/GetKeyboardState`：返回合成状态，游戏正常跑帧，
///     菜单 / 小游戏 / 世界输入一次全覆盖，不抢焦点、不依赖用户键位。这里用第 2 种。
module Input =

    type Hold =
        { MouseLeft: bool
          MouseRight: bool
          Keys: Keys list
          Deadline: int64 }

    let private empty =
        { MouseLeft = false
          MouseRight = false
          Keys = []
          Deadline = 0L }

    let private gate = obj ()
    let mutable private state = empty

    /// 观测值：主线程每帧读一次「游戏实际拿到的输入」，
    /// /state 里能看到 —— 这是补丁生效的直接证据（不用真去钓鱼也能验）。
    let mutable observedLeft = false
    let mutable observedKeys = ""

    let private now () = Environment.TickCount64

    let private keyOf (name: string) =
        match name.Trim().ToLowerInvariant() with
        | "w" -> Some Keys.W
        | "a" -> Some Keys.A
        | "s" -> Some Keys.S
        | "d" -> Some Keys.D
        | "up" -> Some Keys.Up
        | "down" -> Some Keys.Down
        | "left" -> Some Keys.Left
        | "right" -> Some Keys.Right
        | "space" -> Some Keys.Space
        | "c" -> Some Keys.C
        | "x" -> Some Keys.X
        | "e" -> Some Keys.E
        | "escape" -> Some Keys.Escape
        | "enter" -> Some Keys.Enter
        | _ -> None

    /// 语义按钮 → 实际按住的键。`use` 就是「使用工具」的默认绑定（鼠标左键）。
    let keysFor (buttons: string list) =
        let mouseLeft =
            buttons
            |> List.exists (fun b ->
                let n = b.Trim().ToLowerInvariant()
                n = "use" || n = "mouse_left" || n = "left_click")

        let mouseRight =
            buttons |> List.exists (fun b -> b.Trim().ToLowerInvariant() = "mouse_right")

        let keys = buttons |> List.choose keyOf |> List.distinct |> List.truncate 8
        mouseLeft, mouseRight, keys

    /// 按住一组按钮，`ms` 毫秒后由主线程自动松开（不 sleep）。
    let hold (buttons: string list) (ms: int) =
        let ms = max 1 (min 10000 ms)
        let mouseLeft, mouseRight, keys = keysFor buttons

        lock gate (fun () ->
            state <-
                { MouseLeft = mouseLeft
                  MouseRight = mouseRight
                  Keys = keys
                  Deadline = now () + int64 ms })

        mouseLeft, mouseRight, keys, ms

    /// 立刻全部松开。
    let release () = lock gate (fun () -> state <- empty)

    let current () = lock gate (fun () -> state)

    let remainingMs () =
        let s = current ()
        if s.Deadline = 0L then 0 else max 0 (int (s.Deadline - now ()))

    /// 主线程每帧调用：到期释放 + 采样「游戏现在读到的输入」。
    let tick () =
        let s = current ()

        if s.Deadline > 0L && now () >= s.Deadline then
            release ()

        try
            let ms = StardewValley.Game1.input.GetMouseState()
            observedLeft <- ms.LeftButton = ButtonState.Pressed
            let pressed = StardewValley.Game1.input.GetKeyboardState().GetPressedKeys()

            observedKeys <-
                if isNull pressed || pressed.Length = 0 then ""
                else pressed |> Array.map string |> String.concat ","
        with _ ->
            ()
