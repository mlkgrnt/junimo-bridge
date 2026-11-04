namespace JunimoBridge

open System
open System.Reflection
open Microsoft.Xna.Framework.Input
open StardewValley
open StardewValley.Minigames

/// 小游戏（Junimo Kart / Prairie King / 飞镖 / 抓娃娃 等）。
///
/// 和菜单那条路完全不同：`IMinigame` 有**直接入口**
/// `receiveKeyPress(Keys)` / `receiveKeyRelease(Keys)` / `forceQuit()`，
/// 不用赌键盘状态有没有被 tick 读到 —— 这些游戏大多在 `tick()` 里硬编码
/// `Keys.Space` / 方向键，压根不看 `options`。
module Minigame =

    let private current () =
        try
            match box Game1.currentMinigame with
            | null -> None
            | _ -> Some Game1.currentMinigame
        with _ ->
            None

    let private idOf (m: IMinigame) =
        try
            match m.minigameId() with
            | null -> ""
            | s -> s
        with _ ->
            ""

    /// 只摊开"能看懂局面"的字段：数值 / 布尔 / 字符串，最多 40 个。
    /// 小游戏的字段名各版本会变，所以走反射而不是编译期绑死。
    let private fieldsOf (m: IMinigame) (limit: int) : (string * J) list =
        let t = m.GetType()

        let flags =
            BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

        let interesting (ty: Type) =
            ty = typeof<bool>
            || ty = typeof<int>
            || ty = typeof<float32>
            || ty = typeof<string>
            || ty = typeof<Microsoft.Xna.Framework.Vector2>

        try
            t.GetFields(flags)
            |> Array.filter (fun f -> interesting f.FieldType)
            |> Array.truncate limit
            |> Array.map (fun f ->
                let v =
                    try
                        match f.GetValue m with
                        | null -> JNull
                        | :? bool as b -> JBool b
                        | :? int as i -> Json.num i
                        | :? float32 as s -> JNum(float s)
                        | :? string as s -> JStr s
                        | :? Microsoft.Xna.Framework.Vector2 as v -> JObj [ "x", JNum(float v.X); "y", JNum(float v.Y) ]
                        | other -> JStr(other.ToString())
                    with _ ->
                        JStr "err"

                f.Name, v)
            |> List.ofArray
        with _ ->
            []

    /// GET /minigame —— 现在有没有小游戏、是哪个、局面如何。
    let state (limit: int) : J =
        Ops.awaitMain
            (fun () ->
                match current () with
                | None -> JObj [ "ok", JBool true; "active", JBool false ]
                | Some m ->
                    let t = m.GetType()

                    JObj
                        [ "ok", JBool true
                          "active", JBool true
                          "id", JStr(idOf m)
                          "type", JStr t.Name
                          "fullType", JStr t.FullName
                          "fields", JObj(fieldsOf m (max 1 (min 120 limit))) ])
            2500

    /// POST /minigame/key {key, holdMs} —— 直接喂 `receiveKeyPress`（+ 延时 `receiveKeyRelease`）。
    ///
    /// 键名走**字面物理键**（`space` / `left` / `w` / `enter` …）：
    /// 小游戏不读 `options`，语义名在这里没有意义。
    let key (name: string) (holdMs: int) : J =
        match Input.parseKey name with
        | None ->
            JObj
                [ "ok", JBool false
                  "error", JStr(sprintf "不认识的键名 \"%s\"" name)
                  "hint", JStr "小游戏用字面键名：space / enter / left / right / up / down / w / a / s / d / escape / 0-9" ]
        | Some k ->
            match current () with
            | None -> JObj [ "ok", JBool false; "error", JStr "当前没有小游戏在跑" ]
            | Some m ->
                let hold = max 30 (min 3000 holdMs)
                let mutable released = false
                let mutable frames = 0

                let press () =
                    Ops.awaitMain
                        (fun () ->
                            m.receiveKeyPress k
                            JNull)
                        1500
                    |> ignore

                let unpressed () =
                    Ops.awaitMain
                        (fun () ->
                            m.receiveKeyRelease k
                            JNull)
                        1500
                    |> ignore

                press ()

                let frame =
                    Ops.run (fun _ ->
                        frames <- frames + 1

                        if (not released) && frames * 16 >= hold then
                            unpressed ()
                            released <- true
                            Some(JBool true)
                        else
                            None)

                let ok =
                    match Ops.await frame (hold + 2000) with
                    | JBool b -> b
                    | _ -> false

                // 兜底：主线程那条路超时也要松开，别把键卡在按下状态
                if not released then
                    try
                        unpressed ()
                    with _ ->
                        ()

                JObj
                    [ "ok", JBool ok
                      "key", JStr(string k)
                      "holdMs", Json.num hold
                      "stillActive", JBool(Option.isSome (current ())) ]

    /// POST /minigame/quit —— 退出小游戏（有些游戏卡住只能这样出来）。
    let quit () : J =
        Ops.awaitMain
            (fun () ->
                match current () with
                | None -> JObj [ "ok", JBool true; "active", JBool false; "note", JStr "本来就没有小游戏" ]
                | Some m ->
                    let id = idOf m

                    try
                        m.forceQuit () |> ignore
                    with _ ->
                        ()

                    JObj
                        [ "ok", JBool true
                          "active", JBool(Option.isSome (current ()))
                          "quit", JStr id ])
            3000
