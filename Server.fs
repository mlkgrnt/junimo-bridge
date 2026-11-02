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
              "observedKeys", Json.str Input.observedKeys ]

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
        | "GET", "/surroundings" -> 200, Ops.awaitMain (fun () -> Compat.surroundings (queryInt query "radius" 10)) 4000
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
        | "POST", "/key" -> 200, Extra.key (fieldStr body "key") (fieldInt body "count" 1)
        | "POST", "/pause" -> 200, Extra.pause ()
        | "POST", "/resume" -> 200, Extra.resume ()
        | "POST", "/refill" -> 200, Extra.refill ()
        | "POST", "/eat" -> 200, Extra.eat ()
        | "POST", "/harvest" -> 200, Extra.harvest (queryInt query "radius" 15)
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
            Extra.store (fieldInt body "x" -1) (fieldInt body "y" -1) (fieldStr body "name") (fieldBool body "keepTools" true)
        | "POST", "/chest_take" ->
            200,
            Extra.chestTake
                (fieldInt body "x" -1)
                (fieldInt body "y" -1)
                (fieldStr body "name")
                (fieldInt body "slot" -1)
                (fieldInt body "count" 0)
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
            respond ctx 500 (Json.render (JObj [ "ok", JBool false; "error", JStr ex.Message ]))

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
