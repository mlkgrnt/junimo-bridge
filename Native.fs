namespace JunimoBridge

open System
open System.Runtime.InteropServices

/// 一点点 Win32。只为一件事：把游戏窗口顶到前台。
///
/// 为什么不靠 SMAPI：`Game1.game1.Activate()` 在「进程在后台」时不一定生效，
/// 而 SDV 默认 `pauseWhenOutOfFocus`，失焦时点击全部无效。
///
/// ⚠️ 这件事**比看上去难**，因为 Windows 有「前台锁」：只有当前前台进程（或它启动的进程）
/// 才能把别的窗口顶上去，后台进程调 `SetForegroundWindow` 会被**静默无视**（返回 false，
/// 什么都不发生）。真机踩过：光标明明停在 Load 按钮正中、矩形也算得进来，点下去一辈子没反应。
/// 所以这里按「代价从小到大」连试几种打法，哪种成哪种算。
module Native =

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool SetForegroundWindow(IntPtr hWnd)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool BringWindowToTop(IntPtr hWnd)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool ShowWindow(IntPtr hWnd, int nCmdShow)

    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()

    [<DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(IntPtr hWnd, uint32& lpdwProcessId)

    [<DllImport("user32.dll")>]
    extern bool AttachThreadInput(uint32 idAttach, uint32 idAttachTo, bool fAttach)

    [<DllImport("kernel32.dll")>]
    extern uint32 GetCurrentThreadId()

    /// 未公开但长期稳定：不受前台锁限制，效果约等于任务栏点一下。
    [<DllImport("user32.dll")>]
    extern void SwitchToThisWindow(IntPtr hWnd, bool fUnknown)

    [<DllImport("user32.dll")>]
    extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo)

    [<DllImport("user32.dll")>]
    extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)

    /// SW_RESTORE：最小化的窗口也能拉回来
    [<Literal>]
    let SW_RESTORE = 9

    [<Literal>]
    let private VK_MENU = 0x12uy

    /// 游戏那个**顶层窗口**的句柄。
    ///
    /// ⚠️ `Game1.game1.Window.Handle` 不一定是它：真机实测两者不同（SDL 内部窗口 vs 系统窗口），
    /// 拿错的那个去 `SetForegroundWindow` 永远失败、拿去比 `GetForegroundWindow` 永远不相等
    /// —— 于是 `/focus` 一直以为自己在后台。这里优先用进程的 MainWindowHandle。
    let gameHandle () : IntPtr =
        try
            let main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle

            if main <> IntPtr.Zero then
                main
            else
                let game = StardewValley.Game1.game1

                if isNull game || isNull (box game.Window) then IntPtr.Zero else game.Window.Handle
        with _ ->
            IntPtr.Zero

    let foregroundHandle () : IntPtr = try GetForegroundWindow () with _ -> IntPtr.Zero

    /// 当前**前台窗口**是不是游戏窗口。别用 `Game1.IsActive`：那个属性可能被别的 mod
    /// 补丁成恒定 true（NagiBridge 就打了 `Game.IsActive` 的 Harmony），拿它判会得到
    /// 「永远已在前台」—— 于是 `/focus` 一次也不重试、之后所有点击落空，还不报错。
    /// （`GetForegroundWindow` 也可能是 SMAPI 的控制台窗口 ⇒ 必须**比句柄**，不能只看非零。）
    let isForeground () : bool =
        let handle = gameHandle ()
        handle <> IntPtr.Zero && foregroundHandle () = handle

    /// 把游戏窗口顶到前台，返回**哪一招生效**（失败也如实说）。
    ///
    /// 顺序有讲究，前面的便宜且无副作用，后面的会碰键盘：
    ///   1. `SetForegroundWindow` —— 游戏本来就是前台时一发就中；
    ///   2. `AttachThreadInput` 借方身份 —— 通用做法，多数情况够用；
    ///   3. 敲一下 ALT 再 Set —— ALT 会解锁前台锁，是文档里没写但一直有效的口子；
    ///   4. `SwitchToThisWindow` —— 不受前台锁管，约等于任务栏点一下；
    ///   5. `SetWindowPos` 置顶 —— 最后手段：至少让人**看得见**（不保证能吃键鼠）。
    let focusAttempt () : string =
        try
            let handle = gameHandle ()

            if handle = IntPtr.Zero then
                "拿不到窗口句柄"
            elif isForeground () then
                "本来就在前台"
            else
                // 不判返回值：窗口已经是前台时 SetForegroundWindow 照样可能回 0。
                ShowWindow(handle, SW_RESTORE) |> ignore
                BringWindowToTop handle |> ignore
                SetForegroundWindow handle |> ignore

                if isForeground () then
                    "SetForegroundWindow"
                else
                    let fg = GetForegroundWindow()
                    let mutable pid = 0u
                    let fgThread = if fg = IntPtr.Zero then 0u else GetWindowThreadProcessId(fg, &pid)
                    let self = GetCurrentThreadId()

                    let attached =
                        fgThread <> 0u && fgThread <> self && AttachThreadInput(self, fgThread, true)

                    SetForegroundWindow handle |> ignore

                    if attached then
                        AttachThreadInput(self, fgThread, false) |> ignore

                    if isForeground () then
                        "AttachThreadInput"
                    else
                        // ALT 一按一松：解锁前台锁。不是为了输入什么，纯粹是让下一发能成。
                        keybd_event (VK_MENU, 0uy, 0u, UIntPtr.Zero)
                        keybd_event (VK_MENU, 0uy, 2u, UIntPtr.Zero)
                        SetForegroundWindow handle |> ignore

                        if isForeground () then
                            "ALT+SetForegroundWindow"
                        else
                            SwitchToThisWindow(handle, true)

                            if isForeground () then
                                "SwitchToThisWindow"
                            else
                                // HWND_TOPMOST = -1、HWND_NOTOPMOST = -2；
                                // SWP_NOSIZE=1 SWP_NOMOVE=2 SWP_NOACTIVATE=16
                                SetWindowPos(handle, IntPtr(-1), 0, 0, 0, 0, 0x13u) |> ignore
                                SetWindowPos(handle, IntPtr(-2), 0, 0, 0, 0, 0x13u) |> ignore
                                "全部招数试过，仍不是前台"
        with ex ->
            ex.Message
