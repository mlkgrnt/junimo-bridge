namespace JunimoBridge

open System
open StardewValley

/// 兼容层：按 NagiBridge 的报文形状输出，供现有 OCaml 客户端直接解析。
///
/// 为什么单独一层：客户端 `game_state.ml` 读的是 `player.currentTool` / `time.dayOfMonth`
/// / `location.mapWidth` / `inventory[].category`（中文字符串）这些具体键名。改客户端要动
/// 十几个文件，改这里只要动这一层。
module Compat =

    let private safe (value: unit -> 'a) (fallback: 'a) =
        try
            value ()
        with _ ->
            fallback

    /// 和 NagiBridge 的 item 形状对齐（客户端只吃 name/stack/category/waterLeft/waterMax，
    /// 其余是为了 `get_inventory` 显示得更全）。
    let itemJson (slot: int) (item: Item) : J =
        let water =
            match item with
            | :? StardewValley.Tools.WateringCan as can -> [ "waterLeft", Json.num can.WaterLeft; "waterMax", Json.num can.waterCanMax ]
            | _ -> []

        JObj(
            [ "name", Json.str item.Name
              "displayName", Json.str item.DisplayName
              "itemId", Json.str item.QualifiedItemId
              "stack", Json.num item.Stack
              "category", Json.str (safe (fun () -> item.getCategoryName()) "")
              "catNum", Json.num item.Category
              "quality", Json.num item.Quality
              "value", Json.num (safe (fun () -> item.salePrice()) 0)
              "isTool", JBool(item :? Tool)
              "slotIndex", Json.num slot ]
            @ water
        )

    let private inventoryJson (player: Farmer) : J =
        let items = ResizeArray<J>()

        for i in 0 .. player.Items.Count - 1 do
            let item = player.Items.[i]

            if not (isNull item) then
                items.Add(itemJson i item)

        JArr(List.ofSeq items)

    let private fishingJson (player: Farmer) : J =
        match player.CurrentTool with
        | :? StardewValley.Tools.FishingRod as rod ->
            let bx =
                if rod.bobber.X = 0.0f && rod.bobber.Y = 0.0f then JNull
                else Json.num (int (rod.bobber.X / 64.0f))

            let by =
                if rod.bobber.X = 0.0f && rod.bobber.Y = 0.0f then JNull
                else Json.num (int (rod.bobber.Y / 64.0f))

            JObj
                [ "isCasting", JBool rod.isTimingCast
                  "isFishing", JBool rod.isFishing
                  "isNibbling", JBool rod.isNibbling
                  "isReeling", JBool rod.isReeling
                  "hit", JBool rod.hit
                  "bobberX", bx
                  "bobberY", by
                  "castingPower", JNum(float rod.castingPower)
                  "clearWaterDistance", Json.num rod.clearWaterDistance
                  "fishingLevel", Json.num player.FishingLevel ]
        | _ -> JNull

    let private rodJson (player: Farmer) : J =
        let rod: Item option =
            match player.CurrentTool with
            | :? StardewValley.Tools.FishingRod as r -> Some(r :> Item)
            | _ -> player.Items |> Seq.tryFind (fun i -> i :? StardewValley.Tools.FishingRod)

        match rod with
        | Some(:? StardewValley.Tools.FishingRod as r) -> JObj [ "name", Json.str r.Name ]
        | _ -> JNull

    let private weatherCode () =
        if Game1.isLightning then 2
        elif Game1.isRaining then 1
        elif Game1.isSnowing then 5
        elif Game1.isDebrisWeather then 3
        else 0

    let private menuJson () =
        let menu = Game1.activeClickableMenu

        if isNull menu then
            JNull
        else
            JObj [ "type", JStr(menu.GetType().Name) ]

    let private eventJson () =
        let loc = Game1.currentLocation

        if isNull loc || isNull loc.currentEvent then
            JNull
        else
            JObj
                [ "id", Json.str loc.currentEvent.id
                  "skippable", JBool loc.currentEvent.skippable
                  "message", Json.str (Actions.menuReadDialogue ()) ]

    /// POST/GET /state —— NagiBridge 形状。
    let state () : J =
        let player = Game1.player

        if not StardewModdingAPI.Context.IsWorldReady || isNull player then
            JObj [ "ok", JBool true; "worldReady", JBool false ]
        else
            let loc = player.currentLocation

            JObj
                [ "ok", JBool true
                  "worldReady", JBool true
                  "player",
                  JObj
                      [ "name", Json.str player.Name
                        "x", Json.num player.TilePoint.X
                        "y", Json.num player.TilePoint.Y
                        "health", Json.num player.health
                        "maxHealth", Json.num player.maxHealth
                        "stamina", JNum(float player.Stamina)
                        "maxStamina", Json.num player.MaxStamina
                        "money", Json.num player.Money
                        "clubCoins", Json.num 0
                        "minigame", Json.str (safe (fun () -> if isNull Game1.currentMinigame then "" else Game1.currentMinigame.GetType().Name) "")
                        "currentTool", Json.str (if isNull player.CurrentTool then "" else player.CurrentTool.Name)
                        "currentItem", Json.str (if isNull player.CurrentItem then "" else player.CurrentItem.Name)
                        "currentItemId", Json.str (if isNull player.CurrentItem then "" else player.CurrentItem.QualifiedItemId)
                        "currentItemCat", Json.num (if isNull player.CurrentItem then 0 else player.CurrentItem.Category)
                        "currentToolUpgrade", Json.num (match player.CurrentTool with :? Tool as t -> t.UpgradeLevel | _ -> 0)
                        "facingDirection", Json.num player.FacingDirection
                        "isMale", JBool player.IsMale
                        "dailyLuck", JNum(safe (fun () -> player.DailyLuck) 0.0)
                        "maxItems", Json.num (safe (fun () -> player.MaxItems) 36)
                        "isMoving", JBool(safe (fun () -> player.movementDirections.Count > 0) false)
                        "isInBed", JBool player.isInBed.Value
                        "fishCaught", Json.num (safe (fun () -> player.fishCaught.Count()) 0)
                        "fishing", fishingJson player
                        "rod", rodJson player
                        "toolUpgrading", Json.str (safe (fun () -> player.toolBeingUpgraded.Value.Name) "") ]
                  "location",
                  JObj
                      [ "name", Json.str (if isNull loc then "" else loc.Name)
                        "uniqueName", Json.str (if isNull loc then "" else loc.NameOrUniqueName)
                        "mapWidth", Json.num (safe (fun () -> loc.map.Layers.[0].LayerWidth) 0)
                        "mapHeight", Json.num (safe (fun () -> loc.map.Layers.[0].LayerHeight) 0) ]
                  "mailbox", JNull
                  "time",
                  JObj
                      [ "timeOfDay", Json.num Game1.timeOfDay
                        "dayOfMonth", Json.num Game1.dayOfMonth
                        "season", Json.str Game1.currentSeason
                        "year", Json.num Game1.year
                        "weather", Json.num (weatherCode ())
                        "isRaining", JBool Game1.isRaining
                        "isSnowing", JBool Game1.isSnowing
                        "isLightning", JBool Game1.isLightning
                        "isGreenRain", JBool Game1.isGreenRain ]
                  "farmType", Json.num (safe (fun () -> Game1.whichFarm) 0)
                  "activeMenu", menuJson ()
                  "activeEvent", eventJson ()
                  "in_dialogue", JBool(Game1.activeClickableMenu :? StardewValley.Menus.DialogueBox)
                  "npcs", JArr []
                  "pets", JArr []
                  "inventory", inventoryJson player
                  "otherPlayers", JArr [] ]

    /// /map —— 当前地图的建筑/传送点/NPC 概况。
    let map () : J =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let loc = player.currentLocation

            let warps =
                safe
                    (fun () ->
                        loc.warps
                        |> Seq.map (fun w ->
                            JObj
                                [ "x", Json.num w.X
                                  "y", Json.num w.Y
                                  "targetLocation", Json.str w.TargetName
                                  "targetX", Json.num w.TargetX
                                  "targetY", Json.num w.TargetY ])
                        |> List.ofSeq)
                    []

            let npcs =
                safe
                    (fun () ->
                        loc.characters
                        |> Seq.map (fun n -> JObj [ "name", Json.str n.Name; "x", Json.num n.TilePoint.X; "y", Json.num n.TilePoint.Y ])
                        |> List.ofSeq)
                    []

            JObj
                [ "ok", JBool true
                  "player", JObj [ "x", Json.num player.TilePoint.X; "y", Json.num player.TilePoint.Y ]
                  "location",
                  JObj
                      [ "name", Json.str loc.Name
                        "width", Json.num (safe (fun () -> loc.map.Layers.[0].LayerWidth) 0)
                        "height", Json.num (safe (fun () -> loc.map.Layers.[0].LayerHeight) 0) ]
                  "buildings",
                  JArr(
                      safe
                          (fun () ->
                              match loc with
                              | :? Farm as farm -> farm.buildings |> Seq.map (fun b -> JStr b.buildingType.Value) |> List.ofSeq
                              | _ -> [])
                          []
                  )
                  "warps", JArr warps
                  "npcs", JArr npcs
                  "farmers", JArr []
                  "animals", JArr [] ]

    /// /surroundings?radius=N —— 只列**不可通行**的格子（客户端 `look` 用它看障碍）。
    let surroundings (radius: int) : J =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let loc = player.currentLocation
            let r = max 1 (min 30 radius)
            let cx, cy = player.TilePoint.X, player.TilePoint.Y
            let tiles = ResizeArray<J>()

            for x in cx - r .. cx + r do
                for y in cy - r .. cy + r do
                    let passable =
                        safe (fun () -> loc.isTilePassable(xTile.Dimensions.Location(x, y), Game1.viewport)) false

                    if not passable then
                        tiles.Add(JObj [ "x", Json.num x; "y", Json.num y; "passable", JBool false ])

            JObj
                [ "ok", JBool true
                  "center", JObj [ "x", Json.num cx; "y", Json.num cy ]
                  "radius", Json.num r
                  "rect_mode", JBool false
                  "scanned",
                  JObj
                      [ "x1", Json.num (cx - r)
                        "y1", Json.num (cy - r)
                        "x2", Json.num (cx + r)
                        "y2", Json.num (cy + r) ]
                  "location", Json.str loc.Name
                  "tiles", JArr(List.ofSeq tiles) ]

    /// /scan_chests —— 当前地图上所有箱子（含位置与内容条数）。
    let scanChests () : J =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let loc = player.currentLocation
            let chests = ResizeArray<J>()

            safe
                (fun () ->
                    for kv in loc.Objects.Pairs do
                        match kv.Value with
                        | :? StardewValley.Objects.Chest as chest ->
                            let items = ResizeArray<J>()

                            for i in 0 .. chest.Items.Count - 1 do
                                let item = chest.Items.[i]

                                if not (isNull item) then
                                    items.Add(itemJson i item)

                            chests.Add(
                                JObj
                                    [ "x", Json.num (int kv.Key.X)
                                      "y", Json.num (int kv.Key.Y)
                                      "items", JArr(List.ofSeq items) ]
                            )
                        | _ -> ()
                    ())
                ()

            JObj
                [ "ok", JBool true
                  "location", Json.str loc.Name
                  "count", Json.num chests.Count
                  "chests", JArr(List.ofSeq chests) ]

    /// /friendship?npc=xx
    let friendship (npc: string) : J =
        let player = Game1.player

        if isNull player then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let hit =
                safe
                    (fun () ->
                        player.friendshipData.Pairs
                        |> Seq.tryFind (fun kv -> kv.Key.IndexOf(npc, StringComparison.OrdinalIgnoreCase) >= 0)
                        |> Option.map (fun kv -> kv.Key, kv.Value))
                    None

            match hit with
            | None ->
                JObj
                    [ "ok", JBool true
                      "npc", JStr npc
                      "points", Json.num 0
                      "hearts", Json.num 0
                      "giftsThisWeek", Json.num 0
                      "giftsToday", Json.num 0
                      "known", JBool false ]
            | Some(name, data) ->
                JObj
                    [ "ok", JBool true
                      "npc", JStr name
                      "points", Json.num data.Points
                      "hearts", Json.num (data.Points / 250)
                      "giftsThisWeek", Json.num data.GiftsThisWeek
                      "giftsToday", Json.num data.GiftsToday
                      "known", JBool true ]

    /// /find_npc?name=xx —— 全地图扫描。
    let findNpc (name: string) : J =
        let result = ResizeArray<J>()

        safe
            (fun () ->
                for loc in Game1.locations do
                    if not (isNull loc) then
                        for npc in loc.characters do
                            if not (isNull npc)
                               && (npc.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                                   || npc.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) then
                                result.Add(
                                    JObj
                                        [ "name", Json.str npc.Name
                                          "displayName", Json.str npc.displayName
                                          "location", Json.str loc.Name
                                          "locUnique", Json.str loc.NameOrUniqueName
                                          "x", Json.num npc.TilePoint.X
                                          "y", Json.num npc.TilePoint.Y
                                          "facing", Json.num npc.FacingDirection
                                          "isSleeping", JBool npc.isSleeping.Value
                                          "isVillager", JBool npc.IsVillager
                                          "dialogue", Json.str (safe (fun () -> npc.CurrentDialogue.Peek().getCurrentDialogue()) "") ]
                                )
                ())
            ()

        JObj
            [ "ok", JBool true
              "name", JStr name
              "count", Json.num result.Count
              "npcs", JArr(List.ofSeq result) ]

    /// /status —— 键名对齐，但 `server` 如实写我们自己。
    let status () : J =
        JObj
            [ "ok", JBool true
              "server", JStr "JunimoBridge"
              "version", JStr Runtime.modVersion
              "port", Json.num Runtime.port
              "worldReady", JBool StardewModdingAPI.Context.IsWorldReady
              "isMultiplayer", JBool(safe (fun () -> Game1.IsMultiplayer) false)
              "tick", JNum(float Runtime.tick)
              "uptimeMs", JNum(float (Runtime.uptimeMs ()))
              "pending", Json.num (Frame.pending ())
              "caps", JObj [] ]
