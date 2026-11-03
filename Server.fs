namespace JunimoBridge

open System
open StardewValley
open System.IO
open System.Net
open System.Text
open System.Threading

/// HTTP 层。只在 localhost:7850 上说话，不做鉴权（本地回环，和 NagiBridge 同样的取舍）。
///
/// 拿状态的请求一律丢进 `Frame` 去游戏线程执行；输入类请求直接改 `Input` 的标志。
module Server =

    let mutable private listener: HttpListener = null

    let private field (body: string) (name: string) =
        Json.parse body |> Option.bind (fun v -> Json.tryField name v)

    let private fieldStr (body: string) (name: string) =
        field body name
        |> Option.bind (fun v ->
            match v with
            | JStr s -> Some s
            | JNum n -> Some(string n)
            | _ -> None)
        |> Option.defaultValue ""

    let private fieldInt (body: string) (name: string) (fallback: int) =
        field body name |> Option.bind Json.asNumber |> Option.map int |> Option.defaultValue fallback

    let private fieldBool (body: string) (name: string) (fallback: bool) =
        field body name |> Option.bind Json.asBool |> Option.defaultValue fallback

    let private queryValue (query: string) (name: string) =
        if String.IsNullOrEmpty query then
            None
        else
            query.TrimStart('?').Split('&')
            |> Array.tryPick (fun pair ->
                let parts = pair.Split('=', 2)

                if parts.Length = 2 && parts.[0] = name then
                    Some(Uri.UnescapeDataString(parts.[1]))
                else
                    None)

    let private queryInt (query: string) (name: string) (fallback: int) =
        match queryValue query name with
        | Some raw ->
            match Int32.TryParse raw with
            | true, v -> v
            | _ -> fallback
        | None -> fallback

    let private inputState () : J =
        let s = Input.current ()

        JObj
            [ "ok", JBool true
              "holding", JBool(Input.remainingMs () > 0)
              "msLeft", Json.num (Input.remainingMs ())
              "mouseLeft", JBool s.MouseLeft
              "mouseRight", JBool s.MouseRight
              "keys", Json.str (s.Keys |> List.map string |> String.concat ",")
              "observedLeft", JBool Input.observedLeft
              "observedKeys", Json.str Input.observedKeys
              // 注入是否**真的落地**（补丁改写字段的次数）+ 焦点闸门现状。
              // 前者为 0 说明补丁没跑；后者为 false 时 `GetKeyboardState()` 会直接返回空，
              // 注入写得再对游戏也读不到（这就是"能改朝向但不走"的那道门）。
              "injectedKeys", Json.num Runtime.injectedKeys
              "injectedMouse", Json.num Runtime.injectedMouse
              "hasKeyboardFocus", JBool(try Game1.game1.HasKeyboardFocus() with _ -> false)
              "isMainInstance", JBool(try Game1.game1.IsMainInstance with _ -> false) ]

    let private walkBody (body: string) : J =
        let x = fieldInt body "x" -1
        let y = fieldInt body "y" -1
        let timeoutMs = fieldInt body "timeoutMs" 20000

        if x < 0 || y < 0 then
            JObj [ "ok", JBool false; "error", JStr "需要 x/y" ]
        else
            let location = fieldStr body "location"

            let needWarp =
                not (String.IsNullOrWhiteSpace location)
                && (try
                        Game1.player.currentLocation.Name <> location
                    with _ ->
                        false)

            if needWarp then
                match Actions.warp location -1 -1 with
                | JObj fields when (fields |> List.exists (fun (k, v) -> k = "ok" && v = JBool false)) ->
                    JObj(fields |> List.filter (fun (k, _) -> k <> "ok"))
                | _ -> Walk.run x y timeoutMs
            else
                Walk.run x y timeoutMs

    let private route (methodName: string) (path: string) (query: string) (body: string) : int * J =
        match methodName, path with
        // ---- 只读 ----
        | "GET", "/status" -> 200, Compat.status ()
        | "GET", "/state" -> 200, Ops.awaitMain Compat.state 3000
        | "GET", "/map" -> 200, Ops.awaitMain Compat.map 3000
        | "GET", "/surroundings" ->
            200,
            let rect =
                match
                    ( queryInt query "x1" -1,
                      queryInt query "y1" -1,
                      queryInt query "x2" -1,
                      queryInt query "y2" -1 )
                with
                | a, b, c, d when a >= 0 && b >= 0 && c >= 0 && d >= 0 -> Some(a, b, c, d)
                | _ -> None

            Ops.awaitMain (fun () -> Compat.surroundings (queryInt query "radius" 10) rect) 6000
        | "GET", "/scan_chests" -> 200, Ops.awaitMain Compat.scanChests 4000
        | "GET", "/friendship" ->
            200,
            Ops.awaitMain
                (fun () -> Compat.friendship (match queryValue query "npc" with Some v -> v | None -> ""))
                3000
        | "GET", "/find_npc" ->
            200,
            Ops.awaitMain
                (fun () -> Compat.findNpc (match queryValue query "name" with Some v -> v | None -> ""))
                4000
        | "GET", "/menu" -> 200, Actions.menuRead ()
        | "GET", "/screen" -> 200, Actions.screen ()
        // 移动诊断：把"能不能走"的各个闸门一次性摊出来。
        // 反射读、不写死类型名 —— 这些字段在不同版本里挪过位置，编译期绑死会白跑一轮。
        | "GET", "/debug/move" ->
            200,
            Ops.awaitMain
                (fun () ->
                    let player = Game1.player

                    let safe (f: unit -> string) =
                        try
                            f ()
                        with _ -> "err"

                    let read (o: obj) (names: string list) =
                        names
                        |> List.map (fun n ->
                            let t = o.GetType()

                            let flags =
                                System.Reflection.BindingFlags.Public
                                ||| System.Reflection.BindingFlags.NonPublic
                                ||| System.Reflection.BindingFlags.Instance

                            let v =
                                match t.GetProperty(n, flags) with
                                | null ->
                                    match t.GetField(n, flags) with
                                    | null -> "?"
                                    | f ->
                                        (try
                                            match f.GetValue o with
                                            | null -> "null"
                                            | x -> string x
                                         with _ -> "err")
                                | pr ->
                                    (try
                                        match pr.GetValue o with
                                        | null -> "null"
                                        | x -> string x
                                     with _ -> "err")

                            n, JStr v)

                    // 键盘状态：input 层（注入后的结果） vs Game1.oldKBState（上一帧）。
                    let keysOf (ks: Microsoft.Xna.Framework.Input.KeyboardState) =
                        try
                            ks.GetPressedKeys() |> Array.map string |> String.concat ","
                        with _ -> "err"

                    let bindings (bs: StardewValley.InputButton[]) =
                        try
                            bs
                            |> Array.map (fun b ->
                                [ if b.mouseLeft then "ML"
                                  if b.mouseRight then "MR"
                                  if b.key <> Microsoft.Xna.Framework.Input.Keys.None then string b.key ]
                                |> String.concat "|")
                            |> String.concat ","
                        with _ -> "err"

                    let locReq =
                        try
                            match box Game1.locationRequest with
                            | null -> "null"
                            | _ -> "pending"
                        with _ -> "err"

                    JObj
                        [ "ok", JBool true
                          "player",
                          JObj(
                              read
                                  player
                                  [ "CanMove"
                                    "UsingTool"
                                    "Frozen"
                                    "freezePause"
                                    "isInBed"
                                    "usingSlingshot"
                                    "CurrentToolIndex"
                                    "Position" ]
                              @ [ "movementDirections",
                                  JStr(
                                      safe (fun () ->
                                          player.movementDirections |> Seq.map string |> String.concat ",")
                                  )
                                  "isMoving", JBool(try player.isMoving() with _ -> false)
                                  "speed", JStr(safe (fun () -> string (player.getMovementSpeed())))
                                  "tile",
                                  JStr(
                                      safe (fun () -> sprintf "%d,%d" player.TilePoint.X player.TilePoint.Y)
                                  ) ]
                          )
                          "game",
                          JObj(
                              read
                                  (box Game1.game1)
                                  [ "IsActive"
                                    "paused"
                                    "eventUp"
                                    "messagePause"
                                    "gamePaused"
                                    "IsChatting" ]
                              @ [ "pauseTime", JStr(safe (fun () -> string (Game1.pauseTime)))
                                  "locationRequest", JStr locReq
                                  "isQuestion", JBool(try Game1.isQuestion with _ -> false)
                                  "eventUpStatic", JBool(try Game1.eventUp with _ -> false)
                                  "ticks", JStr(safe (fun () -> string (Game1.ticks)))
                                  "hasEvent", JBool(try not (isNull Game1.CurrentEvent) with _ -> false) ]
                          )
                          "keys",
                          JObj
                              [ "current", JStr(safe (fun () -> keysOf (Game1.input.GetKeyboardState())))
                                "old", JStr(safe (fun () -> keysOf Game1.oldKBState)) ]
                          "bindings",
                          JObj
                              [ "up", JStr(safe (fun () -> bindings Game1.options.moveUpButton))
                                "right", JStr(safe (fun () -> bindings Game1.options.moveRightButton))
                                "down", JStr(safe (fun () -> bindings Game1.options.moveDownButton))
                                "left", JStr(safe (fun () -> bindings Game1.options.moveLeftButton))
                                "menu", JStr(safe (fun () -> bindings Game1.options.menuButton))
                                "cancel", JStr(safe (fun () -> bindings Game1.options.cancelButton)) ]
                          "menu", JBool(not (isNull Game1.activeClickableMenu))
                          "minigame", JBool(not (isNull Game1.currentMinigame))
                          "gamepad", JBool(try Game1.options.gamepadControls with _ -> false)
                          // 碰撞判定：MovePositionImpl 真正拦人的地方。
                          // 关键在 `hitNow` —— 如果人**当前**就已经卡在实心格里，
                          // 那么往任何方向 nextPosition 都必然也实心 ⇒ 永久走不动，
                          // 而 `/move`（直接改坐标）照样能"瞬移"出去。这正是"能转向不能走"的形态。
                          "collide",
                          JObj(
                              [ "hitNow",
                                JStr(
                                    safe (fun () ->
                                        let r = player.GetBoundingBox()

                                        let hit =
                                            Game1.currentLocation.isCollidingPosition(r, Game1.viewport, true, 0, false, player)

                                        sprintf "bbox=%d,%d,%d,%d hit=%b" r.X r.Y r.Width r.Height hit)
                                )
                                "bbox",
                                JStr(
                                    safe (fun () ->
                                        let r = player.GetBoundingBox()
                                        sprintf "%d,%d,%d,%d" r.X r.Y r.Width r.Height)
                                ) ]
                              @ [ for name, d in [ "up", 0; "right", 1; "down", 2; "left", 3 ] ->
                                      name,
                                      JStr(
                                          safe (fun () ->
                                              let r = player.nextPosition d

                                              let hit =
                                                  Game1.currentLocation.isCollidingPosition(
                                                      r,
                                                      Game1.viewport,
                                                      true,
                                                      0,
                                                      false,
                                                      player
                                                  )

                                              sprintf "%d,%d hit=%b" r.X r.Y hit)
                                      ) ]
                          ) ])
                4000
        | "GET", "/festival" -> 200, Ops.awaitMain Festival.info 3000
        | "GET", "/inventory" -> 200, World.inventory ()
        | "GET", "/probe/active_menu" ->
            200,
            Ops.awaitMain
                (fun () ->
                    let menu = Game1.activeClickableMenu

                    JObj [ "ok", JBool true; "open", JBool(not (isNull menu)); "menu", Probe.describe menu ])
                2000
        | "GET", "/input/state" -> 200, inputState ()
        // ---- 移动 ----
        | "POST", "/move"
        | "POST", "/walk_to" -> 200, walkBody body
        | "POST", "/stop" ->
            Ops.abort "被 /stop 中止"
            Input.release ()
            200, JObj [ "ok", JBool true; "stopped", JBool true ]
        | "POST", "/position" -> 200, Extra.position (fieldInt body "x" -1) (fieldInt body "y" -1)
        | "POST", "/warp" ->
            let location = fieldStr body "location"

            if String.IsNullOrWhiteSpace location then
                400, JObj [ "ok", JBool false; "error", JStr "需要 location" ]
            else
                200, Actions.warp location (fieldInt body "x" -1) (fieldInt body "y" -1)
        // ---- 动作 ----
        | "POST", "/face" -> 200, Actions.faceValue (defaultArg (field body "direction") (JStr "down"))
        | "POST", "/tool"
        | "POST", "/use" -> 200, Actions.useTool ()
        | "POST", "/interact" -> 200, Actions.interact ()
        | "POST", "/click" ->
            200,
            Actions.click (fieldInt body "x" -1) (fieldInt body "y" -1) (fieldBool body "no_move" false) (fieldStr body "component")
        | "GET", "/focus"
        | "POST", "/focus" -> 200, Actions.focus ()
        | "POST", "/key" -> 200, Extra.key (fieldStr body "key") (fieldInt body "count" 1)
        | "POST", "/pause" -> 200, Extra.pause ()
        | "POST", "/resume" -> 200, Extra.resume ()
        | "POST", "/refill" -> 200, Extra.refill ()
        | "POST", "/eat" -> 200, Extra.eat ()
        | "POST", "/harvest" -> 200, Extra.harvest (queryInt query "radius" 15)
        | "GET", "/ripen" -> 200, Extra.ripen (queryInt query "radius" 30)
        | "POST", "/sleep" -> 200, Actions.sleep (fieldBool body "stay" false)
        // ---- 物品 / 经济 ----
        | "POST", "/select" -> 200, World.select (fieldStr body "name") (fieldInt body "slot" -1)
        | "POST", "/buy" -> 200, Extra.buy (fieldStr body "id") (fieldInt body "quantity" 1)
        | "POST", "/sell" -> 200, Extra.sell (fieldBool body "all" false) (fieldStr body "name")
        | "POST", "/gift" -> 200, Extra.gift (fieldStr body "target") (fieldStr body "item")
        // ---- 箱子 ----
        | "POST", "/chest" -> 200, Extra.chest (fieldInt body "x" -1) (fieldInt body "y" -1)
        | "POST", "/store" ->
            200,
            Extra.store
                (fieldInt body "x" -1)
                (fieldInt body "y" -1)
                (fieldStr body "name")
                (fieldInt body "count" 0)
                (fieldInt body "slot" -1)
                (fieldInt body "quality" -1)
                (fieldBool body "keepTools" true)
        | "POST", "/chest_take" ->
            200,
            Extra.chestTake
                (fieldInt body "x" -1)
                (fieldInt body "y" -1)
                (fieldStr body "name")
                (fieldInt body "slot" -1)
                (fieldInt body "count" 0)
                (fieldInt body "quality" -1)
        | "POST", "/chest_take_list" -> 200, Extra.chestTakeList body
        // ---- 批量农活 / 节日 ----
        | "POST", "/tool_area" ->
            200,
            Farm.toolArea
                (fieldStr body "operation")
                (fieldInt body "x1" -1)
                (fieldInt body "y1" -1)
                (fieldInt body "x2" -1)
                (fieldInt body "y2" -1)
        | "POST", "/festival/interact" -> 200, Festival.interact (fieldStr body "name")
        | "POST", "/festival/answer" -> 200, Festival.answer (fieldInt body "answer" 0)
        // ---- 菜单 ----
        | "POST", "/menu/click" -> 200, Actions.menuClick body
        | "POST", "/menu_close" -> 200, Actions.menuClose ()
        // ---- 虚拟输入（我们自己的扩展）----
        | "POST", "/input/hold" ->
            let buttons =
                match field body "buttons" with
                | Some b ->
                    match Json.asStringList b with
                    | [] -> [ "use" ]
                    | xs -> xs
                | None ->
                    match field body "button" with
                    | Some b -> (match Json.asString b with Some s -> [ s ] | None -> [ "use" ])
                    | None -> [ "use" ]

            let mouseLeft, mouseRight, keys, ms = Input.hold buttons (fieldInt body "ms" 200)

            200,
            JObj
                [ "ok", JBool true
                  "buttons", JArr(buttons |> List.map JStr)
                  "ms", Json.num ms
                  "mouseLeft", JBool mouseLeft
                  "mouseRight", JBool mouseRight
                  "keys", JArr(keys |> List.map (fun k -> JStr(string k))) ]
        | "POST", "/input/release" ->
            Input.release ()
            200, JObj [ "ok", JBool true; "released", JBool true ]
        | _ -> 404, JObj [ "ok", JBool false; "error", JStr(sprintf "没有这个端点: %s %s" methodName path) ]

    let private respond (ctx: HttpListenerContext) (status: int) (payload: string) =
        try
            let res = ctx.Response
            let bytes = Encoding.UTF8.GetBytes payload
            res.StatusCode <- status
            res.ContentType <- "application/json; charset=utf-8"
            res.ContentLength64 <- int64 bytes.Length
            res.KeepAlive <- true
            res.Headers.["Cache-Control"] <- "no-store"
            res.OutputStream.Write(bytes, 0, bytes.Length)
            res.OutputStream.Flush()
            res.Close()
        with _ ->
            ()

    let private handle (ctx: HttpListenerContext) =
        try
            let req = ctx.Request
            let body =
                use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                reader.ReadToEnd()

            let status, payload =
                route req.HttpMethod (req.Url.AbsolutePath.TrimEnd('/')) req.Url.Query body

            respond ctx status (Json.render payload)
        with ex ->
            // 光一句 "Object reference not set" 等于没说 —— 连哪一行都不知道。
            // 把**抛点**带上（TargetSite 是抛出的方法，StackFrame[0] 是它的位置），
            // 外层照旧只认 `error` 这个键，多的 `where` 谁想看就看。
            let where =
                try
                    let site =
                        if isNull ex.TargetSite then ""
                        else
                            let t = ex.TargetSite.DeclaringType
                            (if isNull t then "" else t.Name + ".") + ex.TargetSite.Name

                    let frame =
                        match ex.StackTrace with
                        | null -> ""
                        | s ->
                            s.Split('\n')
                            |> Array.tryFind (fun l -> l.Contains "at ")
                            |> Option.map (fun l -> l.Trim())
                            |> Option.defaultValue ""

                    site + " | " + frame
                with _ ->
                    ""

            respond
                ctx
                500
                (Json.render (JObj [ "ok", JBool false; "error", JStr ex.Message; "where", JStr where ]))

    let start (port: int) : Result<unit, string> =
        try
            let l = new HttpListener()
            // 必须是 localhost 而不是 127.0.0.1：后者会被 HTTP.sys 拒
            l.Prefixes.Add(sprintf "http://localhost:%d/" port)
            l.Start()
            listener <- l

            let loop () =
                while l.IsListening do
                    try
                        let ctx = l.GetContext()
                        ThreadPool.QueueUserWorkItem(fun _ -> handle ctx) |> ignore
                    with _ ->
                        ()

            let thread = Thread(ThreadStart loop)
            thread.IsBackground <- true
            thread.Start()
            Ok()
        with ex ->
            Error ex.Message

    let stop () =
        try
            if not (isNull listener) then
                listener.Stop()
                listener.Close()
        with _ ->
            ()
