namespace JunimoBridge

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Xna.Framework
open StardewValley

/// 走位。
///
/// 两段式：先用游戏自己的 `isTilePassable` 跑一遍 BFS 求路径（**在游戏线程上算**），
/// 再交给 `Ops` 一帧走一格。这样既不会穿墙，也不会有「寻路失败还硬走直线」那种 bug。
module Walk =

    /// 四方向 BFS。终点即使不可站也允许（走到它旁边算到达，由调用方看 ok/arrived）。
    let private findPath (loc: GameLocation) (start: Point) (goal: Point) : Point list option =
        try
            let layer = loc.map.Layers.[0]
            let w, h = layer.LayerWidth, layer.LayerHeight

            if w <= 0 || h <= 0 then
                None
            elif start.X < 0 || start.Y < 0 || start.X >= w || start.Y >= h then
                None
            else
                let visited = Array2D.create w h false
                let parent = Array2D.create w h (Point(-1, -1))
                let queue = Queue<Point>()
                queue.Enqueue start
                visited.[start.X, start.Y] <- true
                let mutable found = start = goal
                let mutable guard = 0

                while queue.Count > 0 && not found && guard < 30000 do
                    guard <- guard + 1
                    let cur = queue.Dequeue()

                    for dx, dy in [ 1, 0; -1, 0; 0, 1; 0, -1 ] do
                        let nx, ny = cur.X + dx, cur.Y + dy

                        if nx >= 0 && ny >= 0 && nx < w && ny < h && not visited.[nx, ny] then
                            let passable =
                                if nx = goal.X && ny = goal.Y then true
                                else
                                    try
                                        loc.isTilePassable(xTile.Dimensions.Location(nx, ny), Game1.viewport)
                                    with _ ->
                                        false

                            if passable then
                                visited.[nx, ny] <- true
                                parent.[nx, ny] <- cur
                                queue.Enqueue(Point(nx, ny))

                                if nx = goal.X && ny = goal.Y then
                                    found <- true

                if not found then
                    None
                else
                    // 从终点回溯
                    let path = ResizeArray<Point>()
                    let mutable cur = goal

                    while cur <> start do
                        path.Add cur
                        let p = parent.[cur.X, cur.Y]

                        if p.X < 0 then
                            cur <- start
                        else
                            cur <- p

                    path.Reverse()
                    Some(List.ofSeq path)
        with _ ->
            None

    /// 一帧走一格的 stepper。`gap` 是每格之间停几帧（4 ≈ 12 格/秒，看起来像快走）。
    let private stepper (path: Point list) (goal: Point) (timeoutFrames: int) =
        let points = Array.ofList path
        let mutable index = 0
        let mutable gap = 0

        fun (frames: int) ->
            if frames > timeoutFrames then
                Some(
                    JObj
                        [ "ok", JBool false
                          "error", JStr "走位超时"
                          "x", Json.num (Game1.player.TilePoint.X)
                          "y", Json.num (Game1.player.TilePoint.Y) ]
                )
            elif index >= points.Length then
                let tile = Game1.player.TilePoint

                Some(
                    JObj
                        [ "ok", JBool true
                          "arrived", JBool(tile = goal)
                          "x", Json.num tile.X
                          "y", Json.num tile.Y
                          "steps", Json.num points.Length ]
                )
            elif gap > 0 then
                gap <- gap - 1
                None
            else
                let step = points.[index]
                let player = Game1.player
                // X 不对齐到格中心：Tile 由 bbox 中心算 ((X+32)/64)，X 加半格会把落点推到下一格（NagiBridge 的教训）
                player.Position <- Vector2(float32 (step.X * 64), float32 (step.Y * 64) + 32.0f)
                index <- index + 1
                gap <- 4

                if index >= points.Length then
                    let tile = player.TilePoint

                    Some(
                        JObj
                            [ "ok", JBool true
                              "arrived", JBool(tile = goal)
                              "x", Json.num tile.X
                              "y", Json.num tile.Y
                              "steps", Json.num points.Length ]
                    )
                else
                    None

    /// 终点站不住时退而求其次：找最近的一个能站的邻格。
    let private resolveGoal (loc: GameLocation) (x: int) (y: int) =
        let mapW = loc.map.Layers.[0].LayerWidth
        let mapH = loc.map.Layers.[0].LayerHeight

        let passable (tx: int) (ty: int) =
            tx >= 0
            && ty >= 0
            && tx < mapW
            && ty < mapH
            && (try
                    loc.isTilePassable(xTile.Dimensions.Location(tx, ty), Game1.viewport)
                with _ ->
                    false)

        if passable x y then
            Some(Point(x, y), true)
        else
            [ 0, -1; 0, 1; -1, 0; 1, 0; -1, -1; 1, -1; -1, 1; 1, 1 ]
            |> List.tryPick (fun (dx, dy) -> if passable (x + dx) (y + dy) then Some(Point(x + dx, y + dy), false) else None)

    /// 请求走位。路径在游戏线程上算，走的过程由 Ops 每帧推进。
    let run (x: int) (y: int) (timeoutMs: int) : J =
        let plan =
            Frame.post (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let start = player.TilePoint

                match resolveGoal loc x y with
                | None -> Error(sprintf "(%d,%d) 站不住，旁边也没有能站的地方" x y)
                | Some(goal, exact) ->
                    if start = goal then
                        Ok([], goal, exact)
                    else
                        match findPath loc start goal with
                        | Some path -> Ok(path, goal, exact)
                        | None -> Error(sprintf "从 (%d,%d) 到 (%d,%d) 找不到可走的路径" start.X start.Y goal.X goal.Y))

        if not (plan.Wait 1500) then
            JObj [ "ok", JBool false; "error", JStr "求路径超时" ]
        else
            match plan.Result with
            | Error message -> JObj [ "ok", JBool false; "error", JStr message ]
            | Ok(path, goal, exact) ->
                let baseFields =
                    if exact then
                        []
                    else
                        [ "requested", JObj [ "x", Json.num x; "y", Json.num y ]
                          "reason", JStr "目标格站不住，走到最近的可站格" ]

                if path.IsEmpty then
                    JObj(
                        [ "ok", JBool true
                          "arrived", JBool exact
                          "x", Json.num goal.X
                          "y", Json.num goal.Y
                          "steps", Json.num 0 ]
                        @ baseFields
                    )
                else
                    let walking = Ops.run (stepper path goal (max 30 (timeoutMs / 17)))
                    let result = Ops.await walking timeoutMs

                    match result with
                    | JObj fields -> JObj(fields @ baseFields)
                    | other -> other
