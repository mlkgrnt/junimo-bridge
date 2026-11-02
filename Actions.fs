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

    /// 用一次手里的工具。等生效帧过去（约 550ms）再回读目标格，避免假成功。
    let useTool () : J =
        let effect =
            Ops.run (fun frames ->
                let player = Game1.player

                if frames = 1 then
                    try
                        player.BeginUsingTool()
                        player.EndUsingTool()
                    with _ ->
                        ()

                    None
                elif frames < 10 then
                    None
                elif frames < 120 && (player.UsingTool || frames < 40) then
                    // 动画没走完，或者还没到生效帧（40 帧 ≈ 660ms），继续等
                    None
                else
                    let loc = player.currentLocation

                    Some(
                        JObj
                            [ "ok", JBool true
                              "frames", Json.num frames
                              "stillUsing", JBool player.UsingTool
                              "target", tileSnapshot loc (facingPoint player) ]
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
                            Some(
                                JObj
                                    [ "ok", JBool true
                                      "location", JStr here
                                      "x", Json.num player.TilePoint.X
                                      "y", Json.num player.TilePoint.Y
                                      "frames", Json.num frames ]
                            )
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
    let click (x: int) (y: int) (noMove: bool) : J =
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
                | menu ->
                    menu.receiveLeftClick(cx, cy, true)

                    JObj
                        [ "ok", JBool true
                          "action", JStr "menu_click"
                          "menu", Json.str (menu.GetType().Name)
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
        let attempt =
            Ops.run (fun frames ->
                if frames = 1 then
                    let detail = Native.focusAttempt ()
                    (if String.IsNullOrEmpty detail then () else ())
                    None
                elif frames < 4 then
                    None
                else
                    let active =
                        try
                            Game1.game1.IsActive
                        with _ ->
                            false

                    if active || frames > 40 then
                        Some(
                            JObj
                                [ "ok", JBool active
                                  "action", JStr "focus"
                                  "detail",
                                  JStr(
                                      if active then "已在前台"
                                      else "试过切前台，但游戏仍报告未激活（可能被别的窗口压着）"
                                  ) ]
                        )
                    else
                        None)

        Ops.await attempt 4000
