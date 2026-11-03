namespace JunimoBridge

open System

/// 一丁点全局运行时状态：只放「谁都要看、谁都不改结构」的标量。
/// 真正的状态在游戏里；这里只是给 /status 自检用。
module Runtime =

    /// 本 mod 版本（Entry 时从 manifest 读）
    let mutable modVersion = "0.0.0"

    /// SMAPI API 版本
    let mutable apiVersion = "?"

    /// 监听端口
    let mutable port = 7850

    /// 启动时刻（UTC），用来算 uptime
    let mutable startedAt = DateTime.UtcNow

    /// UpdateTicking 的累计帧数。60fps 下约等于「已运行的秒数 × 60」。
    let mutable tick = 0L

    /// `/pause` 的冻结点：冻结期间每帧把 timeOfDay 按回去（mod 侧冻结，不动游戏的 paused 标志）
    let mutable timeFrozen = false
    let mutable frozenAt = 0

    /// 读档后要清 isInBed 的倒计时（我们 /sleep 手工置过位，游戏醒来不一定清）
    let mutable wakePending = 0

    /// 「按掉下一发工具落地」标记。
    ///
    /// 我们自己的 `Tool.DoFunction` 是**权威**的一发（范围/落点都算过）；紧跟着
    /// `BeginUsingTool()` 放出的挥舞动画在 frame68 还会自己再落一发（固定"面前 1 格"）。
    /// 那一发在锄地上是 no-op，但**照样吃体力**；浇水上则真的再浇一次、再扣水
    /// （真机量过：一挥手掉 2 格水、4 点体力，正好翻倍）。
    /// ⇒ 标一下，让补丁把**下一发**按掉，按完即解除（绝不长期劫持）。
    let mutable suppressToolUse = false
    let mutable suppressTicks = 0

    /// 注入落地计数：补丁**真的改写**了 `_currentKeyboardState` / `_currentMouseState` 的次数。
    /// 没有这两个数就分不清"注入没生效"和"注入了但游戏不认"——真机上就是卡在这一步。
    let mutable injectedKeys = 0
    let mutable injectedMouse = 0

    let uptimeMs () = int64 (DateTime.UtcNow - startedAt).TotalMilliseconds
