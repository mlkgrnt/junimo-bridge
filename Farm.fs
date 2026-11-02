namespace JunimoBridge

open System
open Microsoft.Xna.Framework
open StardewValley

/// 批量农活（`/tool_area`）。
///
/// 和 NagiBridge 的差别要说清楚：它是「真蓄力挥动、一次覆盖一片」，
/// 我们这版是**逐格挥**——正确但慢（每格约 40 帧）。先保证结果对，再谈提速。
module Farm =

    let private tileHasTarget (loc: GameLocation) (op: string) (x: int) (y: int) =
        let key = Vector2(float32 x, float32 y)

        if op = "till" then
            // 必须真的是可耕地：只看「没特征、没物件」会把木地板也算进去（真机抓到：
            // 在 FarmHouse 里 /tool_area 回了 swings:4，其实一刀都没落地）。
            let diggable =
                try
                    // 返回的是属性值（string），不是 bool
                    not (isNull(loc.doesTileHaveProperty(x, y, "Diggable", "Back")))
                with _ ->
                    false

            diggable
            && not (loc.terrainFeatures.ContainsKey key)
            && not (loc.Objects.ContainsKey key)
        else
            // water：只浇「有作物且没浇」的耕地
            match loc.terrainFeatures.TryGetValue key with
            | true, (:? StardewValley.TerrainFeatures.HoeDirt as dirt) -> dirt.state.Value <> 1
            | _ -> false

    /// 矩形批量农活（till / water）。
    ///
    /// 实现选择：**逐格 Begin/End**，每格等 38 帧让生效帧落地，然后按地形特征回读。
    /// 试过「按住使用键 0.7s 让游戏自己蓄力挥一片」，但在无真实窗口焦点/连续输入时
    /// 游戏会中途取消这次使用（真机实测：60 秒里一条地也没锄出来）。宁可慢，不要假成功。
    let toolArea (operation: string) (x1: int) (y1: int) (x2: int) (y2: int) : J =
        let op = if operation = "water" then "water" else "till"
        let ax, bx = min x1 x2, max x1 x2
        let ay, by = min y1 y2, max y1 y2

        let remaining () =
            let loc = Game1.player.currentLocation
            [ for x in ax..bx do
                  for y in ay..by do
                      if tileHasTarget loc op x y then
                          yield Point(x, y) ]

        let initial =
            Frame.post (fun () ->
                // 必须先把对应的工具拿到手上：/use 之所有能锄，是因为那时手里真是锄头。
                // 少了这一步，BeginUsingTool 挥的是斧头 —— 62 挥 0 落地就是这么来的。
                let player = Game1.player

                let want =
                    if op = "till" then
                        player.Items |> Seq.tryFindIndex (fun i -> i :? StardewValley.Tools.Hoe)
                    else
                        player.Items |> Seq.tryFindIndex (fun i -> i :? StardewValley.Tools.WateringCan)

                match want with
                | Some index when index < 12 -> player.CurrentToolIndex <- index
                | Some index ->
                    let first = player.Items.[0]
                    player.Items.[0] <- player.Items.[index]
                    player.Items.[index] <- first
                    player.CurrentToolIndex <- 0
                | None -> ()

                remaining ())

        if not (initial.Wait 2000) then
            JObj [ "ok", JBool false; "error", JStr "求目标格超时" ]
        else if initial.Result.IsEmpty then
            JObj
                [ "ok", JBool false
                  "error", JStr(if op = "till" then "No diggable tiles nearby" else "No unwatered crops nearby") ]
        else
            let before = initial.Result.Length
            let mutable swings = 0
            let mutable doneCount = 0
            let mutable index = 0
            let mutable wait = 0
            let mutable guard = 0

            let step (_frames: int) =
                guard <- guard + 1
                let todo = remaining ()

                if todo.IsEmpty || guard > 2400 then
                    Some(
                        JObj
                            [ "ok", JBool true
                              "operation", JStr op
                              "swings", Json.num swings
                              "out_of_water", JBool false ]
                    )
                elif wait > 0 then
                    wait <- wait - 1
                    None
                else
                    let anchor = todo.[0]
                    let player = Game1.player
                    let px, py = player.TilePoint.X, player.TilePoint.Y

                    // 工具只作用于**正前方那一格**。所以要先把人挪到这个格子的四邻里，
                    // 再转过去挥；直接朝斜角挥是打不中的（真机实测：62 挥 0 落地）。
                    let stand =
                        [ px, py ]                                   // 已经在正前方就原地挥
                        @ [ anchor.X, anchor.Y + 1
                            anchor.X, anchor.Y - 1
                            anchor.X - 1, anchor.Y
                            anchor.X + 1, anchor.Y ]
                        |> List.distinct
                        |> List.tryFind (fun (sx, sy) ->
                            let adjacent =
                                (abs (sx - anchor.X) + abs (sy - anchor.Y)) = 1

                            adjacent
                            && (try
                                    player.currentLocation.isTilePassable(
                                        xTile.Dimensions.Location(sx, sy), Game1.viewport)
                                with _ ->
                                    false))

                    match stand with
                    | None -> None
                    | Some(sx, sy) ->
                        if (px, py) <> (sx, sy) then
                            player.Position <- Vector2(float32 (sx * 64), float32 (sy * 64) + 32.0f)
                        else
                            let dx = anchor.X - px
                            let dy = anchor.Y - py

                            player.FacingDirection <-
                                if abs dx >= abs dy then
                                    (if dx >= 0 then 1 else 3)
                                else if dy >= 0 then 2
                                else 0

                            try
                                player.BeginUsingTool()
                                player.EndUsingTool()
                            with _ ->
                                ()

                            swings <- swings + 1

                            let left = remaining ()
                            doneCount <- doneCount + max 0 (todo.Length - left.Length)
                            wait <- 38

                        None

            let task = Ops.run step
            let result = Ops.await task 45000

            let left = Frame.post remaining

            let leftList =
                if left.Wait 1000 then left.Result
                else []

            let fields =
                match result with
                | JObj fs -> fs |> List.filter (fun (k, _) -> k <> "still_missing")
                | _ -> []

            JObj(
                fields
                @ [ "requested", Json.num before
                    "patches", Json.num (max 0 (before - leftList.Length))
                    "still_missing",
                    JArr(
                        leftList
                        |> List.map (fun p -> JObj [ "x", Json.num p.X; "y", Json.num p.Y; "reason", JStr "没落地" ])
                    ) ]
            )
