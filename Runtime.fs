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

    let uptimeMs () = int64 (DateTime.UtcNow - startedAt).TotalMilliseconds
