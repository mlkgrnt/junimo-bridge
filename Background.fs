namespace JunimoBridge

open System
open System.Reflection

/// 让游戏在**失焦 / 最小化**时仍然认为自己是 active。
///
/// 为什么必须落到字段这一层：
///   `Game.get_IsActive()` → `GamePlatform.get_IsActive()` → `ldsfld _isActive`。
///   `Game1.get_IsActiveNoOverlay()` 又是通过 `InstanceGame.IsActive`（GameRunner.instance）拿它。
///   给这些小 getter 打 Harmony postfix **会被 JIT 内联绕过** —— 真机试过：
///   `/debug/focus` 里读到的 IsActiveNoOverlay 是 true（那是走补丁后的方法），
///   可 `_update` 内部照样按 false 走，输入依旧不消费。
///   字段是所有读取点（含内联副本）共同的数据源，改它一刀到位。
///
/// 影响面：`_update` 的两处 `IsActiveNoOverlay` 闸、`updateActiveMenu` 的短路分支、
/// `UpdateControlInput` 的调用条件，以及 `pauseWhenOutOfFocus` 早退分支。
/// 不改 `Game.IsActive` 的属性语义、不发任何 Win32 焦点消息、不抢用户前台。
module Background =

    let private flags =
        BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly

    /// 反射字段只查一次，之后每帧只是两次 Set/GetValue。
    let private findField (o: obj) (name: string) : FieldInfo option =
        let mutable ty = if isNull o then null else o.GetType()
        let mutable hit: FieldInfo = null

        while isNull hit && not (isNull ty) do
            hit <- ty.GetField(name, flags)
            ty <- ty.BaseType

        if isNull hit then None else Some hit

    let mutable private platformField: FieldInfo option = None
    let mutable private activeField: FieldInfo option = None
    let mutable private resolved = false

    let mutable lastError = ""
    let mutable pinnedFrames = 0L
    let mutable observedActive = false

    /// ⚠️ 对象是 `GameRunner.instance`，不是 `Game1.game1`：
    /// `InstanceGame.get_IsActive()` 的 IL 是 `ldsfld GameRunner.instance; callvirt Game::get_IsActive()`，
    /// 而 `IsActiveNoOverlay` 正是走它。pin 错对象等于白干。
    /// 也别在第一帧解析失败后就锁死：游戏刚启动时 `GameRunner.instance` 可能还是 null。
    let private resolve () =
        if not resolved then
            try
                let game = box StardewValley.GameRunner.instance

                if not (isNull game) then
                    platformField <- findField game "Platform"

                    match platformField with
                    | Some pf ->
                        let platform = pf.GetValue game
                        activeField <- findField platform "_isActive"
                    | None -> ()

                    // 两个都拿到才算解析成功；否则下一帧继续试
                    resolved <- Option.isSome platformField && Option.isSome activeField
            with ex ->
                lastError <- ex.GetType().Name + ": " + ex.Message

    /// 读一次真实的 `GamePlatform._isActive`（未被我们改之前的语义探针）。
    let private rawActive () : bool =
        try
            match platformField, activeField with
            | Some pf, Some af ->
                let platform = pf.GetValue(box StardewValley.GameRunner.instance)
                unbox<bool> (af.GetValue platform)
            | _ -> false
        with _ ->
            false

    /// 每帧在主线程调一次（Mod.fs 的 UpdateTicking）。
    let tick () =
        resolve ()

        try
            match platformField, activeField with
            | Some pf, Some af ->
                let game = box StardewValley.GameRunner.instance
                let platform = pf.GetValue game

                if not (isNull platform) then
                    observedActive <- rawActive ()

                    if Runtime.backgroundControl then
                        af.SetValue(platform, box true)
                        pinnedFrames <- pinnedFrames + 1L
                    else
                        // ⚠️ 不能只是"不再写"：最小化时不会再有窗口激活事件来刷新这个字段，
                        // 它会一直停在 true，等于关不掉。主动还原成真实前台状态。
                        af.SetValue(platform, box (Native.isForeground ()))
            | _ -> ()
        with ex ->
            lastError <- ex.GetType().Name + ": " + ex.Message

    /// 给 /debug/focus 用：字段解析成功了吗、pin 了多少帧、有没有出错。
    let state () : (string * J) list =
        resolve ()

        [ "platformPinned", JBool(Option.isSome platformField && Option.isSome activeField)
          "pinnedFrames", JNum(float pinnedFrames)
          "rawIsActive", JBool(observedActive)
          "pinError", JStr lastError ]
