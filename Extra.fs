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

    /// /key —— 客户端只用到 confirm / skip / down 这几个语义键。
    let key (name: string) (count: int) : J =
        Ops.awaitMain
            (fun () ->
                let n = max 1 (min 20 count)
                let k = name.Trim().ToLowerInvariant()

                for _ in 1 .. n do
                    match k with
                    | "confirm"
                    | "action"
                    | "ok" -> pressConfirm ()
                    | "skip"
                    | "escape" -> skipAll ()
                    | "down" -> Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState()) |> ignore
                    | _ -> ()

                JObj [ "ok", JBool true; "key", JStr name; "count", Json.num count ])
            5000

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

    // ---- 箱子（按坐标，和 NagiBridge 一致）----

    let private chestAt (x: int) (y: int) =
        let loc = Game1.player.currentLocation

        try
            match loc.Objects.TryGetValue(Vector2(float32 x, float32 y)) with
            | true, o when (o :? StardewValley.Objects.Chest) -> Some(o :?> StardewValley.Objects.Chest)
            | _ -> None
        with _ ->
            None

    let chest (x: int) (y: int) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                | Some box ->
                    let items = ResizeArray<J>()

                    for i in 0 .. box.Items.Count - 1 do
                        let item = box.Items.[i]

                        if not (isNull item) then
                            items.Add(Compat.itemJson i item)

                    JObj
                        [ "ok", JBool true
                          "x", Json.num x
                          "y", Json.num y
                          "count", Json.num items.Count
                          "items", JArr(List.ofSeq items) ])
            2000

    /// /store {x,y,name?,keepTools} —— 放进箱子；keepTools 默认 true，工具不塞进去。
    let store (x: int) (y: int) (name: string) (keepTools: bool) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                | Some box ->
                    let items = Game1.player.Items
                    let stored = ResizeArray<J>()

                    for i in 0 .. items.Count - 1 do
                        let item = items.[i]

                        if not (isNull item) then
                            let isTool = item :? Tool

                            let matches =
                                if String.IsNullOrWhiteSpace name then
                                    not (keepTools && isTool)
                                else
                                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)

                            if matches && box.Items.Count < 70 then
                                box.Items.Add(item)
                                items.[i] <- null
                                stored.Add(JStr item.Name)

                    JObj
                        [ "ok", JBool true
                          "stored", JArr(List.ofSeq stored)
                          "count", Json.num stored.Count ])
            4000

    /// /chest_take {x,y,name?,slot?,count?}
    let chestTake (x: int) (y: int) (name: string) (slot: int) (count: int) : J =
        Ops.awaitMain
            (fun () ->
                match chestAt x y with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "No chest at (%d,%d)" x y) ]
                | Some box ->
                    let index =
                        if slot >= 0 && slot < box.Items.Count && not (isNull box.Items.[slot]) then
                            slot
                        else
                            let mutable hit = -1

                            for i in 0 .. box.Items.Count - 1 do
                                let item = box.Items.[i]

                                if hit < 0 && not (isNull item) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) then
                                    hit <- i

                            hit

                    if index < 0 then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "箱子里没有 \"%s\"" name) ]
                    else
                        let item = box.Items.[index]
                        let want = if count <= 0 then item.Stack else min count item.Stack
                        let original = item.Stack
                        item.Stack <- want
                        let added = Game1.player.addItemToInventoryBool(item, false)

                        if added then
                            if want >= original then
                                box.Items.[index] <- null

                            JObj [ "ok", JBool true; "took", Json.str item.Name; "count", Json.num want ]
                        else
                            item.Stack <- original
                            JObj [ "ok", JBool false; "error", JStr "背包放不下" ])
            3000

    /// /chest_take_list {items:[{name,count?}|"name"]} —— 按清单一次拿多件。
    let chestTakeList (body: string) : J =
        let names =
            match Json.parse body with
            | Some value ->
                match Json.tryField "items" value with
                | Some(JArr items) ->
                    items
                    |> List.choose (fun item ->
                        match item with
                        | JStr s -> Some(s, 0)
                        | JObj fields ->
                            let name =
                                fields |> List.tryFind (fun (k, _) -> k = "name") |> Option.bind (snd >> Json.asString)

                            let count =
                                fields |> List.tryFind (fun (k, _) -> k = "count") |> Option.bind (snd >> Json.asNumber)

                            name |> Option.map (fun n -> n, (match count with Some c -> int c | None -> 0))
                        | _ -> None)
                | _ -> []
            | None -> []

        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let results = ResizeArray<J>()

                for name, wanted in names do
                    // 在周围 4 格找箱子（客户端调用时人就在箱边）
                    let mutable box: StardewValley.Objects.Chest option = None

                    for dx in -2 .. 2 do
                        for dy in -2 .. 2 do
                            if box.IsNone then
                                let cx = player.TilePoint.X + dx
                                let cy = player.TilePoint.Y + dy

                                try
                                    match loc.Objects.TryGetValue(Vector2(float32 cx, float32 cy)) with
                                    | true, o when (o :? StardewValley.Objects.Chest) -> box <- Some(o :?> StardewValley.Objects.Chest)
                                    | _ -> ()
                                with _ ->
                                    ()

                    let taken = ResizeArray<string>()

                    match box with
                    | None -> ()
                    | Some chest ->
                        let mutable need = if wanted <= 0 then Int32.MaxValue else wanted

                        for i in 0 .. chest.Items.Count - 1 do
                            if need > 0 && not (isNull chest.Items.[i]) then
                                let item = chest.Items.[i]

                                if item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) then
                                    let original = item.Stack
                                    let take = min need original
                                    item.Stack <- take

                                    if player.addItemToInventoryBool(item, false) then
                                        taken.Add(item.Name)
                                        need <- need - take

                                        if take >= original then
                                            chest.Items.[i] <- null
                                    else
                                        item.Stack <- original

                        results.Add(
                            JObj
                                [ "item", JStr name
                                  "wanted", Json.num (if wanted <= 0 then Int32.MaxValue else wanted)
                                  "taken", Json.num taken.Count
                                  "from", JArr(taken |> Seq.map JStr |> List.ofSeq) ]
                        )

                JObj
                    [ "ok", JBool true
                      "location", Json.str loc.Name
                      "items", JArr(List.ofSeq results)
                      "tooFar", JArr []
                      "reachTiles", Json.num 4 ])
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
                        && (c.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
                            || c.displayName.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0))

                match npc with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "本图找不到 \"%s\"" target) ]
                | Some n ->
                    let index =
                        let mutable hit = -1

                        for i in 0 .. player.Items.Count - 1 do
                            let it = player.Items.[i]

                            if hit < 0 && not (isNull it) && not (it :? Tool)
                               && it.Name.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0 then
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

                            let before = player.Items.Count

                            n.receiveGift(asObj, player, true, 1.0f, true) |> ignore

                            JObj
                                [ "ok", JBool true
                                  "npc", Json.str n.Name
                                  "item", Json.str asObj.Name
                                  "taste", JStr taste
                                  "consumed", JBool(player.Items.Count < before) ]
                        with ex ->
                            JObj [ "ok", JBool false; "error", JStr ex.Message ])
            4000
