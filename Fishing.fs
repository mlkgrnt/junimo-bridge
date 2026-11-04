namespace JunimoBridge

open System
open System.Reflection
open StardewValley
open StardewValley.Menus
open StardewValley.Tools

/// 钓鱼。
///
/// 三条关键事实（都来自 IL + 真机）：
///  1. **BobberBar 读的是 `Game1.oldMouseState` / `Game1.oldKBState`**，不是 current ——
///     注入要慢一帧才被吃进去。我们的 `InputState` 补丁照样管用，只是别指望同帧生效。
///  2. `buttonPressed == true` ⇒ 绿条加速度 **-0.25**（往上），松开 ⇒ **+0.25**（往下）。
///     控制就一句话：**鱼在绿条中心上方就按住**。
///  3. 进度是 `BobberBar.distanceFromCatching`（0→1），**不在** `FishingRod` 上。
module Fishing =

    let private bar () =
        match Game1.activeClickableMenu with
        | :? BobberBar as b -> Some b
        | _ -> None

    let private barOpen () = Option.isSome (bar ())

    /// 找鱼竿：先看手上，再看背包（并顺手选上）。
    let private findRod () =
        let player = Game1.player

        match player.CurrentTool with
        | :? FishingRod as r -> Some r
        | _ ->
            let mutable idx = -1

            for i in 0 .. player.Items.Count - 1 do
                if idx < 0 then
                    match player.Items.[i] with
                    | :? FishingRod -> idx <- i
                    | _ -> ()

            if idx >= 0 then
                player.CurrentToolIndex <- idx

                match player.CurrentTool with
                | :? FishingRod as r -> Some r
                | _ -> None
            else
                None

    let private barJson (b: BobberBar) : J =
        let barTop = b.bobberBarPos - 32.0f
        let barBottom = barTop + float32 b.bobberBarHeight

        JObj
            [ "progress", JNum(float b.distanceFromCatching)
              "fishY", JNum(float b.bobberPosition)
              "fishSpeed", JNum(float b.bobberSpeed)
              "barTop", JNum(float barTop)
              "barHeight", Json.num b.bobberBarHeight
              "barBottom", JNum(float barBottom)
              "inBar", JBool b.bobberInBar
              "pressing", JBool b.buttonPressed
              "fishId", Json.str b.whichFish
              "difficulty", JNum(float b.difficulty)
              "fishSize", Json.num b.fishSize
              "fishQuality", Json.num b.fishQuality
              "perfect", JBool b.perfect
              "treasure", JBool b.treasure
              "treasureCaught", JBool b.treasureCaught
              "bossFish", JBool b.bossFish
              "beginnersRod", JBool b.beginnersRod ]

    let private rodJson (rod: FishingRod) (player: Farmer) : (string * J) list =
        [ "castPower", JNum(float rod.castingPower)
          "isCasting", JBool rod.isTimingCast
          "inAir", JBool rod.castedButBobberStillInAir
          "isFishing", JBool rod.isFishing
          "isNibbling", JBool rod.isNibbling
          "isReeling", JBool rod.isReeling
          "isPullingOut", JBool rod.pullingOutOfWater
          "hit", JBool rod.hit
          "fishCaught", JBool rod.fishCaught
          "biteInMs", JNum(float rod.timeUntilFishingBite)
          "bobberTile",
          JObj
              [ "x",
                (if rod.bobber.X = 0.0f && rod.bobber.Y = 0.0f then
                     JNull
                 else
                     Json.num (int (rod.bobber.X / 64.0f)))
                "y",
                (if rod.bobber.X = 0.0f && rod.bobber.Y = 0.0f then
                     JNull
                 else
                     Json.num (int (rod.bobber.Y / 64.0f))) ]
          "clearWaterDistance", Json.num rod.clearWaterDistance
          "fishingLevel", Json.num player.FishingLevel ]

    /// 背包快照（名字 → 总堆叠数）。用来判断「到底多出来什么」。
    /// 不按 Category 过滤：钓上来的可能是鱼，也可能是垃圾（海草/浮木），都要算。
    let private invSnapshot () =
        try
            Game1.player.Items
            |> Seq.filter (fun i -> not (isNull i))
            |> Seq.map (fun i -> i.Name, i.Stack)
            |> List.ofSeq
        with _ ->
            []

    let private invDelta (before: (string * int) list) (after: (string * int) list) =
        let countOf (name: string) (lst: (string * int) list) =
            lst |> List.filter (fun (n, _) -> n = name) |> List.sumBy snd

        after
        |> List.map fst
        |> List.distinct
        |> List.choose (fun n ->
            let gained = countOf n after - countOf n before

            if gained > 0 then Some(n, gained) else None)

    /// GET /fishing —— 一次问清「现在在钓什么状态」。
    let state () : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player

                match findRod () with
                | None ->
                    JObj
                        [ "ok", JBool true
                          "hasRod", JBool false
                          "note", JStr "背包里没有鱼竿（钓鱼要先去 Willy 那里拿竿）"
                          "fishingLevel", Json.num player.FishingLevel ]
                | Some rod ->
                    let holding =
                        try
                            obj.ReferenceEquals(player.CurrentTool, rod)
                        with _ ->
                            false

                    JObj
                        ([ "ok", JBool true
                           "hasRod", JBool true
                           "holdingRod", JBool holding
                           "minigame", JBool(barOpen ()) ]
                         @ rodJson rod player
                         @ [ "bar",
                             (match bar () with
                              | Some b -> barJson b
                              | None -> JNull) ]))
                3000

    /// 按帧等条件成立；超时返回 false。
    /// `Ops.run` 的 step 跑在主线程，所以 `cond` 里读游戏状态是安全的。
    let private waitUntil (ms: int) (cond: unit -> bool) =
        let frame = Ops.run (fun _ -> if cond () then Some(JBool true) else None)

        match Ops.await frame ms with
        | JBool b -> b
        | _ -> false

    /// 鱼竿是否处于「空闲可抛」状态。
    /// 钓完一条之后有一段收线动画（`fishCaught` / `pullingOutOfWater`），
    /// 这期间再抛会**完全无效**，真机实测表现为「三轮都没开成小游戏」。
    ///
    /// `hit` / `isNibbling` 也算在内：`tickUpdate` 里有一句
    /// `if (timeUntilFishingNibbleDone != -1 && hit) return;` ——
    /// **hit 一挂住，整根竿每帧直接早退**，既不会咬钩也不会响应提竿。
    let private idle (rod: FishingRod) =
        let p = Game1.player
        // 关键：`CanMove` 掉了 `pressUseToolButton` 就不响应。
        // ⚠️ **不要**把 `UsingTool` 当"卡住"来清：整段钓鱼（抛竿→搏鱼→收线）
        //    都靠 `UsingTool=true` 让 FishingRod.tickUpdate 每帧跑；一清就冻结在
        //    `isPullingOut=true`，鱼永远进不了背包（真机抓了三轮才定位到）。
        let canMove = p.CanMove
        let moving = (p :> StardewValley.Character).isMoving()

        canMove
        && (not moving)
        && (not rod.isFishing)
        && (not rod.isNibbling)
        && (not rod.hit)
        && (not rod.fishCaught)
        && (not rod.isTimingCast)
        && (not rod.castedButBobberStillInAir)
        && (not rod.pullingOutOfWater)
        && (not (barOpen ()))

    /// 等鱼竿空闲；确实僵死了才做**最小**修复。
    ///
    /// ⚠️ 绝不能碰 `pullingOutOfWater` / `fishCaught` / `doneWithAnimation` ——
    /// 钓上来之后 game 要播一段收线动画，走完才把鱼放进背包。之前把它们清了，
    /// 结果 5/5 都「进度打满」却一条鱼都没入包（真机抓到：`isPullingOut` 卡住）。
    /// 判据：没浮标、没小游戏、**没在收线**、**没有待结算的鱼**，才算僵死。
    let private waitIdle (rod: FishingRod) (ms: int) : bool =
        if waitUntil ms (fun () -> idle rod) then
            true
        else
            let inCatchAnimation = rod.pullingOutOfWater || rod.fishCaught

            if not inCatchAnimation && not (barOpen ()) then
                try
                    // 上一竿收尾没走完时 isFishing 会一直挂着，让下一竿误以为
                    // 「已经抛好了」而跳过抛竿、直接干等咬钩。resetState 是游戏
                    // 自己的干净复位（清 isFishing/pullingOutOfWater/fishCaught/...）。
                    // 只在 waitIdle 超时且确认没有收线动画/小游戏时才调。
                    if rod.isFishing || rod.hit || rod.isNibbling then
                        rod.resetState ()
                    rod.hit <- false
                    rod.isNibbling <- false

                    // 只补 CanMove；UsingTool 一律不碰（见 idle 的注释）
                    if not Game1.player.CanMove then
                        Game1.player.forceCanMove ()
                with _ ->
                    ()

            // 收线动画没走完就继续等，绝不去动它
            waitUntil (if inCatchAnimation then 8000 else 1500) (fun () -> idle rod)

    /// 抛竿（内部复用）。返回 (浮标是否落水, 蓄力峰值)。
    ///
    /// ⚠️ 判定条件只认 `isFishing / isNibbling / 小游戏已开` ——
    /// **绝不能把 `isTimingCast` 算进去**：它起手第一帧就是 true，
    /// 那样等于按住一帧就松手，castPower 只有 0.06，浮标根本到不了水面。
    ///
    /// 另：真机第一次抛竿偶尔不落地（刚 warp / face 完，输入被过场吃掉），
    /// 所以失败会重试一次 —— 这比让调用方猜靠谱。
    let private doCast (rod: FishingRod) (holdMs: int) : bool * float =
        let mutable peak = 0.0

        let attempt () =
            Input.release ()
            Input.hold [ "use" ] holdMs |> ignore

            let landed =
                waitUntil (holdMs + 6000) (fun () ->
                    peak <- max peak (float rod.castingPower)
                    // ⚠️ 只认 isFishing。残留的 isNibbling=true 会把「没抛出去」误判成
                    //    「抛好且已咬钩」，下一竿 DoFunction 又因 isFishing=false 走抛竿分支。
                    rod.isFishing || barOpen ())

            Input.release ()
            landed

        waitIdle rod 6000 |> ignore

        if attempt () then
            true, peak
        else
            // 给游戏一两帧喘气，再试一次
            waitUntil 300 (fun () -> false) |> ignore
            let ok = attempt ()
            (ok || rod.isFishing), peak

    /// POST /cast {ms} —— 按住「使用」蓄力，松手抛竿。
    ///
    /// 不要用 `/use`：那是「点一下」，`FishingRod.DoFunction` 一起手就松，力度几乎为 0。
    /// 真机节奏：按 ~300ms 是近抛，600~800ms 是满力。
    let cast (ms: int) : J =
        match findRod () with
        | None -> JObj [ "ok", JBool false; "error", JStr "背包里没有鱼竿" ]
        | Some rod ->
            let holdMs = max 60 (min 2000 ms)
            let ok, peak = doCast rod holdMs

            JObj
                [ "ok", JBool ok
                  "castMs", Json.num holdMs
                  "castPower", JNum peak
                  "isFishing", JBool rod.isFishing
                  "isNibbling", JBool rod.isNibbling
                  "inAir", JBool rod.castedButBobberStillInAir
                  "clearWaterDistance", Json.num rod.clearWaterDistance
                  "bobberTile",
                  JObj
                      [ "x", Json.num (int (rod.bobber.X / 64.0f))
                        "y", Json.num (int (rod.bobber.Y / 64.0f)) ]
                  (if ok then
                       "hint", JStr "等 isNibbling=true 再 /reel；要全自动就直接 /fish"
                   else
                       "error", JStr "抛竿没落水（可能对着陆地/障碍，或者这里没水）") ]

    /// 最近一次提竿的前后状态快照（排查用，会进 /fish 的 trace）。
    let mutable lastReelDiag = ""
    /// 提竿：把 `hit` 置上再**直接调 `FishingRod.DoFunction`**。
    ///
    /// 为什么不靠按键：提竿要的是「咬钩窗口内的那一次按下」这个边沿，
    /// 而注入的鼠标状态要走 `Game1.pressUseToolButton` 的边沿判定 ——
    /// 真机实测这个边沿在自动化下不成立（`bitten` 一直 true、`opened` 永远 false）。
    /// 直接调方法没有这个问题：`DoFunction` 里读到 `hit` 就走 `startMinigameEndFunction`，
    /// 和我们处理锄头/水壶是同一套路子。
    let private reelOnce (rod: FishingRod) : bool =
        let pressed =
            Ops.awaitMain
                (fun () ->
                    try
                        let p = Game1.player

                        // ⚠️ **不要**在这里 forceCanMove()：四节点快照显示抛竿/等咬钩期间
                        //    canMove=0 是游戏正常状态（casting 动画锁），强行恢复会打断
                        //    Farmer 的状态机，DoFunction 之后 isFishing/nib 被一起清掉、
                        //    BobberBar 也开不出来。之前为修「进档后 CanMove=false」加的，
                        //    在钓鱼进行中是有害的。
                        if rod.isNibbling && not (barOpen ()) then
                            let beforeDiag =
                                sprintf "before[hit=%b fish=%b nib=%b pull=%b caught=%b reel=%b menu=%s toolRod=%b]"
                                    rod.hit rod.isFishing rod.isNibbling rod.pullingOutOfWater
                                    rod.fishCaught rod.isReeling
                                    (if isNull Game1.activeClickableMenu then "" else string (Game1.activeClickableMenu.GetType().Name))
                                    (obj.ReferenceEquals(p.CurrentTool, rod))
                            lastReelDiag <- beforeDiag
                            // ⚠️ **不要**手动设 `rod.hit`：`DoFunction` 里有
                            //    IL_07C1 `if (hit) return;` —— 自己先设 true 就把它堵死了。
                            //    `hit` 是 DoFunction 自己写的（IL_07D9），随后才
                            //    `startMinigameEndFunction` 造 BobberBar。
                            // ⚠️ 也**不要**再调 `BeginUsingTool()`：它会走 `beginUsing`，
                            //    把 `isFishing` / `hit` 一起清零，等于把刚钩上的鱼放了。
                            rod.fishCaught <- false
                            // ⚠️ 上一竿成功后 `DoFunction` 会把 hit 置 true，而
                            //    `resetState()` **不清 hit**。下一竿提竿时会撞上
                            //    IL_07C1 `if (hit) return` —— 表现正是
                            //    `bitten:true / opened:false` 且每一轮都这样。
                            //    之前只记住「别手动设成 true」，漏了「要清掉残留的 true」。
                            rod.hit <- false
                            Runtime.suppressToolUse <- false

                            try
                                rod.DoFunction(p.currentLocation, p.TilePoint.X, p.TilePoint.Y, 0, p)
                            finally
                                Runtime.suppressToolUse <- true

                            lastReelDiag <-
                                lastReelDiag
                                + sprintf " after[bar=%b hit=%b fish=%b nib=%b menu=%s]"
                                    (barOpen ()) rod.hit rod.isFishing rod.isNibbling
                                    (if isNull Game1.activeClickableMenu then "" else string (Game1.activeClickableMenu.GetType().Name))
                    with _ ->
                        ()

                    JNull)
                3000
            |> ignore

        let ok = waitUntil 1500 (fun () -> barOpen ())

        // 还没开就退回按键那条路（万一 DoFunction 这条路被版本改了）
        if not ok then
            Input.release ()
            Input.hold [ "use" ] 260 |> ignore
            let byKey = waitUntil 1200 (fun () -> barOpen ())
            Input.release ()
            byKey
        else
            ok

    /// POST /reel —— 鱼咬钩那一刻提竿，开钓鱼小游戏。
    ///
    /// `isNibbling` 的窗口很短（`maxTimeToNibble`），错过就跑了。
    let reel () : J =
        match findRod () with
        | None -> JObj [ "ok", JBool false; "error", JStr "背包里没有鱼竿" ]
        | Some rod ->
            if barOpen () then
                JObj [ "ok", JBool true; "alreadyFighting", JBool true ]
            else
                let opened = reelOnce rod

                JObj
                    [ "ok", JBool(opened || rod.fishCaught)
                      "opened", JBool opened
                      "isNibbling", JBool rod.isNibbling
                      "fishCaught", JBool rod.fishCaught
                      (if opened then
                           "hint", JStr "小游戏已开；用 /debug/bar 看实时状态，/fish 让它自己收"
                       else
                           "error", JStr "没咬钩或已跑掉（先确认 /fishing 里 isNibbling=true）") ]

    /// 防重复替玩家确认（doneHoldingFish 一次就够）。
    let mutable private holdingHandled = false


    /// 搏鱼：**预测式 PD** —— 把「绿条中心下一帧会在哪」算出来，鱼在它上面就按住。
    ///
    /// 为什么不能只比当前位置：绿条是带阻尼的惯性系统（`bobberSpeed += accel - speed/5`），
    /// 光看当前位置必然过冲，真机实测 40 秒只爬到 0.85。
    /// 条自己的速度只能按帧差分（`BobberBar` 只暴露鱼的位置与速度，没有条的速度字段）。
    let private fight (rod: FishingRod) (timeoutMs: int) (lookahead: float) (deadband: float) : int * float * float =
        let mutable frames = 0
        let mutable pressing = false
        let mutable holdLeft = 0
        let mutable minP = 1.0
        let mutable maxP = 0.0
        let mutable prevCenter = nan
        let mutable presses = 0

        let releaseIfNeeded () =
            if pressing then
                Input.release ()
                pressing <- false

        let frame =
            Ops.run (fun _ ->
                frames <- frames + 1

                match bar () with
                | None ->
                    releaseIfNeeded ()
                    Some(JBool true)
                | Some b ->
                    // ⚠️ BobberBar 结算那一段（IL_012F）是
                    //    `var rod = Game1.player.CurrentTool as FishingRod;`
                    //    之后 IL_01BA `if (rod == null) 走失败分支` ——
                    //    **手上不是竿，进度打满也白发**。每帧把竿握回去。
                    if not (obj.ReferenceEquals(Game1.player.CurrentTool, rod)) then
                        try
                            let items = Game1.player.Items
                            let mutable slot = -1

                            for i in 0 .. items.Count - 1 do
                                if slot < 0 && obj.ReferenceEquals(items.[i], rod) then
                                    slot <- i

                            if slot >= 0 then Game1.player.CurrentToolIndex <- slot
                        with _ ->
                            ()

                    // 新一竿开始：复位「替玩家确认」的一次性标记并续期收尾窗口
                    if frames = 1 then holdingHandled <- false
                    Runtime.fishFinishUntil <- Environment.TickCount64 + 30000L

                    let p = float b.distanceFromCatching
                    minP <- min minP p
                    maxP <- max maxP p

                    let center = float b.bobberBarPos - 32.0 + float b.bobberBarHeight / 2.0

                    let barVel =
                        if Double.IsNaN prevCenter then 0.0 else center - prevCenter

                    prevCenter <- center

                    let predicted = center + lookahead * barVel
                    let fishY = float b.bobberPosition
                    let want = fishY < predicted - deadband

                    if want then
                        if not pressing then
                            Input.hold [ "use" ] 9000 |> ignore
                            pressing <- true
                            holdLeft <- 9000
                            presses <- presses + 1
                        else
                            holdLeft <- holdLeft - 16

                            if holdLeft < 700 then
                                Input.hold [ "use" ] 9000 |> ignore
                                holdLeft <- 9000
                    else
                        releaseIfNeeded ()

                    None)

        Ops.await frame timeoutMs |> ignore
        releaseIfNeeded ()
        (presses, minP, maxP)

    /// POST /fish {timeoutMs, lookahead, deadband} —— 全自动钓一条：
    /// 抛竿 → 等咬钩 → 提竿 → 收线。
    ///
    /// `timeoutMs` 总预算（默认 45s）；`lookahead` / `deadband` 是搏鱼控制器参数
    /// （默认 3.0 / 0.0），调参不用重编。
    /// 轻量收尾：只清会让下一竿误判的标志，不强行重置角色动画/竿状态。
    /// 之前在这里调 `completelyStopAnimatingOrDoingAction` + `resetState()` 太重，
    /// 反而让 `UsingTool` 被清掉、`tickUpdate` 早退、浮标再也不落地。
    let private cleanupFishing () : unit =
        Ops.awaitMain
            (fun () ->
                try
                    Input.release ()

                    match Game1.player.CurrentTool with
                    | :? FishingRod as r ->
                        // 三个「假状态」标志：resetState 不清 hit；isNibbling 在
                        // tickUpdate 早退时会永久挂着，让 doCast 误判、让 DoFunction
                        // 在 isFishing=false 时走抛竿分支。每次 /fish 进出都清干净。
                        r.hit <- false
                        r.isNibbling <- false
                        r.isFishing <- false
                    | _ -> ()

                    holdingHandled <- false
                    Runtime.fishFinishUntil <- 0L
                with _ ->
                    ()

                JNull)
            2000
        |> ignore

    /// 排查用：一轮里关键节点的竿/角色状态快照。
    let private snapRod (rod: FishingRod) : J =
        JObj
            [ "fish", JBool rod.isFishing
              "nib", JBool rod.isNibbling
              "hit", JBool rod.hit
              "pull", JBool rod.pullingOutOfWater
              "caught", JBool rod.fishCaught
              "hasDone", JBool rod.hasDoneFucntionYet
              "canMove", JBool Game1.player.CanMove
              "usingTool", JBool Game1.player.UsingTool
              "bar", JBool(barOpen ()) ]

    let private fishCore (timeoutMs: int) (lookahead: float) (deadband: float) : J =
        match findRod () with
        | None -> JObj [ "ok", JBool false; "error", JStr "背包里没有鱼竿" ]
        | Some rod ->
            // 每次都从干净状态起：上一竿的 isFishing / UsingTool / CanMove 残留
            // 会让本轮跳过抛竿、或让 DoFunction 走错分支（真机实测连续调用必现）。
            cleanupFishing ()

            let budget = max 8000 (min 120000 timeoutMs)
            let started = Environment.TickCount64
            let before = invSnapshot ()
            let look = max 0.0 (min 20.0 lookahead)
            let dead = max 0.0 (min 60.0 deadband)

            let elapsed () = int (Environment.TickCount64 - started)

            // 1~3) 抛竿 → 等咬钩 → 提竿，最多 3 轮。
            //   必须能重来：错过了咬钩窗口鱼就跑了，只能收线重抛。
            let mutable opened = false
            let mutable rounds = 0
            let mutable lastBitten = false
            let trace = ResizeArray<string * J>()

            while (not opened) && rounds < 3 && elapsed () < budget - 12000 do
                rounds <- rounds + 1

                // 只认 `isFishing / 小游戏已开`；**不认 `isNibbling`** ——
                // 钓完之后这根竿可能留下一个僵死的 isNibbling=true，信它会跳过抛竿。
                let s0 = snapRod rod
                let wasIdle = idle rod
                let fishing =
                    if rod.isFishing || barOpen () then
                        true
                    else
                        fst (doCast rod 420)
                let s1 = snapRod rod

                let bitten =
                    if fishing then
                        // ⚠️ 别给咬钩设主观上限：真机测到 `timeUntilFishingBite` 能到 **22 秒**
                        //（0 级竿、无鱼饵、远抛），卡 20s 会一直在"没咬钩"上超时。
                        lastBitten <-
                            waitUntil (max 3000 (budget - 6000 - elapsed ())) (fun () ->
                                rod.isNibbling || rod.hit || barOpen ())
                        lastBitten
                    else
                        false

                let s2 = snapRod rod

                if fishing then
                    if barOpen () then
                        opened <- true
                    elif bitten then
                        opened <- reelOnce rod

                let s3 = snapRod rod

                trace.Add(
                    sprintf "round%d" rounds,
                    JObj
                        [ "idleBefore", JBool wasIdle
                          "casted", JBool fishing
                          "bitten", JBool bitten
                          "opened", JBool opened
                          "castPower", JNum(float rod.castingPower)
                          "clearWaterDistance", Json.num rod.clearWaterDistance
                          "ms", Json.num (elapsed ())
                          "reel", JStr lastReelDiag
                          "snap",
                          JObj
                              [ "beforeCast", s0
                                "afterCast", s1
                                "afterBite", s2
                                "afterReel", s3 ] ]
                )

                if not opened then
                    // 收线 + 喘口气，再重来
                    Input.hold [ "use" ] 90 |> ignore
                    waitUntil 500 (fun () -> false) |> ignore
                    Input.release ()

            if not opened then
                // ⚠️ 没开 BobberBar ≠ 没钓到：垃圾类物品（Joja Cola / Driftwood /
                //    Broken CD …）走 `DoFunction` 的直接发放路径，不进小游戏。
                //    真机实测连续钓第 2/3/4 竿都拿到了这些，旧回执却一律报失败。
                //    以背包增量为准，如实区分「小游戏打赢」和「直接收获」。
                let gained = invDelta before (invSnapshot ())
                let delivered = not gained.IsEmpty

                JObj
                    [ "ok", JBool delivered
                      "won", JBool false
                      "delivered", JBool delivered
                      "caught", JBool delivered
                      "directCatch", JBool delivered
                      "caughtItems",
                      JArr(
                          gained
                          |> List.map (fun (n, c) -> JObj [ "name", JStr n; "count", Json.num c ])
                      )
                      "progressPeak", JNum 0.0
                      "bitten", JBool lastBitten
                      "rounds", Json.num rounds
                      "state",
                      JObj
                          [ "hit", JBool rod.hit
                            "isNibbling", JBool rod.isNibbling
                            "isFishing", JBool rod.isFishing
                            "fishCaught", JBool rod.fishCaught ]
                      "trace", JObj(List.ofSeq trace)
                      (if delivered then
                           "hint", JStr "直接收获（垃圾等无需小游戏的物品），未经过钓鱼小游戏"
                       else
                           "error", JStr "三轮都没开成小游戏（没咬钩 / 提竿错过窗口）")
                      "elapsedMs", Json.num (elapsed ()) ]
            else
                    // 4) 搏鱼
                    let presses, minP, maxP = fight rod (budget - 5000) look dead

                    // 5) 结算。**打赢小游戏 ≠ 鱼进了背包**，两件事分开报。
                    //    BobberBar 进度打满后还要播一段收线动画才发物品；无焦点自动化下
                    //    这段动画可能卡住（真机实测：5/5 进度打满，物品却一条没入包，
                    //    竿子停在 isPullingOut=true / UsingTool=false）。
                    //    所以 ok 只认「真的多出来东西」，绝不拿 progress 冒充成果。
                    let won = maxP >= 0.999

                    let mutable gained = []

                    waitUntil 12000 (fun () ->
                        gained <- invDelta before (invSnapshot ())
                        not gained.IsEmpty)
                    |> ignore

                    if gained.IsEmpty then
                        gained <- invDelta before (invSnapshot ())

                    let delivered = not gained.IsEmpty

                    JObj
                        [ "ok", JBool delivered
                          "won", JBool won
                          "delivered", JBool delivered
                          "caught", JBool delivered
                          "caughtItems",
                          JArr(
                              gained
                              |> List.map (fun (n, c) -> JObj [ "name", JStr n; "count", Json.num c ])
                          )
                          "progressPeak", JNum maxP
                          "progressLow", JNum minP
                          "fishingLevel", Json.num Game1.player.FishingLevel
                          "presses", Json.num presses
                          "tuning", JObj [ "lookahead", JNum look; "deadband", JNum dead ]
                          "elapsedMs", Json.num (elapsed ())
                          (if delivered then
                               "hint", JStr "已入包"
                           elif won then
                               "error", JStr "打赢了钓鱼小游戏，但物品没入包：收线动画在自动化下卡住了（与窗口焦点无关，前台同样复现；/debug/bar 与 /fishing 可查）。可重试，或改用 cast + fishing 分步操作"
                           else
                               "error", JStr "鱼跑了（进度没打满）。可重试，或搏鱼时用 /debug/bar 看它到底在干什么") ]

    /// GET /water?radius=N —— 附近哪里有水（钓鱼找钓点用）。
    ///
    /// `GameLocation.waterTiles` 是 `WaterTiles`，按 `[x,y]` 索引。
    /// 顺手给出「站在哪、往哪抛」：水面格相邻、且站得住的那一格。
    let water (radius: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let px = player.TilePoint.X
                let py = player.TilePoint.Y
                let r = max 1 (min 40 radius)

                let isWater (x: int) (y: int) =
                    try
                        loc.waterTiles.[x, y]
                    with _ ->
                        false

                let passable (x: int) (y: int) =
                    try
                        loc.isTilePassable(xTile.Dimensions.Location(x, y), Game1.viewport)
                    with _ ->
                        false

                let hits = ResizeArray<int * int>()

                for y in py - r .. py + r do
                    for x in px - r .. px + r do
                        if isWater x y then hits.Add(x, y)

                if hits.Count = 0 then
                    JObj
                        [ "ok", JBool true
                          "location", Json.str loc.Name
                          "count", Json.num 0
                          "note", JStr(sprintf "%s 半径 %d 内没有水（换个地方）" loc.Name r) ]
                else
                    // 站格：紧挨水面、可站立、离玩家最近的
                    let stands =
                        hits
                        |> Seq.collect (fun (x, y) ->
                            [ x - 1, y; x + 1, y; x, y - 1; x, y + 1 ]
                            |> Seq.filter (fun (sx, sy) -> passable sx sy)
                            |> Seq.map (fun (sx, sy) -> (sx, sy, x, y)))
                        |> Seq.sortBy (fun (sx, sy, _, _) -> abs (sx - px) + abs (sy - py))
                        |> Seq.truncate 8
                        |> Seq.map (fun (sx, sy, wx, wy) ->
                            JObj
                                [ "standX", Json.num sx
                                  "standY", Json.num sy
                                  "waterX", Json.num wx
                                  "waterY", Json.num wy ])
                        |> List.ofSeq

                    let nearest =
                        hits
                        |> Seq.sortBy (fun (x, y) -> abs (x - px) + abs (y - py))
                        |> Seq.truncate 12
                        |> Seq.map (fun (x, y) -> JObj [ "x", Json.num x; "y", Json.num y ])
                        |> List.ofSeq

                    JObj
                        [ "ok", JBool true
                          "location", Json.str loc.Name
                          "count", Json.num hits.Count
                          "playerTile", JObj [ "x", Json.num px; "y", Json.num py ]
                          "spots", JArr stands
                          "nearestWater", JArr nearest ])
            3000

    /// GET /debug/bar —— 搏鱼时实时读小游戏状态（排查控制逻辑用）。
    let barState () : J =
        Ops.awaitMain
            (fun () ->
                match bar () with
                | None -> JObj [ "ok", JBool true; "open", JBool false ]
                | Some b -> JObj [ "ok", JBool true; "open", JBool true; "bar", barJson b ])
            2000

    // ---------- 排查「收线动画卡住」的探针（不进 MCP 工具层）----------
    //
    // 链条（全部来自 IL）：
    //   BobberBar.update 成功 → rod.pullFishFromWater(...)        [只 Fire 一个 NetEvent]
    //   → doPullFishFromWater(BinaryReader)                       [设 pullingOutOfWater=true,
    //                                                             挂 TemporaryAnimatedSprite.endFunction]
    //   → sprite.endFunction → playerCaughtFishEndFunction(false) [清 pullingOutOfWater、设 fishCaught]
    //   → tickUpdate 的 fishCaught 分支**等到一次 use/点击** → doneHoldingFish(who,false)
    //                                                          [createItemDebris / addItemToInventoryBool]
    // 所以「鱼没入包」可能卡在任一环。这个探针把整条链摊开，并允许手动补环验证。

    let private dbgFlags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

    let private dbgField (o: obj) (name: string) : obj option =
        if isNull o then None
        else
            let mutable ty = o.GetType()
            let mutable f: FieldInfo = null
            while isNull f && not (isNull ty) do
                f <- ty.GetField(name, dbgFlags)
                ty <- ty.BaseType
            if isNull f then None else Some (f.GetValue o)

    let private dbgBool (o: obj option) = match o with Some (:? bool as b) -> b | _ -> false
    let private dbgNum (o: obj option) =
        match o with
        | Some (:? float as f) -> f
        | Some (:? single as f) -> float f
        | Some (:? int as n) -> float n
        | _ -> 0.0
    let private dbgText (o: obj option) =
        match o with Some x when not (isNull x) -> string x | _ -> ""

    let private dbgSprites () =
        let loc = Game1.player.currentLocation
        match dbgField loc "temporarySprites" with
        | Some (:? System.Collections.IEnumerable as e) -> e |> Seq.cast<obj> |> Seq.toList
        | _ -> []

    let private dbgSpriteJson (s: obj) =
        JObj
            [ "timer", JNum(dbgNum (dbgField s "timer"))
              "hasEndFunction", JBool(match dbgField s "endFunction" with
                                      | Some x when not (isNull x) -> true
                                      | _ -> false) ]

    /// POST /debug/fishchain  {"action": "none|endfn|caught|holding|tick"}
    /// 只读 action=none；其余是**排查用的手动补环**，正常玩法不会走到。
    let debugChain (action: string) : J =
        Ops.awaitMain
            (fun () ->
                match findRod () with
                | None -> JObj [ "ok", JBool false; "error", JStr "没有鱼竿" ]
                | Some rod ->
                    let p = Game1.player
                    let sprites = dbgSprites ()
                    let withEnd =
                        sprites
                        |> List.filter (fun s -> match dbgField s "endFunction" with
                                                 | Some x when not (isNull x) -> true
                                                 | _ -> false)

                    let mutable did = ""
                    let mutable err = ""

                    try
                        match action with
                        | "endfn" ->
                            match withEnd with
                            | s :: _ ->
                                match dbgField s "endFunction" with
                                | Some (:? Delegate as d) ->
                                    d.DynamicInvoke([| box 0 |]) |> ignore
                                    did <- "已手动触发 sprite.endFunction"
                                | _ -> err <- "endFunction 不是 Delegate"
                            | [] -> err <- "没有带 endFunction 的 sprite"
                        | "caught" ->
                            rod.playerCaughtFishEndFunction false
                            did <- "已调 playerCaughtFishEndFunction(false)"
                        | "holding" ->
                            rod.doneHoldingFish (p, false)
                            did <- "已调 doneHoldingFish(player,false)"
                        | "tick" ->
                            // 手动推进所有带回调的 sprite 一帧（验证是不是 Draw 被跳过导致不 tick）
                            for s in withEnd do
                                let m = s.GetType().GetMethod("update", [| typeof<Microsoft.Xna.Framework.GameTime> |])
                                if not (isNull m) then
                                    m.Invoke(s, [| box Game1.currentGameTime |]) |> ignore
                            did <- sprintf "已手动 update %d 个 sprite" withEnd.Length
                        | _ -> ()
                    with ex ->
                        err <- ex.GetType().Name + ": " + ex.Message

                    let inv =
                        p.Items
                        |> Seq.filter (fun i -> not (isNull i))
                        |> Seq.map (fun i -> JStr i.Name)
                        |> Seq.toList

                    JObj
                        [ "ok", JBool true
                          "action", JStr action
                          "did", JStr did
                          "error", JStr err
                          "rod",
                          JObj
                              [ "pullingOutOfWater", JBool rod.pullingOutOfWater
                                "fishCaught", JBool rod.fishCaught
                                "isFishing", JBool rod.isFishing
                                "isReeling", JBool rod.isReeling
                                "isNibbling", JBool rod.isNibbling
                                "hit", JBool rod.hit
                                "castedButBobberStillInAir", JBool rod.castedButBobberStillInAir
                                "whichFish", JStr(dbgText (dbgField rod "whichFish")) ]
                          "player",
                          JObj
                              [ "UsingTool", JBool p.UsingTool
                                "CanMove", JBool p.CanMove
                                "canReleaseTool", JBool(dbgBool (dbgField p "canReleaseTool"))
                                "spritePaused", JBool(dbgBool (dbgField p.FarmerSprite "PauseForSingleAnimation")) ]
                          "menu", JStr(if isNull Game1.activeClickableMenu then "" else string (Game1.activeClickableMenu.GetType().Name))
                          "sprites", JNum(float sprites.Length)
                          "spritesWithEndFunction", JNum(float withEnd.Length)
                          "spriteSample", JArr(withEnd |> List.truncate 3 |> List.map dbgSpriteJson)
                          "inventory", JArr inv ])
            3000

    /// 每帧由 Mod.fs 的 UpdateTicking 调用。**只修收线这一环，不代发物品**。
    ///
    /// 根因（IL_0177~01C7）：`FishingRod.tickUpdate` 开头要求
    ///   `who.CurrentTool is FishingRod && who.UsingTool`，否则直接 `ret`。
    /// 收线用的 `TemporaryAnimatedSprite` 存在 **`FishingRod.animations`**（不是地图的
    /// temporarySprites），全靠这段 tick 推进；它的 `endFunction` 才调
    /// `playerCaughtFishEndFunction` 清 `pullingOutOfWater`、置 `fishCaught`。
    /// 真机实测卡住时 `UsingTool=false` ⇒ 每帧早退 ⇒ 动画永不结束 ⇒
    /// `pullingOutOfWater=true` 一直挂着、鱼永远不发。而玩家此时按 use 会在
    /// `fishCaught=false` 的 `DoFunction` 里走抛竿分支，把待结算的鱼清掉
    /// —— 这就是「输入一进去就变新抛竿」。
    ///
    /// 修法：只在 /fish 期间（`Runtime.fishFinishUntil` 未过期）
    ///   ① 收线阶段把 `UsingTool` 按回 true，让游戏自己把动画跑完；
    ///   ② 等 `fishCaught=true`（举鱼待确认）后，替玩家点那一下 ——
    ///      `tickUpdate` IL_1542 本来就是等一次 use/点击才调 `doneHoldingFish`，
    ///      而 `fishCaught=true` 时 `DoFunction` 会在 IL_001B 直接 ret，不会新抛竿。
    /// 不碰 `whichFish`、不造物品，发鱼仍走游戏自己的 `createItemDebris`。
    let tickFinish () : unit =
        if Environment.TickCount64 < Runtime.fishFinishUntil then
            try
                let p = Game1.player

                match p.CurrentTool with
                | :? FishingRod as rod ->
                    if rod.pullingOutOfWater && not rod.fishCaught then
                        // 恢复 tickUpdate 的运行条件（Farmer.updateCommon 每帧可能清掉）
                        if not p.UsingTool then p.UsingTool <- true
                        holdingHandled <- false
                    elif rod.fishCaught && not holdingHandled then
                        holdingHandled <- true
                        rod.doneHoldingFish (p, false)

                        // `doneHoldingFish` 只是把鱼发给玩家；游戏原本还要靠
                        // `doneFishingEvent` 在下一帧 Poll → `doDoneFishing` 来收尾
                        // （UsingTool=false / CanMove=true / 停动画）。
                        // 收线期 tickUpdate 刚被救活，这个事件常常赶不上，于是
                        // isFishing/UsingTool 残留，下一竿全部从脏状态开始。
                        // 这里直接做等价收尾，并立刻停止干预。
                        rod.resetState ()
                        p.UsingTool <- false
                        p.forceCanMove ()
                        Runtime.fishFinishUntil <- 0L
                    elif not rod.fishCaught then
                        holdingHandled <- false
                | _ -> ()
            with _ ->
                ()

    /// POST /fish 的统一出口：返回前也收一次尾，别把脏状态留给下一次动作。
    let fish (timeoutMs: int) (lookahead: float) (deadband: float) : J =
        let result = fishCore timeoutMs lookahead deadband
        cleanupFishing ()
        result
