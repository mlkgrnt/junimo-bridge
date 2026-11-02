namespace JunimoBridge

open System
open System.Reflection

/// 菜单探针。
///
/// 存在的理由：小游戏和不少菜单的关键状态（钓鱼的条和鱼、卡片的题面）都是 private 字段，
/// 而我们**不想为每个玩法写一次反射**。这里用一套通用规则把任意菜单摊成 JSON：
/// 标量取全，坐标/矩形展开，可点击控件的 bounds 也吐出来（点击时要用）。
module Probe =

    let private flags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

    let private scalar (value: obj) : J option =
        match value with
        | null -> None
        | :? bool as b -> Some(JBool b)
        | :? int as i -> Some(JNum(float i))
        | :? int64 as i -> Some(JNum(float i))
        | :? float32 as f -> Some(JNum(float f))
        | :? float as f -> Some(JNum f)
        | :? string as s -> Some(JStr s)
        | :? Enum as e -> Some(JStr(string e))
        | :? Microsoft.Xna.Framework.Point as p -> Some(JObj [ "x", JNum(float p.X); "y", JNum(float p.Y) ])
        | :? Microsoft.Xna.Framework.Vector2 as v ->
            Some(JObj [ "x", JNum(float v.X); "y", JNum(float v.Y) ])
        | :? Microsoft.Xna.Framework.Rectangle as r ->
            Some(
                JObj
                    [ "x", JNum(float r.X)
                      "y", JNum(float r.Y)
                      "w", JNum(float r.Width)
                      "h", JNum(float r.Height) ]
            )
        | _ -> None

    let private tryField (t: Type) (name: string) (o: obj) =
        try
            let f = t.GetField(name, flags)

            if isNull f then None else Some(f.GetValue o)
        with _ ->
            None

    /// 可点击控件 → {name, x, y, w, h}，点击端点直接吃这个坐标。
    ///
    /// 不同菜单把控件放在不同地方：`IClickableMenu` 是 `allClickableComponents`，
    /// `GameMenu` 则是每个 tab 各有一份（`tabs`）。所以这里按候选字段找，找不到就往下钻一层 tab。
    let rec private collectComponents (o: obj) (prefix: string) (depth: int) : J list =
        if isNull o || depth > 1 then
            []
        else
            let t = o.GetType()

            let direct =
                [ "allClickableComponents"; "clickableComponents"; "components" ]
                |> List.tryPick (fun name ->
                    match tryField t name o with
                    | Some (:? System.Collections.IEnumerable as items) -> Some items
                    | _ -> None)

            match direct with
            | Some items ->
                [ for item in items do
                      if not (isNull item) then
                          let ct = item.GetType()

                          let name =
                              match tryField ct "name" item with
                              | Some (:? string as n) when n <> "" -> prefix + n
                              | _ -> prefix + "?"

                          match tryField ct "bounds" item with
                          | Some (:? Microsoft.Xna.Framework.Rectangle as r) ->
                              yield
                                  JObj
                                      [ "name", JStr name
                                        "x", Json.num r.X
                                        "y", Json.num r.Y
                                        "w", Json.num r.Width
                                        "h", Json.num r.Height ]
                          | _ -> () ]
            | None ->
                [ "pages"; "tabs" ]
                |> List.tryPick (fun name ->
                    match tryField t name o with
                    | Some (:? System.Collections.IEnumerable as items) ->
                        Some(
                            [ for i, page in Seq.indexed (Seq.cast<obj> items) do
                                  yield! collectComponents page (sprintf "%s%d:" (name.Substring(0, 1)) i) (depth + 1) ]
                        )
                    | _ -> None)
                |> Option.defaultValue []

    /// 把任意对象摊成 {type, fields, components}。
    let describe (o: obj) : J =
        if isNull o then
            JNull
        else
            let t = o.GetType()
            let fields = ResizeArray<string * J>()

            let add (name: string) (value: obj) =
                if fields.Count < 60 && not (fields |> Seq.exists (fun (k, _) -> k = name)) then
                    match scalar value with
                    | Some j -> fields.Add(name, j)
                    | None -> ()

            let mutable cur = t

            while not (isNull cur) && cur <> typeof<obj> do
                for f in cur.GetFields(flags) do
                    if not (f.Name.StartsWith "<") then
                        add f.Name (try f.GetValue o with _ -> null)

                for p in cur.GetProperties(flags) do
                    if p.CanRead && p.GetIndexParameters().Length = 0 then
                        add p.Name (try p.GetValue o with _ -> null)

                cur <- cur.BaseType

            JObj
                [ "type", JStr t.Name
                  "fields", JObj(List.ofSeq fields)
                  "components", JArr(collectComponents o "" 0) ]
