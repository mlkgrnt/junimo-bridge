namespace JunimoBridge

open System
open System.Reflection
open Microsoft.Xna.Framework
open StardewValley

/// 动作类端点：转向 / 用工具 / 交互 / 菜单。
///
/// 一律「先做，再回读终态」。挥工具那条尤其重要：游戏里的生效帧在 `EndUsingTool`
/// 之后约 550ms 才落下，只回 ok 不回读就是「说挥了其实没挥」——这类假成功是我们在
/// NagiBridge 那边踩得最多的坑。
module Actions =

    let private reflectionFlags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

    /// 有些菜单把**真正的界面**藏在 `subMenu` 属性里（标题界面的读档/存档、角色创建都是），
    /// `Game1.activeClickableMenu` 永远只报外层那一个。
    ///
    /// ⚠️ 「读档界面观测不到」这个结论就是这么来的 —— 其实它一直在，只是被外层挡着。
    /// 所以 `/menu` 和 `/screen` 都必须把它一起吐出来。
    let private subMenuOf (menu: obj) : obj =
        try
            let flags = reflectionFlags ||| BindingFlags.Static
            let getter = menu.GetType().GetMethod("get_subMenu", flags)

            if isNull getter then
                null
            else
                // ⚠️ `TitleMenu.get_subMenu()` 是 **static** —— 标题界面把子菜单存在静态成员里，
                //    只找实例属性会永远拿到 null。真机后果：读档界面其实开着，我们一直以为没开，
                //    于是"点不动读档界面"查了半天。
                let target = if getter.IsStatic then null else menu
                getter.Invoke(target, [||])
        with _ ->
            null

    let private describeSubMenu (menu: obj) : J =
        let sub = subMenuOf menu
        if isNull sub then JNull else Probe.describe sub


    /// 朝向那一格（游戏自己的 `GetToolLocation` 也是这个规则）。
    let facingPoint (player: Farmer) : Point =
        let t = player.TilePoint

        match player.FacingDirection with
        | 0 -> Point(t.X, t.Y - 1)
        | 1 -> Point(t.X + 1, t.Y)
        | 2 -> Point(t.X, t.Y + 1)
        | 3 -> Point(t.X - 1, t.Y)
        | _ -> Point(t.X, t.Y)

    /// 一格的「终态快照」：有没有物件、有没有地形特征（耕地/浇水都在 terrainFeatures 里）。
    let tileSnapshot (loc: GameLocation) (p: Point) : J =
        let mutable objName = ""

        try
            match loc.Objects.TryGetValue(Vector2(float32 p.X, float32 p.Y)) with
            | true, o when not (isNull o) -> objName <- o.Name
            | _ -> ()
        with _ ->
            ()

        let hasFeature =
            try
                loc.terrainFeatures.ContainsKey(Vector2(float32 p.X, float32 p.Y))
            with _ ->
                false

        JObj
            [ "x", Json.num p.X
              "y", Json.num p.Y
              "object", Json.str objName
              "hasTerrainFeature", JBool hasFeature ]

    let private directionCode (name: string) =
        match name.Trim().ToLowerInvariant() with
        | "up" -> 0
        | "right" -> 1
        | "down" -> 2
        | "left" -> 3
        | _ -> -1

    /// 方向既接受 "up/right/down/left"，也接受 0..3（客户端传的是整数）。
    let private directionText (code: int) = ["up"; "right"; "down"; "left"] |> List.tryItem code |> Option.defaultValue "?"

    let faceValue (direction: J) : J =
        let code =
            match direction with
            | JNum n -> int n
            | JStr s -> directionCode s
            | _ -> -1

        if code < 0 || code > 3 then
            JObj [ "ok", JBool false; "error", JStr "方向只能是 0..3 或 up/right/down/left" ]
        else
            Ops.awaitMain
                (fun () ->
                    let player = Game1.player
                    player.FacingDirection <- code

                    JObj
                        [ "ok", JBool true
                          "facing", JStr(directionText code)
                          "facingDirection", Json.num code
                          "target", tileSnapshot player.currentLocation (facingPoint player) ])
                2000

    let face (direction: string) : J =
        Ops.awaitMain
            (fun () ->
                let code = directionCode direction

                if code < 0 then
                    JObj [ "ok", JBool false; "error", JStr(sprintf "方向只能是 up/right/down/left，收到 \"%s\"" direction) ]
                else
                    let player = Game1.player
                    player.FacingDirection <- code

                    JObj
                        [ "ok", JBool true
                          "facing", JStr direction
                          "target", tileSnapshot player.currentLocation (facingPoint player) ])
            2000

    /// 让**我们自己这一发**工具/物品生效（落点、范围都可控）。
    ///
    /// ⚠️ 三个非显然的点，少一个就是"挥了等于没挥"：
    ///   1. `Hoe.DoFunction` / `WateringCan.DoFunction` **不收** `power` 参数 —— 它们内部写
    ///      `power = who.toolPower.Value`。而 `toolPower` 我们从来没设过 ⇒ 恒为 0 ⇒ 只落 1 格。
    ///      所以要在调用前后把 `toolPower.Value` 临时摆成想要的 power（这里单格用 0）。
    ///   2. 光 `BeginUsingTool()` 不会立刻落地：动画走到 frame68 才落，而那一发读到的
    ///      `toolPower` 已经被清零 ⇒ 位置也不一定是我们算的那格。⇒ 落地必须自己来。
    ///   3. 自己落完之后，紧跟的动画那一发要**按掉**（`Runtime.suppressToolUse`），
    ///      否则浇水会双扣水、锄地会双扣体力。
    /// 目标格是不是锄过的地（放种子前必须过这一关）。
    let private safeHoeDirt (loc: GameLocation) (t: Point) : bool =
        try
            match loc.terrainFeatures.TryGetValue(Vector2(float32 t.X, float32 t.Y)) with
            | true, (:? StardewValley.TerrainFeatures.HoeDirt) -> true
            | _ -> false
        with _ ->
            false

    /// 用之前先按**游戏自己的判据**挡一道，被挡就说清楚为什么。
    ///
    /// 为什么非要这一层：`/use` 若永远回 ok，上层就没法区分"真干了"和"白挥" ——
    /// 客户端的 `swing_tool` 正是靠这句 `already tilled` 判断"这一下到底有没有意义"。
    /// 文案照上游（`Tile already tilled` / `Already watered`），别自创。
    let private precheck (player: Farmer) (loc: GameLocation) (target: Point) : string option =
        try
            let v = Microsoft.Xna.Framework.Vector2(float32 target.X, float32 target.Y)
            let dirt =
                match loc.terrainFeatures.TryGetValue v with
                | true, (:? StardewValley.TerrainFeatures.HoeDirt as d) -> Some d
                | _ -> None

            match player.CurrentTool with
            | :? StardewValley.Tools.Hoe when dirt.IsSome -> Some "Tile already tilled"
            | :? StardewValley.Tools.WateringCan as can ->
                match dirt with
                | Some d when d.state.Value = 1 -> Some "Already watered"
                | _ when can.WaterLeft <= 0 -> Some "Out of water"
                | _ -> None
            | _ -> None
        with _ ->
            None

    let applyHeldTool () : string * int * int =
        let player = Game1.player
        let loc = player.currentLocation
        let target = facingPoint player
        let px = target.X * 64 + 32
        let py = target.Y * 64 + 32
        let item = player.CurrentItem

        match item with
        | :? Tool as tool ->
            // 🧹 先清：我们自己这一发绝不能被按掉
            Runtime.suppressToolUse <- false
            let before = player.toolPower.Value

            try
                player.toolPower.Value <- 0
                tool.DoFunction(loc, px, py, 0, player)
            finally
                player.toolPower.Value <- before

            // 之后紧跟着来的那一发（动画 frame68）按掉
            Runtime.suppressToolUse <- true
            Runtime.suppressTicks <- 90
            player.BeginUsingTool()
            tool.Name, target.X, target.Y
        // 种子/可放置物：不是"挥"，是摆下去（`/plant` 走的也是这条）。
        // ⚠️ 种子**必须先有锄过的地**再放：`placementAction` 在农舍地板上也可能回 true，
        //    不拦就会把整包种子白扣掉（真机踩过一次，10 颗种子凭空蒸发）。
        | :? StardewValley.Object as o when
            (o.Category <> StardewValley.Object.SeedsCategory
             || safeHoeDirt loc target)
            && o.placementAction(loc, px - 32, py - 32, player)
            ->
            player.reduceActiveItemByOne()
            o.Name, target.X, target.Y
        | _ ->
            player.BeginUsingTool()
            "", target.X, target.Y

    /// 用一次手里的东西。等生效帧过去再回读目标格，避免假成功。
    let useTool () : J =
        let mutable applied = "", -1, -1

        let effect =
            Ops.run (fun frames ->
                let player = Game1.player

                if frames = 1 then
                    match precheck player player.currentLocation (facingPoint player) with
                    // 挡下来：立刻结账（别等到 40 帧后回一个"看起来成功了"的 ok）
                    | Some err -> Some(JObj [ "ok", JBool false; "error", JStr err ])
                    | None ->
                        applied <-
                            try
                                applyHeldTool ()
                            with _ ->
                                "", -1, -1

                        None
                elif frames < 120 && (player.UsingTool || frames < 40) then
                    // 动画没走完，或者还没到生效帧（40 帧 ≈ 660ms），继续等
                    None
                else
                    let item, tx, ty = applied

                    let target =
                        if tx >= 0 && ty >= 0 then Point(tx, ty) else facingPoint player

                    Some(
                        JObj
                            [ "ok", JBool true
                              "action", JStr(if item = "" then "swing" else "use")
                              "item", Json.str item
                              "frames", Json.num frames
                              "stillUsing", JBool player.UsingTool
                              "target", tileSnapshot player.currentLocation target ]
                    ))

        Ops.await effect 5000

    /// 对面前那格做一次交互（开门、捡东西、跟 NPC 说话、开箱子都走这里）。
    let interact () : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let target = facingPoint player

                let triggered =
                    try
                        loc.checkAction(xTile.Dimensions.Location(target.X, target.Y), Game1.viewport, player)
                    with _ ->
                        false

                JObj
                    [ "ok", JBool true
                      "triggered", JBool triggered
                      "target", tileSnapshot loc target ])
            2000

    /// 当前对话正文（事件那一屏），Compat 的 activeEvent.message 用它。
    let menuReadDialogue () =
        try
            let menu = Game1.activeClickableMenu

            if isNull menu then
                ""
            else
                let field = menu.GetType().GetField("characterDialogue", reflectionFlags)

                if isNull field then
                    ""
                else
                    match field.GetValue menu with
                    | null -> ""
                    | d ->
                        let m = d.GetType().GetMethod("getCurrentDialogue", reflectionFlags)
                        if isNull m then "" else string (m.Invoke(d, [||]))
        with _ ->
            ""

    /// 菜单读：统一走探针，另外尽量把对话正文捞出来（不在 `fields` 里，得钻一层）。
    let menuRead () : J =
        Ops.awaitMain
            (fun () ->
                let menu = Game1.activeClickableMenu

                if isNull menu then
                    JObj [ "ok", JBool true; "open", JBool false ]
                else
                    let dialogue =
                        try
                            let field = menu.GetType().GetField("characterDialogue", reflectionFlags)

                            if isNull field then
                                ""
                            else
                                match field.GetValue menu with
                                | null -> ""
                                | d ->
                                    let m = d.GetType().GetMethod("getCurrentDialogue", reflectionFlags)
                                    if isNull m then "" else string (m.Invoke(d, [||]))
                        with _ ->
                            ""

                    let responses =
                        try
                            let field = menu.GetType().GetField("responses", reflectionFlags)

                            if isNull field then
                                JNull
                            else
                                match field.GetValue menu with
                                | :? System.Collections.IEnumerable as items ->
                                    let list = ResizeArray<J>()

                                    for item in items do
                                        if list.Count < 20 then
                                            let t = item.GetType()
                                            let textField = t.GetField("responseText", reflectionFlags)

                                            list.Add(
                                                JStr(
                                                    if isNull textField then
                                                        ""
                                                    else
                                                        string (textField.GetValue item)
                                                )
                                            )

                                    JArr(List.ofSeq list)
                                | _ -> JNull
                        with _ ->
                            JNull

                    JObj
                        [ "ok", JBool true
                          "open", JBool true
                          "type", JStr(menu.GetType().Name)
                          "subMenu", describeSubMenu menu
                          "dialogue", JStr dialogue
                          "responses", responses
                          "probe", Probe.describe menu ])
            2000

    let private clickComponent (menu: obj) (name: string) : bool =
        try
            let field = menu.GetType().GetField("allClickableComponents", reflectionFlags)

            match field.GetValue menu with
            | :? System.Collections.IEnumerable as items ->
                let mutable done_ = false

                for item in items do
                    if not done_ && not (isNull item) then
                        let t = item.GetType()
                        let nameField = t.GetField("name", reflectionFlags)

                        let matches =
                            not (isNull nameField)
                            && string (nameField.GetValue item) = name

                        if matches then
                            match t.GetField("bounds", reflectionFlags).GetValue item with
                            | :? Rectangle as r ->
                                let m = menu :?> StardewValley.Menus.IClickableMenu
                                m.receiveLeftClick(r.Center.X, r.Center.Y, true)
                                done_ <- true
                            | _ -> ()
                done_
            | _ -> false
        with _ ->
            false

    /// 点菜单。三种给法：`{component:"name"}` / `{x,y}` / 都不给则点屏幕中心。
    let menuClick (body: string) : J =
        let parsed = Json.parse body

        let component =
            parsed
            |> Option.bind (fun v -> Json.tryField "component" v)
            |> Option.bind Json.asString

        let optionIndex =
            parsed
            |> Option.bind (fun v -> Json.tryField "option" v)
            |> Option.bind Json.asNumber
            |> Option.map int

        let coord =
            parsed |> Option.bind (fun v -> Json.tryField "x" v) |> Option.bind Json.asNumber

        let coordY =
            parsed |> Option.bind (fun v -> Json.tryField "y" v) |> Option.bind Json.asNumber

        Ops.awaitMain
            (fun () ->
                let menu = Game1.activeClickableMenu

                if isNull menu then
                    JObj [ "ok", JBool false; "error", JStr "当前没有打开的菜单" ]
                else
                    match optionIndex with
                    | Some index ->
                        // 客户端选对话/事件选项走这里。三条路依次试：
                        // ① 对话类菜单：先写 selectedResponse 再按 Enter（游戏自己的键盘选法）
                        // ② 数字键（部分菜单绑定 1..9）
                        // ③ 点第 index 个可点击控件
                        let viaDialogue =
                            try
                                let t = menu.GetType()
                                let field = t.GetField("selectedResponse", reflectionFlags)

                                if isNull field then
                                    false
                                else
                                    field.SetValue(menu, index)
                                    menu.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Enter)
                                    true
                            with _ ->
                                false

                        if viaDialogue then
                            JObj [ "ok", JBool true; "clicked", JStr(sprintf "option:%d" index) ]
                        else
                        try
                            menu.receiveKeyPress(enum<Microsoft.Xna.Framework.Input.Keys>(int Microsoft.Xna.Framework.Input.Keys.D1 + index))
                            JObj [ "ok", JBool true; "clicked", JStr(sprintf "option:%d" index) ]
                        with _ ->
                            if clickComponent menu (string index) then
                                JObj [ "ok", JBool true; "clicked", JStr(sprintf "option:%d" index) ]
                            else
                                JObj [ "ok", JBool false; "error", JStr(sprintf "菜单里点不到第 %d 个选项" index) ]
                    | None ->
                    match component with
                    | Some name ->
                        if clickComponent menu name then
                            JObj [ "ok", JBool true; "clicked", JStr name; "type", JStr(menu.GetType().Name) ]
                        else
                            JObj [ "ok", JBool false; "error", JStr(sprintf "菜单里没有名为 \"%s\" 的控件" name) ]
                    | None ->
                        let x = match coord with Some v -> int v | None -> Game1.viewport.Width / 2
                        let y = match coordY with Some v -> int v | None -> Game1.viewport.Height / 2
                        let m = menu :?> StardewValley.Menus.IClickableMenu
                        m.receiveLeftClick(x, y, true)
                        JObj [ "ok", JBool true; "clicked", JObj [ "x", Json.num x; "y", Json.num y ] ])
            2000

    /// 关菜单。注意：钓鱼小游戏也是菜单，关掉它等于放跑这条鱼。
    let menuClose () : J =
        Ops.awaitMain
            (fun () ->
                let menu = Game1.activeClickableMenu

                if isNull menu then
                    JObj [ "ok", JBool true; "open", JBool false ]
                else
                    let name = menu.GetType().Name

                    try
                        menu.exitThisMenu()
                    with _ ->
                        ()

                    Game1.activeClickableMenu <- null

                    JObj
                        [ "ok", JBool true
                          "closed", JStr name
                          "fishingMinigame", JBool(name = "BobberBar") ])
            2000

    /// 跨图传送。
    ///
    /// **必须等传送真正落地**：`Game1.warpFarmer` 只是开始过场，下一帧 `currentLocation`
    /// 还没换 —— 紧接着调 walk_to 会按旧地图算路径（真机抓到：Farm 传送后 BFS 用了
    /// FarmHouse 的 12×12 地图，算出「2 步到 (10,10)」）。所以这里等位置真的变了才返回。
    let warp (location: string) (x: int) (y: int) : J =
        let precheck =
            Ops.awaitMain
                (fun () ->
                    let target =
                        try
                            Game1.getLocationFromName location
                        with _ ->
                            null

                    if isNull target then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "找不到地图 \"%s\"" location) ]
                    else
                        let mapW = target.map.Layers.[0].LayerWidth
                        let mapH = target.map.Layers.[0].LayerHeight

                        let tx = if x < 0 then mapW / 2 else x
                        let ty = if y < 0 then mapH / 2 else y

                        let standable =
                            try
                                target.isTilePassable(xTile.Dimensions.Location(tx, ty), Game1.viewport)
                            with _ ->
                                false

                        if not standable then
                            JObj
                                [ "ok", JBool false
                                  "error", JStr(sprintf "(%d,%d) 站不住（水/墙/障碍），没传送" tx ty) ]
                        else
                            JObj
                                [ "ok", JBool true
                                  "_tx", Json.num tx
                                  "_ty", Json.num ty ])
                3000

        let tx =
            match Json.tryField "_tx" precheck with
            | Some v -> (match Json.asNumber v with Some n -> Some(int n) | None -> None)
            | None -> None

        let ty =
            match Json.tryField "_ty" precheck with
            | Some v -> (match Json.asNumber v with Some n -> Some(int n) | None -> None)
            | None -> None

        match tx, ty with
        | Some tx, Some ty ->
            // 位置**稳定 3 帧**才算落定。
            //
            // ⚠️ 只看"地图名变了"是不够的：`Game1.warpFarmer` 的落点是**延迟**写进去的，
            //    我们一看到 `currentLocation` 变了就返回，紧接着的 `/position` 会被那一写
            //    覆盖掉 —— 真机表现就是「`/position` 回了 ok，人却还在传送落点」，
            //    后面所有按坐标做的事全打在错的地方。
            let mutable lastTile = (-999, -999)
            let mutable stable = 0

            let travel =
                Ops.run (fun frames ->
                    let player = Game1.player

                    if frames = 1 then
                        Game1.warpFarmer(location, tx, ty, false)
                        None
                    elif frames < 12 then
                        None
                    else
                        let here =
                            if isNull player || isNull player.currentLocation then
                                ""
                            else
                                player.currentLocation.Name

                        if here = location then
                            let tile = (player.TilePoint.X, player.TilePoint.Y)

                            if tile = lastTile then
                                stable <- stable + 1
                            else
                                lastTile <- tile
                                stable <- 0

                            if stable >= 3 && frames >= 20 then
                                Some(
                                    JObj
                                        [ "ok", JBool true
                                          "location", JStr here
                                          "x", Json.num (fst tile)
                                          "y", Json.num (snd tile)
                                          "frames", Json.num frames ]
                                )
                            elif frames > 240 then
                                Some(
                                    JObj
                                        [ "ok", JBool true
                                          "location", JStr here
                                          "x", Json.num (fst tile)
                                          "y", Json.num (snd tile)
                                          "frames", Json.num frames
                                          "detail", JStr "位置一直没稳定（可能被地形挤动）" ]
                                )
                            else
                                None
                        elif frames > 240 then
                            Some(
                                JObj
                                    [ "ok", JBool false
                                      "error", JStr(sprintf "传送 4 秒后仍在 \"%s\"" here) ]
                            )
                        else
                            None)

            Ops.await travel 7000
        | _ ->
            // 校验没过：把它的错误原样带出去（去掉内部字段）
            match precheck with
            | JObj fields ->
                JObj(fields |> List.filter (fun (k, _) -> not (k.StartsWith "_")))
            | other -> other

    /// 睡觉。`stay=true` = 就地睡（调用方负责先站到床边）。
    /// 走的是游戏自己的 `answerDialogueAction("Sleep_Yes")`，不是绕过去改日期。
    let sleep (stay: bool) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let before = loc.Name

                try
                    player.isInBed.Value <- true
                    loc.answerDialogueAction("Sleep_Yes", [||]) |> ignore

                    DelayedAction.functionAfterDelay(
                        (fun () -> Game1.netReady.SetLocalReady("sleep", true)),
                        1500
                    )

                    // 睡完把 isInBed 清掉：我们手工置过位，游戏的跨日流程不一定会清
                    DelayedAction.functionAfterDelay(
                        (fun () ->
                            if not (isNull Game1.player) then
                                Game1.player.isInBed.Value <- false),
                        15000
                    )

                    JObj
                        [ "ok", JBool true
                          "action", JStr(if stay then "sleeping_in_place" else "sleeping")
                          "location", JStr before
                          "note", JStr "已发起睡眠；跨日结算需要几秒，用 /status 的 time 观察" ]
                with ex ->
                    JObj [ "ok", JBool false; "error", JStr ex.Message ])
            3000

    /// /click —— 和 NagiBridge 同语义：**先把 OS 光标挪过去**再 `receiveLeftClick`。
    ///
    /// 为什么不能复用 `/menu/click`：`TitleMenu` 的 Load 按钮要求光标真的落在按钮上
    /// （`TitleMenu.receiveLeftClick` 在淡入期还会静默吞点击），只调 `receiveLeftClick(x,y)`
    /// 而不挪光标，点击会被丢掉。插件 `game.js` 的注释记着同一条。
    ///
    /// `no_move=true` 时不碰 OS 光标（后台操作、或用坐标只在菜单里点点）。
    /// 点完之后「到底谁被点中了」——每个控件的名字 / visible / 矩形 / 是否含这个点。
    ///
    /// 没有这一行，`/click` 只能告诉你"我调过 receiveLeftClick 了"，点没点中还得靠猜。
    /// 真机上就是靠它定位到：`ClickableComponent.containsPoint` **先看 `visible`**，
    /// 控件不可见时坐标算得再准也一律 false。
    let private hitTest (menu: obj) (x: int) (y: int) : J =
        try
            let field = menu.GetType().GetField("buttons", reflectionFlags)

            match (if isNull field then null else field.GetValue menu) with
            | :? System.Collections.IEnumerable as items ->
                let list = ResizeArray<J>()

                for item in items do
                    if not (isNull item) then
                        let t = item.GetType()

                        let name =
                            match t.GetField("name", reflectionFlags) with
                            | null -> "?"
                            | f -> string (f.GetValue item)

                        let visible =
                            match t.GetField("visible", reflectionFlags) with
                            | null -> true
                            | f ->
                                match f.GetValue item with
                                | :? bool as b -> b
                                | _ -> true

                        let bounds =
                            match t.GetField("bounds", reflectionFlags) with
                            | null -> Rectangle()
                            | f ->
                                match f.GetValue item with
                                | :? Rectangle as r -> r
                                | _ -> Rectangle()

                        list.Add(
                            JObj
                                [ "name", JStr name
                                  "visible", JBool visible
                                  "x", Json.num bounds.X
                                  "y", Json.num bounds.Y
                                  "w", Json.num bounds.Width
                                  "h", Json.num bounds.Height
                                  "contains", JBool(bounds.Contains(x, y)) ]
                        )

                JArr(List.ofSeq list)
            | _ -> JNull
        with _ ->
            JNull

    /// /click —— 在 UI 坐标上点一下；给 `component` 时按**控件名**点。
    let click (x: int) (y: int) (noMove: bool) (component: string) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player

                let cx = if x >= 0 then x else Game1.viewport.Width / 2
                let cy = if y >= 0 then y else Game1.viewport.Height / 2

                if not noMove then
                    try
                        Game1.setMousePosition(cx, cy)
                    with _ ->
                        ()

                match Game1.activeClickableMenu with
                | null ->
                    // 没菜单：优先推进事件，其次走动作键（等同"点一下世界"）
                    let loc = if isNull player then null else player.currentLocation

                    if not (isNull loc) && not (isNull loc.currentEvent) then
                        loc.currentEvent.receiveActionPress(0, 0) |> ignore

                        JObj
                            [ "ok", JBool true
                              "action", JStr "event_advance"
                              "x", Json.num cx
                              "y", Json.num cy ]
                    else
                        try
                            Game1.pressActionButton(
                                Game1.input.GetKeyboardState(),
                                Game1.input.GetMouseState(),
                                Game1.input.GetGamePadState()
                            )
                            |> ignore
                        with _ ->
                            ()

                        JObj
                            [ "ok", JBool true
                              "action", JStr "action_button"
                              "x", Json.num cx
                              "y", Json.num cy ]
                | menu when not (String.IsNullOrWhiteSpace component) ->
                    // 按**控件名**点：直接调菜单自己的 `performButtonAction(name)`，
                    // 绕开 `containsPoint` / `visible` / 空鼠标那一串前置条件。
                    // 标题界面这种「坐标算得对、点下去没反应」的界面，这是唯一稳的路子。
                    let invoked =
                        try
                            match menu.GetType().GetMethod("performButtonAction", reflectionFlags) with
                            | null -> false
                            | m ->
                                m.Invoke(menu, [| box component |]) |> ignore
                                true
                        with _ ->
                            false

                    JObj
                        [ "ok", JBool invoked
                          "action", JStr "menu_button"
                          "menu", Json.str (menu.GetType().Name)
                          "component", JStr component
                          "detail", JStr(if invoked then "已按控件名触发" else "这个菜单没有 performButtonAction") ]
                | menu ->
                    let hits = hitTest menu cx cy
                    let snapshot () =
                        let read (name: string) =
                            try
                                match menu.GetType().GetField(name, reflectionFlags) with
                                | null -> JNull
                                | f ->
                                    match f.GetValue menu with
                                    | null -> JNull
                                    | :? string as s -> JStr s
                                    | :? bool as b -> JBool b
                                    | :? int as i -> Json.num i
                                    | v when v.GetType().IsEnum -> JStr(string v)
                                    | v -> JStr(string v)
                            with _ ->
                                JNull

                        let sub = subMenuOf menu

                        JObj
                            [ "whichSubMenu", read "whichSubMenu"
                              "isTransitioningButtons", read "isTransitioningButtons"
                              "buttonsDX", read "buttonsDX"
                              "titleInPosition", read "titleInPosition"
                              "showButtonsTimer", read "showButtonsTimer"
                              "subMenu", JStr(if isNull sub then "" else sub.GetType().Name) ]

                    let before = snapshot ()
                    menu.receiveLeftClick(cx, cy, true)
                    let after = snapshot ()

                    JObj
                        [ "ok", JBool true
                          "action", JStr "menu_click"
                          "menu", Json.str (menu.GetType().Name)
                          "hitTest", hits
                          "before", before
                          "after", after
                          "x", Json.num cx
                          "y", Json.num cy ])
            3000

    /// /focus —— 把游戏窗口顶到前台（失焦会暂停，驱动 UI 前必须做）。
    ///
    /// 成败**看 `Game1.game1.IsActive`**，不看 Win32 返回值：
    /// 真机实测 SetForegroundWindow 经常回 0（窗口本来就在前面），
    /// 而 GetForegroundWindow 又可能是 SMAPI 的控制台窗口 —— 两个都不可靠。
    /// IsActive 才是"这一下点击会不会生效"的直接信号，且要等几帧才更新。
    let focus () : J =
        // 试三发：Windows 的前台锁是"逐次"的，第一发常在窗口还没刷帧时被吞掉。
        let mutable detail = "还没试"

        let attempt =
            Ops.run (fun frames ->
                if frames = 1 || frames = 20 || frames = 40 then
                    detail <- Native.focusAttempt ()
                    None
                else
                    let foreground = Native.isForeground ()

                    if foreground || frames > 70 then
                        Some(
                            JObj
                                [ "ok", JBool foreground
                                  "action", JStr "focus"
                                  "detail",
                                  JStr(
                                      if foreground then detail
                                      else "试过切前台，但前台窗口仍不是游戏（" + detail + "）"
                                  ) ]
                        )
                    else
                        None)

        Ops.await attempt 8000

    /// /screen —— 窗口 / 光标 / 当前菜单按钮的**真实矩形**。
    ///
    /// 为什么需要：点不动的时候分不清是「坐标不对」还是「时机不对」，只能靠猜。
    /// 这一套数字一问就明白：UI 空间多大、光标到底落在哪、按钮矩形是不是我们点的那一格。
    /// ⚠️ 别拿 `isActive` 当焦点判据（可能被别的 mod 补丁成恒定 true），看 `foreground`。
    let screen () : J =
        let vp = Game1.viewport
        let ui = Game1.uiViewport

        let mouse =
            try
                let ms = Game1.input.GetMouseState()
                JObj [ "x", Json.num ms.X; "y", Json.num ms.Y ]
            with _ ->
                JNull

        let menu = Game1.activeClickableMenu

        JObj
            [ "ok", JBool true
              "uiViewport", JObj [ "w", Json.num ui.Width; "h", Json.num ui.Height ]
              "viewport",
              JObj
                  [ "x", Json.num vp.X
                    "y", Json.num vp.Y
                    "w", Json.num vp.Width
                    "h", Json.num vp.Height ]
              "mouse", mouse
              "handle", JNum(float (int64 (Native.gameHandle ())))
              "foregroundHandle", JNum(float (int64 (Native.foregroundHandle ())))
              "foreground", JBool(Native.isForeground ())
              "isActive", JBool(try Game1.game1.IsActive with _ -> false)
              "menu",
              JObj
                  [ "open", JBool(not (isNull menu))
                    "type", Json.str (if isNull menu then "" else menu.GetType().Name)
                    "components", (if isNull menu then JArr [] else Probe.componentsOf menu)
                    "subMenu", (if isNull menu then JNull else describeSubMenu menu) ] ]
