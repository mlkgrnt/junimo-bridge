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
        | "escape"
        | "esc"
        | "menu" -> Some Keys.Escape // SDV 里 Esc 就是"开/关菜单"
        | "enter" -> Some Keys.Enter
        | "tab" -> Some Keys.Tab
        | "backspace" -> Some Keys.Back
        | "q" -> Some Keys.Q
        | "f" -> Some Keys.F
        | "0" -> Some Keys.D0
        | "1" -> Some Keys.D1
        | "2" -> Some Keys.D2
        | "3" -> Some Keys.D3
        | "4" -> Some Keys.D4
        | "5" -> Some Keys.D5
        | "6" -> Some Keys.D6
        | "7" -> Some Keys.D7
        | "8" -> Some Keys.D8
        | "9" -> Some Keys.D9
        | "f1" -> Some Keys.F1
        | "f2" -> Some Keys.F2
        | "f3" -> Some Keys.F3
        | "f4" -> Some Keys.F4
        | "f5" -> Some Keys.F5
        | "f6" -> Some Keys.F6
        | "f7" -> Some Keys.F7
        | "f8" -> Some Keys.F8
        | "f9" -> Some Keys.F9
        | "f10" -> Some Keys.F10
        | "f11" -> Some Keys.F11
        | "f12" -> Some Keys.F12
        | _ -> None

    /// 游戏里各按钮**实际**绑定了哪些键（主线程定期刷新）。
    ///
    /// ⚠️ 这是「方向键按了没反应」的根因：SDV 默认的 `moveXButton` **只绑 W/A/S/D**，
    /// 箭头键压根没绑。`Game1.UpdateControlInput` 只查 `options.moveXButton`，
    /// 所以注入 `Keys.Up` 等于按空气 —— 连朝向都不会变（朝向也是从同一批布尔值推的）。
    /// 因此语义名必须解析成**当前绑定**，不能硬编码成箭头键。
    /// 反过来说：用户改键位后这条也能自动跟上。
    let mutable private buttonBindings: (string * Keys list) list = []
    let mutable private bindingAge = 0

    /// 语义按钮名 → 实际要按的键（up/down/left/right、menu、cancel）。
    let resolvedButtonKeys (name: string) =
        match buttonBindings |> List.tryFind (fun (n, _) -> n = name) with
        | Some(_, ks) when not ks.IsEmpty -> ks
        | _ ->
            // 主线程还没采到 / 该按钮绑在鼠标上 —— 退回历史硬编码值。
            match keyOf name with
            | Some k -> [ k ]
            | None ->
                match name with
                | "cancel"
                | "menu" -> [ Keys.Escape ]
                | _ -> []

    /// 语义方向（up/down/left/right）→ 实际要按的键。
    let resolvedMoveKeys (dir: string) = resolvedButtonKeys dir

    /// 这个键是不是"按住才走"的移动键（决定默认按多久）。
    let isMoveKey (name: string) =
        match name.Trim().ToLowerInvariant() with
        | "w" | "a" | "s" | "d" | "up" | "down" | "left" | "right" -> true
        | _ -> false

    /// 键名能不能注入（给 `/key` 用：不认识的键要**如实报错**，别静默 ok）。
    let canInject (name: string) =
        let n = name.Trim().ToLowerInvariant()

        match n with
        | "up"
        | "down"
        | "left"
        | "right"
        | "cancel" -> true
        | n -> (keyOf n).IsSome

    /// 语义按钮 → 实际按住的键。`use` 就是「使用工具」的默认绑定（鼠标左键）。
    /// 方向名 / cancel 走绑定表（跟随游戏内键位），其余按字面键名。
    let keysFor (buttons: string list) =
        let mouseLeft =
            buttons
            |> List.exists (fun b ->
                let n = b.Trim().ToLowerInvariant()
                n = "use" || n = "mouse_left" || n = "left_click")

        let mouseRight =
            buttons |> List.exists (fun b -> b.Trim().ToLowerInvariant() = "mouse_right")

        let keys =
            buttons
            |> List.collect (fun b ->
                let n = b.Trim().ToLowerInvariant()

                match n with
                | "up"
                | "down"
                | "left"
                | "right"
                | "cancel"
                | "menu"
                | "esc"
                | "escape" -> resolvedButtonKeys(if n = "esc" || n = "escape" then "menu" else n)
                | _ ->
                    match keyOf n with
                    | Some k -> [ k ]
                    | None -> [])
            |> List.distinct
            |> List.truncate 8

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

    /// 主线程定期刷新「移动键实际绑定」。
    /// 放在主线程是因为 `Game1.options` 归主线程所有；每 20 帧读一次足够跟上改键位。
    let private refreshBindings () =
        bindingAge <- bindingAge + 1

        if bindingAge >= 20 || buttonBindings.IsEmpty then
            bindingAge <- 0

            try
                let keysOf (arr: StardewValley.InputButton[]) =
                    if isNull arr then
                        []
                    else
                        arr
                        |> Array.filter (fun b -> not (isNull (box b)) && b.key <> Keys.None)
                        |> Array.map (fun b -> b.key)
                        |> Array.toList

                let o = StardewValley.Game1.options

                buttonBindings <-
                    [ "up", keysOf o.moveUpButton
                      "down", keysOf o.moveDownButton
                      "left", keysOf o.moveLeftButton
                      "right", keysOf o.moveRightButton
                      "menu", keysOf o.menuButton
                      "cancel", keysOf o.cancelButton ]
            with _ ->
                ()

    /// 主线程每帧调用：到期释放 + 采样「游戏现在读到的输入」。
    let tick () =
        let s = current ()

        if s.Deadline > 0L && now () >= s.Deadline then
            release ()

        refreshBindings ()

        try
            let ms = StardewValley.Game1.input.GetMouseState()
            observedLeft <- ms.LeftButton = ButtonState.Pressed
            let pressed = StardewValley.Game1.input.GetKeyboardState().GetPressedKeys()

            observedKeys <-
                if isNull pressed || pressed.Length = 0 then ""
                else pressed |> Array.map string |> String.concat ","
        with _ ->
            ()
