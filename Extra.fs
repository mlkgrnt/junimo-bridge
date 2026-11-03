namespace JunimoBridge

open System
open Microsoft.Xna.Framework
open StardewValley

/// 补齐 NagiBridge 那批动作端点：瞬移 / 按键 / 暂停 / 吃 / 打水 / 收菜 / 买卖。
module Extra =

    let private facingPoint (player: Farmer) = Actions.facingPoint player

    /// /position —— 直接改坐标（瞬移）。只在落点能站时才动，避免把人塞进墙里。
    let position (x: int) (y: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation

                let standable =
                    try
                        loc.isTilePassable(xTile.Dimensions.Location(x, y), Game1.viewport)
                    with _ ->
                        false

                if not standable then
                    JObj [ "ok", JBool false; "error", JStr(sprintf "(%d,%d) 站不住" x y) ]
                else
                    player.Position <- Vector2(float32 (x * 64), float32 (y * 64) + 32.0f)

                    JObj
                        [ "ok", JBool true
                          "action", JStr "positioned"
                          "location", Json.str loc.Name
                          "x", Json.num x
                          "y", Json.num y ])
            2000

    let private pressConfirm () =
        let player = Game1.player
        let loc = if isNull player then null else player.currentLocation

        if not (isNull loc) && not (isNull loc.currentEvent) then
            loc.currentEvent.receiveActionPress(0, 0) |> ignore
        else
            match Game1.activeClickableMenu with
            | :? StardewValley.Menus.DialogueBox as dlg -> dlg.receiveLeftClick(dlg.xPositionOnScreen + dlg.width / 2, dlg.yPositionOnScreen + dlg.height / 2, true)
            | null -> Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState()) |> ignore
            | menu -> menu.receiveLeftClick(Game1.viewport.Width / 2, Game1.viewport.Height / 2, true)

    let private skipAll () =
        let player = Game1.player
        let loc = if isNull player then null else player.currentLocation

        if not (isNull loc) && not (isNull loc.currentEvent) then
            loc.currentEvent.skipped <- true
            loc.currentEvent.skipEvent()
        else
            match Game1.activeClickableMenu with
            | null -> ()
            | menu -> menu.receiveKeyPress(Microsoft.Xna.Framework.Input.Keys.Escape)

    /// 当前"看得见的状态"：有没有菜单、站在哪、朝哪。`/key` 用它做回读。
    let private keySnapshot () : J =
        let player = Game1.player

        let menu =
            match Game1.activeClickableMenu with
            | null -> ""
            | m -> m.GetType().Name

        JObj
            [ "menu", JStr menu
              "x", Json.num (if isNull player then -1 else player.TilePoint.X)
              "y", Json.num (if isNull player then -1 else player.TilePoint.Y)
              "facing", Json.num (if isNull player then -1 else player.FacingDirection) ]

    /// /key {key, count} —— 按键。
    ///
    /// 两条路，按语义分：
    ///   ① **语义键**（confirm/action/ok、skip）走"直接驱动游戏"——模态界面里只能这么按；
    ///   ② **物理键**（方向键 / Esc·menu / space / enter / F1-12 / 数字…）走**虚拟输入**：
    ///      按住若干帧再松开，然后回读菜单与坐标。
    ///
    /// ⚠️ 以前这里是个假门：`| _ -> ()` 把绝大部分键名吞掉，却照样回 `ok:true` ——
    ///    `press_key {"key":"menu"}` 永远"成功"、菜单永远不开。现在不认识的键名直接报错。
    /// ⚠️ 另一条根因在补丁层：没键盘焦点时 `GetKeyboardState()` 返回空（见 `ForceKeyboardFocusPatch`），
    ///    所以"注入成功"还必须回读，不能自说自话。
    let key (name: string) (count: int) : J =
        let n = max 1 (min 20 count)
        let k = name.Trim().ToLowerInvariant()

        let hasMenuOrEvent () =
            (not (isNull Game1.activeClickableMenu))
            || (not (isNull Game1.player) && not (isNull Game1.player.currentLocation)
                && not (isNull Game1.player.currentLocation.currentEvent))

        match k with
        | "confirm"
        | "action"
        | "ok" ->
            Ops.awaitMain
                (fun () ->
                    for _ in 1 .. n do
                        pressConfirm ()

                    let snap = keySnapshot ()
                    JObj [ "ok", JBool true; "key", JStr name; "count", Json.num n; "after", snap ])
                8000
        | "skip" ->
            Ops.awaitMain
                (fun () ->
                    skipAll ()
                    JObj [ "ok", JBool true; "key", JStr name; "count", Json.num 1; "after", keySnapshot () ])
                8000
        // Esc：有菜单/事件时"退出/跳过"用直接驱动；什么都没开时才注入——那才是"打开菜单"。
        | "escape"
        | "esc"
        | "menu" when hasMenuOrEvent () ->
            Ops.awaitMain
                (fun () ->
                    for _ in 1 .. n do
                        skipAll ()

                    JObj [ "ok", JBool true; "key", JStr name; "count", Json.num n; "after", keySnapshot () ])
                8000
        | _ when not (Input.canInject k) ->
            JObj
                [ "ok", JBool false
                  "error", JStr(sprintf "不认识的键名 \"%s\"（没有可注入的映射）" name)
                  "hint",
                  JStr
                      "可用：confirm/action/ok、skip、escape/menu/esc、cancel、up/down/left/right、w/a/s/d、space、enter、tab、backspace、q/e/c/x/f、0-9、f1-f12。方向名和 cancel 会解析成**游戏内实际绑定**（默认 up/down/left/right = W/S/A/D，cancel = Esc）—— 箭头键在 SDV 里默认没绑，直接注入等于按空气。" ]
        | _ ->
            // 移动键按住才走（时间短了可能一步都没迈出去）；其余键一个"轻点"就够（边沿触发）
            let holdMs = if Input.isMoveKey k then 420 else 90

            let before =
                match Ops.awaitMain (fun () -> keySnapshot ()) 2000 with
                | JObj _ as j -> j
                | _ -> JNull

            // 语义方向名 / cancel → 游戏里**实际绑定**的键。方向键默认没绑，只能跟着 options 走。
            let resolved =
                match k with
                | "up"
                | "down"
                | "left"
                | "right"
                | "cancel" ->
                    Input.resolvedButtonKeys k |> List.map string |> String.concat ","
                | _ -> ""

            let mutable taps = n
            let mutable phase = 0 // 0=该按了 1=等松开 2=松开的间隔
            let mutable gap = 0

            let effect =
                Ops.run (fun frames ->
                    match phase with
                    | 0 ->
                        Input.hold [ k ] holdMs |> ignore
                        taps <- taps - 1
                        phase <- 1
                        gap <- 0
                        None
                    | 1 when (Input.current ()).Keys.IsEmpty ->
                        // 已经松开了：还有次数就隔两帧再按一次（保证"松开→按下"的边沿成立）
                        if taps > 0 then
                            phase <- 2
                            gap <- 0
                            None
                        else
                            phase <- 3
                            None
                    | 2 ->
                        gap <- gap + 1

                        if gap >= 2 then
                            phase <- 0

                        None
                    | _ ->
                        if frames < 4 then
                            None
                        else
                            let after = keySnapshot ()

                            let dx, dy =
                                match before with
                                | JObj fs ->
                                    let g key =
                                        fs
                                        |> List.tryFind (fun (k2, _) -> k2 = key)
                                        |> Option.bind (snd >> Json.asNumber)
                                        |> Option.defaultValue 0.0
                                        |> int

                                    match after with
                                    | JObj fs2 ->
                                        let g2 key =
                                            fs2
                                            |> List.tryFind (fun (k2, _) -> k2 = key)
                                            |> Option.bind (snd >> Json.asNumber)
                                            |> Option.defaultValue 0.0
                                            |> int

                                        (g2 "x" - g "x", g2 "y" - g "y")
                                    | _ -> (0, 0)
                                | _ -> (0, 0)

                            Some(
                                JObj
                                    [ "ok", JBool true
                                      "key", JStr name
                                      "count", Json.num n
                                      "held_ms", Json.num holdMs
                                      "before", before
                                      "after", after
                                      "moved", JObj [ "dx", Json.num dx; "dy", Json.num dy ]
                                      "resolvedKeys",
                                      (if resolved = "" then
                                           JNull
                                       else
                                           JStr resolved)
                                      // 注入真的落地了吗？两条都读得到才算通
                                      "observedKeys", Json.str Input.observedKeys
                                      "injectedKeys", Json.num Runtime.injectedKeys ]
                            ))

            Ops.await effect (1500 * n + 3000)

    /// /pause —— 冻住游戏时间（和 NagiBridge 一样是 mod 侧的冻结，不动游戏的 paused 标志）。
    let pause () : J =
        Runtime.timeFrozen <- true
        Runtime.frozenAt <- Game1.timeOfDay
        JObj [ "ok", JBool true; "action", JStr "paused"; "frozenAt", Json.num Game1.timeOfDay ]

    let resume () : J =
        Runtime.timeFrozen <- false
        JObj [ "ok", JBool true; "action", JStr "resumed" ]

    /// /refill —— 把水壶灌满（离线补给，不去水边）。
    let refill () : J =
        Ops.awaitMain
            (fun () ->
                let can =
                    Game1.player.Items
                    |> Seq.tryFind (fun i -> i :? StardewValley.Tools.WateringCan)

                match can with
                | Some(:? StardewValley.Tools.WateringCan as wc) ->
                    wc.WaterLeft <- wc.waterCanMax

                    JObj
                        [ "ok", JBool true
                          "water", Json.num wc.WaterLeft
                          "max", Json.num wc.waterCanMax ]
                | _ -> JObj [ "ok", JBool false; "error", JStr "背包里没有浇水壶" ])
            2000

    let private safeEdible (o: StardewValley.Object) =
        try
            o.Edibility
        with _ ->
            0

    /// /eat —— 吃手里的东西。
    let eat () : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player

                match player.CurrentItem with
                | :? StardewValley.Object as food when safeEdible food > 0 ->
                    try
                        player.eatObject(food)
                        player.reduceActiveItemByOne()

                        JObj
                            [ "ok", JBool true
                              "ate", Json.str food.Name
                              "healthRecovered", Json.num (food.healthRecoveredOnConsumption())
                              "staminaRecovered", JNum(float (food.staminaRecoveredOnConsumption())) ]
                    with ex ->
                        JObj [ "ok", JBool false; "error", JStr ex.Message ]
                | null -> JObj [ "ok", JBool false; "error", JStr "手上没东西" ]
                | _ -> JObj [ "ok", JBool false; "error", JStr "当前物品不可食用（先 /select 选个食物）" ])
            3000

    /// /harvest?radius=N —— 收半径内所有成熟的作物（和 NagiBridge 同一套判据）。
    let harvest (radius: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let r = max 1 (min 50 radius)
                let px, py = player.TilePoint.X, player.TilePoint.Y
                let mutable count = 0

                try
                    for kv in loc.terrainFeatures.Pairs do
                        match kv.Value with
                        | :? StardewValley.TerrainFeatures.HoeDirt as dirt when not (isNull dirt.crop) && dirt.readyForHarvest() ->
                            let tx = int kv.Key.X
                            let ty = int kv.Key.Y

                            if Math.Abs(tx - px) <= r && Math.Abs(ty - py) <= r then
                                if dirt.crop.harvest(tx, ty, dirt) then
                                    dirt.destroyCrop(false)
                                    count <- count + 1
                        | _ -> ()
                with _ ->
                    ()

                JObj [ "ok", JBool true; "harvested", Json.num count ])
            8000

    /// /ripen?radius=N —— 把半径内**还没熟**的作物一键催熟（测试/摆场用）。
    /// 只催没熟的：已经能收的不动，否则会把"收完再催"的节奏打乱。
    let ripen (radius: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let r = max 1 (min 50 radius)
                let px, py = player.TilePoint.X, player.TilePoint.Y
                let mutable count = 0

                try
                    for kv in loc.terrainFeatures.Pairs do
                        match kv.Value with
                        | :? StardewValley.TerrainFeatures.HoeDirt as dirt when
                            not (isNull dirt.crop) && not (dirt.readyForHarvest())
                            ->
                            let tx = int kv.Key.X
                            let ty = int kv.Key.Y

                            if Math.Abs(tx - px) <= r && Math.Abs(ty - py) <= r then
                                dirt.crop.growCompletely()
                                count <- count + 1
                        | _ -> ()
                with _ ->
                    ()

                JObj [ "ok", JBool true; "ripened", Json.num count ])
            8000

    // ---- 箱子（按坐标，和 NagiBridge 一致）----
    //
    // 口径（哪些容器算仓库 / 叫什么 / 标签）全在 `Chests`，这里只管「按坐标找到它，然后搬东西」。
    // 别在这里直接查 `loc.Objects`：内置冰箱不在里面，只查它就会「列得出、打不着」。

    let private chestAt (x: int) (y: int) =
        Chests.findAt Game1.player.currentLocation x y

    /// 物品和名字对不对得上：中英混双 —— 上层可能传 `Parsnip Seeds`，也可能传「防风草种子」。
    let private nameMatches (item: Item) (name: string) =
        String.IsNullOrWhiteSpace name
        || (not (isNull item.Name) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        || (not (isNull item.DisplayName) && item.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
        || (not (isNull item.QualifiedItemId) && item.QualifiedItemId.Equals(name, StringComparison.OrdinalIgnoreCase))

    /// 星级只对**有品质的**件成立。非 `Object`（工具/武器/帽/靴/戒/衣）一律放行 ——
    /// 否则 `as Object` 恒 null、`null != 0` 恒成立，带 quality 就永远跳过它们。
    let private qualityOk (item: Item) (quality: int) =
        if quality < 0 then
            true
        else
            match item with
            | :? StardewValley.Object as o -> o.Quality = quality
            | _ -> true

    /// /chest {x,y} —— 读一个箱子（不打开箱子、同地图任意距离）。
    /// 回 `items[].{name,count,slot}` + `used`/`capacity`，和 NagiBridge 同形。
    let chest (x: int) (y: int) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                | Some box ->
                    JObj
                        [ "ok", JBool true
                          "items", Chests.itemsJson box
                          "capacity", Json.num (Chests.capacity box)
                          "used", Json.num box.Items.Count ])
            2000

    /// /store {x,y,name?,count?,slot?,quality?,keepTools} —— 放进箱子。
    /// 从背包**从后往前**扫（先塞末尾那些不常用的），`keepTools` 默认 true；
    /// 容量交给 `Chest.addItem` 自己尊重，不手写「< 70」那种魔数。
    let store (x: int) (y: int) (name: string) (count: int) (slot: int) (quality: int) (keepTools: bool) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                | Some box ->
                    let items = Game1.player.Items
                    let stored = ResizeArray<J>()
                    let mutable remaining = if count <= 0 then Int32.MaxValue else count

                    for i in items.Count - 1 .. -1 .. 0 do
                        if remaining > 0 && not (isNull items.[i]) then
                            let item = items.[i]

                            let matches =
                                (slot < 0 || i = slot)
                                && (not keepTools || not (item :? Tool))
                                && qualityOk item quality
                                && nameMatches item name

                            if matches then
                                let original = item.Stack
                                let take = if remaining = Int32.MaxValue then original else min remaining original
                                let toStore = item.getOne()
                                toStore.Stack <- take
                                let leftover = box.addItem toStore
                                let moved = take - (if isNull leftover then 0 else leftover.Stack)

                                if moved > 0 then
                                    item.Stack <- original - moved

                                    if item.Stack <= 0 then
                                        items.[i] <- null

                                    stored.Add(JObj [ "item", Json.str item.Name; "count", Json.num moved ])

                                    if remaining <> Int32.MaxValue then
                                        remaining <- remaining - moved

                    JObj
                        [ "ok", JBool true
                          "stored", JArr(List.ofSeq stored)
                          "count", Json.num stored.Count
                          "chestAt", JObj [ "x", Json.num x; "y", Json.num y ] ])
            4000

    /// /chest_take {x,y,name?,slot?,count?}
    /// /chest_take {x,y,name?,slot?,count?,quality?}
    /// 回执必须是 `taken`（**实际到手数量**）+ `item`（**真拿到手的那件**的名字）——
    /// 上层读的就是这两个键；写成 `took`/`count` 会让「取成功」被报成「一样没取到」。
    let chestTake (x: int) (y: int) (name: string) (slot: int) (count: int) (quality: int) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                // 两种指法都没给 = 调用方没说清要拿什么。别兜底成「全拿」：那只是把问题挪到下游
                //（同名两摞时拿错还报成功）。
                | Some box when slot < 0 && String.IsNullOrWhiteSpace name ->
                    JObj [ "ok", JBool false; "error", JStr "要么给 name（物品名），要么给 slot（箱子里的格号）" ]
                | Some box ->
                    let player = Game1.player
                    let target = if count <= 0 then Int32.MaxValue else count
                    let mutable taken = 0
                    let mutable takenName = ""
                    let mutable full = false

                    for i in 0 .. box.Items.Count - 1 do
                        if not full && taken < target && not (isNull box.Items.[i]) then
                            let item = box.Items.[i]

                            if (slot < 0 || i = slot) && qualityOk item quality && nameMatches item name then
                                if takenName = "" then
                                    takenName <- item.Name

                                let original = item.Stack

                                let want =
                                    if target = Int32.MaxValue then original
                                    else min (target - taken) original

                                let toGive = item.getOne()
                                toGive.Stack <- want
                                let leftover = player.addItemToInventory toGive
                                let kept = if isNull leftover then 0 else leftover.Stack
                                let moved = want - kept

                                if moved > 0 then
                                    item.Stack <- original - moved

                                    if item.Stack <= 0 then
                                        box.Items.[i] <- null

                                    taken <- taken + moved

                                // 背包满了，剩下的留箱子里 —— 别继续往下扫，那只会白跑
                                if kept > 0 then
                                    full <- true

                    JObj
                        [ "ok", JBool true
                          "taken", Json.num taken
                          // 走 slot 时调用方压根没给 name，原样回空名字等于什么都没说
                          "item", Json.str (if String.IsNullOrWhiteSpace name then takenName else name)
                          "slot", Json.num slot
                          "chestAt", JObj [ "x", Json.num x; "y", Json.num y ] ])
            3000

    /// /chest_take_list {items:[{name,count?}|"name"]} —— 一次取齐清单上的东西。
    /// 只从「够得着」（同图 ≤ `Chests.reachTiles` 格）的箱子里凑；够不着的**不静默丢弃** ——
    /// 一律进 `tooFar` 带坐标，上层走过去再叫一次。
    let chestTakeList (body: string) : J =
        let requests =
            match Json.parse body with
            | Some value ->
                match Json.tryField "items" value with
                | Some(JArr items) ->
                    items
                    |> List.choose (fun item ->
                        match item with
                        | JStr s when not (String.IsNullOrWhiteSpace s) -> Some(s, 0)
                        | JObj fields ->
                            let count =
                                fields
                                |> List.tryFind (fun (k, _) -> k = "count")
                                |> Option.bind (snd >> Json.asNumber)

                            fields
                            |> List.tryFind (fun (k, _) -> k = "name")
                            |> Option.bind (snd >> Json.asString)
                            |> Option.filter (fun n -> not (String.IsNullOrWhiteSpace n))
                            |> Option.map (fun n -> n, (match count with Some c -> int c | None -> 0))
                        | _ -> None)
                | _ -> []
            | None -> []

        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let tooFar = ResizeArray<J>()

                let near =
                    Chests.collectStorageChests loc
                    |> List.filter (fun (box, tile, label) ->
                        if Chests.withinReach player loc tile then
                            true
                        else
                            tooFar.Add(
                                JObj
                                    [ "x", Json.num (int tile.X)
                                      "y", Json.num (int tile.Y)
                                      "name", Json.str (Chests.displayName box label) ]
                            )

                            false)

                let results = ResizeArray<J>()

                for reqName, wanted in requests do
                    let target = if wanted <= 0 then Int32.MaxValue else wanted
                    let mutable taken = 0
                    let from = ResizeArray<J>()

                    for box, tile, label in near do
                        if taken < target then
                            for i in 0 .. box.Items.Count - 1 do
                                if taken < target && not (isNull box.Items.[i]) then
                                    let item = box.Items.[i]

                                    if nameMatches item reqName then
                                        let original = item.Stack

                                        let want =
                                            if target = Int32.MaxValue then original
                                            else min (target - taken) original

                                        if want > 0 then
                                            let toGive = item.getOne()
                                            toGive.Stack <- want
                                            let leftover = player.addItemToInventory toGive
                                            let moved = want - (if isNull leftover then 0 else leftover.Stack)

                                            if moved > 0 then
                                                item.Stack <- original - moved

                                                if item.Stack <= 0 then
                                                    box.Items.[i] <- null

                                                from.Add(
                                                    JObj
                                                        [ "x", Json.num (int tile.X)
                                                          "y", Json.num (int tile.Y)
                                                          "name", Json.str (Chests.displayName box label)
                                                          "color", Json.str (Chests.colorHex box)
                                                          "autoTag", Json.str (Chests.autoTag box)
                                                          "got", Json.num moved ]
                                                )

                                                taken <- taken + moved

                    results.Add(
                        JObj
                            [ "item", JStr reqName
                              "wanted", Json.num (if wanted <= 0 then -1 else target)
                              "taken", Json.num taken
                              "from", JArr(List.ofSeq from) ]
                    )

                JObj
                    [ "ok", JBool true
                      "location", Json.str loc.Name
                      "items", JArr(List.ofSeq results)
                      "tooFar", JArr(List.ofSeq tooFar)
                      "reachTiles", Json.num Chests.reachTiles ])
            8000

    /// /buy {id,quantity} —— 按售价 ×2 计价直接进货（NagiBridge 同款语义）。
    let buy (id: string) (quantity: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let qty = max 1 (min 999 quantity)

                try
                    let item = StardewValley.ItemRegistry.Create(id, qty)

                    if isNull item then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "Unknown item ID: %s" id) ]
                    else
                        let unit = max 1 (item.salePrice() * 2)
                        let cost = unit * qty

                        if player.Money < cost then
                            JObj
                                [ "ok", JBool false
                                  "error", JStr(sprintf "钱不够：需要 %d，身上 %d" cost player.Money) ]
                        else
                            let added = player.addItemToInventoryBool(item, false)

                            if not added then
                                JObj [ "ok", JBool false; "error", JStr "背包放不下" ]
                            else
                                player.Money <- player.Money - cost

                                JObj
                                    [ "ok", JBool true
                                      "bought", Json.str item.Name
                                      "quantity", Json.num qty
                                      "unitPrice", Json.num unit
                                      "totalCost", Json.num cost
                                      "remainingGold", Json.num player.Money ]
                with ex ->
                    JObj [ "ok", JBool false; "error", JStr ex.Message ])
            3000

    /// /sell {all,name?} —— 丢进农场的出货箱（必须有 Farm + 出货箱）。
    let sell (all: bool) (name: string) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation

                let bin =
                    match loc with
                    | :? Farm as farm -> Some(farm.getShippingBin(player))
                    | _ -> None

                match bin with
                | None
                | Some null -> JObj [ "ok", JBool false; "error", JStr "No shipping bin found (must be on Farm)" ]
                | Some bin ->
                    let sold = ResizeArray<J>()

                    for i in 0 .. player.Items.Count - 1 do
                        let item = player.Items.[i]

                        if not (isNull item) then
                            let isTool = item :? Tool

                            let matches =
                                not isTool
                                && (String.IsNullOrWhiteSpace name
                                    || item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))

                            if matches then
                                bin.Add(item)
                                player.Items.[i] <- null
                                sold.Add(JStr item.Name)

                            elif all && not isTool && String.IsNullOrWhiteSpace name then
                                ()

                    JObj
                        [ "ok", JBool true
                          "sold", JArr(List.ofSeq sold)
                          "count", Json.num sold.Count ])
            4000

    /// /gift {target,item} —— 送礼。先把东西拿到手上，再走游戏自己的
    /// `tryToReceiveActiveObject`（这一版里没有 `TryToReceiveGift`）。
    let gift (target: string) (itemName: string) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation

                let npc =
                    loc.characters
                    |> Seq.tryFind (fun c ->
                        not (isNull c)
                        && ((not (isNull c.Name)
                             && c.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (not (isNull c.displayName)
                                && c.displayName.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)))

                match npc with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "本图找不到 \"%s\"" target) ]
                | Some n ->
                    let index =
                        let mutable hit = -1

                        for i in 0 .. player.Items.Count - 1 do
                            let it = player.Items.[i]

                            // ⚠️ 先判空再取名字：背包里的**空格就是 null**，
                            //    老写法 `it.Name.Equals(...)` 会在空格上当场 NRE ——
                            //    而这里正是"送礼永远报 Object reference not set"的真凶。
                            let same =
                                if isNull it then
                                    false
                                else
                                    // 中英混双 + 限定 ID：上层可能传 `Parsnip`、也可能传「防风草」
                                    let nm = if isNull it.Name then "" else it.Name
                                    let dn = if isNull it.DisplayName then "" else it.DisplayName
                                    let qid = if isNull it.QualifiedItemId then "" else it.QualifiedItemId

                                    nm.Equals(itemName, StringComparison.OrdinalIgnoreCase)
                                    || dn.Equals(itemName, StringComparison.OrdinalIgnoreCase)
                                    || qid.Equals(itemName, StringComparison.OrdinalIgnoreCase)

                            if hit < 0 && same && not (it :? Tool) then
                                hit <- i

                        hit

                    if index < 0 then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "背包里没有 \"%s\"" itemName) ]
                    else
                        try
                            // 拿到手上（超过 12 格的先换到 0 格）
                            if index >= 12 then
                                let first = player.Items.[0]
                                player.Items.[0] <- player.Items.[index]
                                player.Items.[index] <- first
                                player.CurrentToolIndex <- 0
                            else
                                player.CurrentToolIndex <- index

                            // 先算口味（love/like/neutral/dislike/hate），再真的送出去
                            let asObj = player.CurrentItem :?> StardewValley.Object

                            // getGiftTasteForThisItem 返回口味码：0 爱 / 2 喜欢 / 6 一般 / 8 不喜欢 / 4 讨厌
                            let taste =
                                try
                                    match n.getGiftTasteForThisItem(asObj) with
                                    | 0 -> "love"
                                    | 2 -> "like"
                                    | 8 -> "dislike"
                                    | 4 -> "hate"
                                    | _ -> "neutral"
                                with _ ->
                                    "?"

                            // ⚠️ 别用 `player.Items.Count` —— 那是**背包容量**（恒定 36），永远不变，
                            //    于是 `consumed` 恒 false（"东西送出去了却报没消耗"，正是我们要避免的那种谎报）。
                            //    数**非空格子**才对。
                            let filled () =
                                player.Items |> Seq.filter (fun i -> not (isNull i)) |> Seq.length

                            let before = filled ()

                            // ⚠️ 两道门必须先过：
                            //   ① 好感度记录**不一定存在**（从没搭过话的 NPC 就没有），
                            //      `receiveGift` 内部直接索引 `friendshipData[npc]` ⇒ KeyNotFound
                            //      （真机：`The given key 'Abigail' was not present`）。
                            //   ② 本周送满了/不可社交时游戏自己会拒绝，提前问清楚能给出人话错误。
                            if not (player.friendshipData.ContainsKey n.Name) then
                                player.friendshipData.[n.Name] <- StardewValley.Friendship(0)

                            if not (n.CanReceiveGifts()) then
                                JObj
                                    [ "ok", JBool false
                                      "error", JStr(sprintf "%s 现在不能收礼（不可社交或本周已送满）" n.Name) ]
                            elif not (asObj.canBeGivenAsGift()) then
                                JObj [ "ok", JBool false; "error", JStr(sprintf "%s 不能作为礼物" asObj.Name) ]
                            else
                                let pointsBefore = player.friendshipData.[n.Name].Points
                                let giftsBefore = player.friendshipData.[n.Name].GiftsToday

                                // ⚠️ `showMessage` 必须传 false：传 true 会走"弹对话框"那条路，
                                //    而我们是在主线程回调里**没有对话上下文**地调它 —— 真机直接
                                //    `Object reference not set`（包在 AggregateException 里）。
                                //    玩家看得见的台词由我们自己那层报告给，不靠游戏弹。
                                n.receiveGift(asObj, player, true, 1.0f, false) |> ignore

                                JObj
                                    [ "ok", JBool true
                                      "npc", Json.str n.Name
                                      "item", Json.str asObj.Name
                                      "taste", JStr taste
                                      "points", Json.num player.friendshipData.[n.Name].Points
                                      "pointsBefore", Json.num pointsBefore
                                      "giftsToday", Json.num player.friendshipData.[n.Name].GiftsToday
                                      "giftsTodayBefore", Json.num giftsBefore
                                      "consumed", JBool(filled () < before) ]
                        with ex ->
                            // AggregateException 的外层话没信息量，把内层那句和抛点带上
                            let inner =
                                if isNull ex.InnerException then "" else " ← " + ex.InnerException.Message

                            let frame =
                                match ex.StackTrace with
                                | null -> ""
                                | st ->
                                    st.Split('\n')
                                    |> Array.tryFind (fun l -> l.Contains "at ")
                                    |> Option.map (fun l -> " | " + l.Trim())
                                    |> Option.defaultValue ""

                            JObj [ "ok", JBool false; "error", JStr(ex.Message + inner + frame) ])
            4000
