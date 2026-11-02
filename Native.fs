namespace JunimoBridge

open System
open System.Runtime.InteropServices

/// 一点点 Win32。只为一件事：把游戏窗口顶到前台。
///
/// 为什么不靠 SMAPI：`Game1.game1.Activate()` 在「进程在后台」时不一定生效，
/// 而 SDV 默认 `pauseWhenOutOfFocus`，失焦时点击全部无效 —— 插件那边也是用
/// PowerShell + SetForegroundWindow 解决的，这里用同一个 API，少一次进程往返。
module Native =

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool SetForegroundWindow(IntPtr hWnd)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool BringWindowToTop(IntPtr hWnd)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool ShowWindow(IntPtr hWnd, int nCmdShow)

    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()

    /// SW_RESTORE：最小化的窗口也能拉回来
    [<Literal>]
    let SW_RESTORE = 9

    /// 发起一次"切前台"：只负责动作，不判断成败（判断交给调用方看 IsActive）。
    let focusAttempt () : string =
        try
            let game = StardewValley.Game1.game1

            if isNull game || isNull (box game.Window) then
                "游戏窗口还没创建"
            else
                let handle = game.Window.Handle

                if handle = IntPtr.Zero then
                    "拿不到窗口句柄"
                else
                    // 不判返回值：窗口已经是前台时 SetForegroundWindow 照样可能回 0；
                    // 而 GetForegroundWindow 也可能是 SMAPI 的控制台窗口，不是游戏窗口。
                    // 真正的判据是游戏自己的 IsActive（见 Actions.focus）。
                    ShowWindow(handle, SW_RESTORE) |> ignore
                    BringWindowToTop handle |> ignore
                    SetForegroundWindow handle |> ignore
                    game.Window.Title
        with ex ->
            ex.Message
