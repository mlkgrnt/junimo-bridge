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

    /// null → 空串。游戏里 `NetString.Value` 在"从没设过"时**就是 null**（不是 ""），
    /// 直接塞进 JSON 会让序列化抛 NRE，整个响应变成一句 "Object reference not set"。
    let private nz (s: string) = if isNull s then "" else s

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
    /// 这一格能不能站人。
    ///
    /// 只用 `loc.isTilePassable` 是不够的 —— 它只查地图图层，**不查物体/建筑**
    /// （真机：畜棚 141 个物体格里 129 格会谎报可走）。所以补两层：物体占格 + 建筑占位方形。
    /// 和 `/walk_to` 走的是同一套判据，地图不再"看着能走、走起来穿墙"。
    let private passableAt (loc: GameLocation) (x: int) (y: int) : bool =
        safe
            (fun () ->
                let v = Microsoft.Xna.Framework.Vector2(float32 x, float32 y)

                loc.isTilePassable v
                && not (loc.Objects.ContainsKey v)
                && (try not (loc.IsTileBlockedBy(v, StardewValley.CollisionMask.Buildings)) with _ -> true))
            false

    /// 一格的描述。`None` = 这格没什么可说的（可走空地不占篇幅）。
    ///
    /// ⚠️ 单独一层 + 调用处 try/with：扫描端点里**一格的怪数据不该毁掉整张图**。
    ///    真机上就是被某一格的 NRE 整片打空，只剩一句 "Object reference not set"，
    ///    既不知道哪一格、也拿不到别的格子 —— 比少报一格糟得多。
    let private tileJson (loc: GameLocation) (tx: int) (ty: int) : J option =
        let v = Microsoft.Xna.Framework.Vector2(float32 tx, float32 ty)
        let passable = passableAt loc tx ty

        let obj =
            match loc.Objects.TryGetValue v with
            | true, o when not (isNull o) -> Some o
            | _ -> None

        // 和游戏 `makeHoeDirt` 同口径：有 Diggable 属性、且没被建筑占格
        let diggable =
            not (isNull (loc.doesTileHaveProperty(tx, ty, "Diggable", "Back")))
            && not (loc.IsTileBlockedBy(v, StardewValley.CollisionMask.Buildings))

        let mutable terrainName: string option = None
        let mutable watered = false
        let mutable cropId: string option = None
        let mutable cropPhase = -1
        let mutable harvestable = false
        let mutable cropName: string option = None
        let mutable fert: string option = None

        match loc.terrainFeatures.TryGetValue v with
        | true, (:? StardewValley.TerrainFeatures.HoeDirt as dirt) ->
            terrainName <- Some "HoeDirt"
            watered <- (safe (fun () -> dirt.state.Value) 0) = 1
            fert <- Some(nz (safe (fun () -> dirt.fertilizer.Value) ""))

            if not (isNull dirt.crop) then
                let id = nz (safe (fun () -> dirt.crop.indexOfHarvest.Value) "")
                cropId <- Some id
                cropPhase <- safe (fun () -> dirt.crop.currentPhase.Value) 0
                harvestable <- safe (fun () -> dirt.readyForHarvest()) false

                if nz id <> "" then
                    cropName <-
                        Some(
                            safe
                                (fun () ->
                                    match StardewValley.ItemRegistry.Create(id, 1) with
                                    | null -> ""
                                    | it -> it.DisplayName)
                                ""
                        )
        | true, other when not (isNull other) -> terrainName <- Some(other.GetType().Name)
        | _ -> ()

        // 大地形（灌木/树桩在 largeTerrainFeatures，不在 terrainFeatures）
        let large =
            safe (fun () -> loc.largeTerrainFeatures |> Seq.tryFind (fun f -> not (isNull f) && f.Tile = v)) None

        let resource =
            safe
                (fun () ->
                    loc.resourceClumps
                    |> Seq.tryFind (fun c ->
                        not (isNull c)
                        && (c.Tile = v
                            || (tx >= int c.Tile.X
                                && tx < int c.Tile.X + int c.width.Value
                                && ty >= int c.Tile.Y
                                && ty < int c.Tile.Y + int c.height.Value)))
                    |> Option.map (fun c -> string c.parentSheetIndex.Value))
                None

        let hasInfo =
            (not passable)
            || obj.IsSome
            || terrainName.IsSome
            || large.IsSome
            || resource.IsSome
            || diggable

        if not hasInfo then
            None
        else
            let fields = ResizeArray<string * J>()
            fields.Add("x", Json.num tx)
            fields.Add("y", Json.num ty)
            fields.Add("passable", JBool passable)

            if diggable then
                fields.Add("diggable", JBool true)

            match obj with
            | Some o ->
                fields.Add("object", Json.str(nz o.Name))
                fields.Add("objId", Json.str(nz o.QualifiedItemId))
                fields.Add("objCategory", Json.num o.Category)
                fields.Add("bigCraftable", JBool(safe (fun () -> o.bigCraftable.Value) false))
                fields.Add("forage", JBool(safe (fun () -> o.isForage()) false))
            | None -> ()

            match terrainName with
            | Some t -> fields.Add("terrain", JStr(nz t))
            | None -> ()

            match large with
            | Some f ->
                fields.Add("largeTerrain", JStr(f.GetType().Name))

                match f with
                | :? StardewValley.TerrainFeatures.Bush as bush ->
                    // 能不能摇 = 游戏 `Bush.shake()` 那句原条件（`!townBush && readyForHarvest && inBloom`）
                    let inSeason = safe (fun () -> bush.inBloom()) false

                    fields.Add(
                        "bushShakeable",
                        JBool(safe (fun () -> not bush.townBush.Value && bush.readyForHarvest() && inSeason) false)
                    )

                    fields.Add("bushInSeason", JBool inSeason)
                    fields.Add("bushSize", Json.num(safe (fun () -> bush.size.Value) 0))
                | _ -> ()
            | None -> ()

            match resource with
            | Some r -> fields.Add("resource", JStr(nz r))
            | None -> ()

            match cropId with
            | Some c ->
                fields.Add("crop", JStr(nz c))
                fields.Add("cropPhase", Json.num cropPhase)
                fields.Add("harvestable", JBool harvestable)
            | None -> ()

            match cropName with
            | Some n when nz n <> "" -> fields.Add("cropName", JStr(nz n))
            | _ -> ()

            if watered then
                fields.Add("watered", JBool true)

            match fert with
            | Some f when nz f <> "" -> fields.Add("fert", JStr(nz f))
            | _ -> ()

            Some(JObj(List.ofSeq fields))

    /// 扫一圈（或一块矩形）地皮。字段名和 NagiBridge 对齐，客户端 `game_state.ml` / `look` 直接吃。
    ///
    /// 只报「有意思的格子」：可走空地不占篇幅 —— 一张 80×65 的农场全量吐出来光 JSON 就几百 KB，
    /// 而 AI 关心的永远是"这格上有什么"。
    let surroundings (radius: int) (rect: (int * int * int * int) option) : J =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let loc = player.currentLocation
            let cx, cy = player.TilePoint.X, player.TilePoint.Y
            let mapW = safe (fun () -> loc.Map.DisplayWidth / 64) 0
            let mapH = safe (fun () -> loc.Map.DisplayHeight / 64) 0
            let r = max 1 (min 30 radius)

            let bounds =
                match rect with
                | Some(a, b, c, d) -> Some(min a c, min b d, max a c, max b d)
                | None -> None

            let tooBig =
                match bounds with
                | Some(lx, ly, hx, hy) -> (int64 (hx - lx + 1)) * (int64 (hy - ly + 1)) > 4096L
                | None -> false

            if tooBig then
                let (lx, ly, hx, hy) = bounds.Value
                // 面积超限**明确报错**，不静默截断：静默截断会让调用方以为"这块地就这些"
                JObj
                    [ "ok", JBool false
                      "error",
                      JStr(
                          sprintf
                              "矩形 %dx%d 超过上限 4096 格——分几次扫（别指望它静默截断）"
                              (hx - lx + 1)
                              (hy - ly + 1)
                      ) ]
            else
                let x1, y1, x2, y2, rectMode =
                    match bounds with
                    | Some(lx, ly, hx, hy) -> (lx, ly, hx, hy, true)
                    | None -> (cx - r, cy - r, cx + r, cy + r, false)

                let startX = max 0 x1
                let startY = max 0 y1
                let endX = (if mapW > 0 then min (mapW - 1) x2 else x2)
                let endY = (if mapH > 0 then min (mapH - 1) y2 else y2)

                let tiles = ResizeArray<J>()

                for ty in startY .. endY do
                    for tx in startX .. endX do
                        // 这一格读崩了就当它没信息，继续扫 —— 一格坏不该毁掉整张图
                        match (try tileJson loc tx ty with _ -> None) with
                        | Some j -> tiles.Add j
                        | None -> ()

                // 范围内的人和怪（`kind` 让上层分清 NPC / 宠物 / 马）
                let inScope (ex: int) (ey: int) =
                    ex >= startX && ex <= endX && ey >= startY && ey <= endY

                let characters =
                    safe
                        (fun () ->
                            loc.characters
                            |> Seq.filter (fun c -> not (isNull c) && inScope c.TilePoint.X c.TilePoint.Y)
                            |> List.ofSeq)
                        []

                let kin = characters |> List.filter (fun c -> not (c :? StardewValley.Monsters.Monster))

                let npcs =
                    kin
                    |> List.map (fun c ->
                        JObj
                            [ "name", Json.str c.Name
                              "x", Json.num c.TilePoint.X
                              "y", Json.num c.TilePoint.Y
                              // 用类型名判，别 :? 具体类（Pet/Horse 的命名空间在版本间挪过）
                              "kind",
                              JStr(
                                  match c.GetType().Name with
                                  | "Pet" -> "pet"
                                  | "Horse" -> "horse"
                                  | _ -> "npc"
                              ) ])

                let monsters =
                    characters
                    |> List.choose (fun c ->
                        match c with
                        | :? StardewValley.Monsters.Monster as m ->
                            Some(
                                JObj
                                    [ "name", Json.str m.Name
                                      "x", Json.num m.TilePoint.X
                                      "y", Json.num m.TilePoint.Y
                                      "health", Json.num m.Health
                                      "maxHealth", Json.num m.MaxHealth ]
                            )
                        | _ -> None)

                JObj
                    [ "ok", JBool true
                      "center", JObj [ "x", Json.num cx; "y", Json.num cy ]
                      "radius", Json.num r
                      "rect_mode", JBool rectMode
                      "scanned",
                      JObj
                          [ "x1", Json.num startX
                            "y1", Json.num startY
                            "x2", Json.num endX
                            "y2", Json.num endY ]
                      "location", Json.str loc.Name
                      "tiles", JArr(List.ofSeq tiles)
                      "npcs", JArr npcs
                      "monsters", JArr monsters ]

    /// /scan_chests —— 当前地图上所有箱子（含位置与内容）。
    /// 口径全在 `Chests`：那些箱子算不算仓库、叫什么、打什么标签，一处写死。
    let scanChests () : J =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            JObj [ "ok", JBool false; "error", JStr "World not ready" ]
        else
            let loc = player.currentLocation

            let chests =
                Chests.collectStorageChests loc
                |> List.map (fun (box, tile, label) -> Chests.chestJson box tile label loc.Name)

            JObj
                [ "ok", JBool true
                  "location", Json.str loc.Name
                  "count", Json.num chests.Length
                  "chests", JArr chests ]

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
              "caps",
              JObj
                  [ "backgroundControl", JBool Runtime.backgroundControl
                    "foreground", JBool(safe (fun () -> Native.isForeground ()) false)
                    "minimized", JBool(safe (fun () -> Native.isMinimized ()) false) ] ]
