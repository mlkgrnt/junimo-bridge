/// Json.fs 的离线断言（不需要游戏，CI 跑的就是这个）。
///
/// 这一层出错的代价很隐蔽：转义漏了会让客户端收到截断的字符串，解析漏了会让
/// `/input/hold` 之类的请求变成"参数没传"——两者都不会崩，只会静默行为不对。
module Tests

open JunimoBridge
open JunimoBridge.Json

let mutable failed = 0

let check (name: string) (condition: bool) =
    if condition then
        printfn "  ok   %s" name
    else
        printfn "  FAIL %s" name
        failed <- failed + 1

let round (value: J) =
    match Json.parse (Json.render value) with
    | Some parsed -> parsed
    | None -> failwithf "解析自己渲染出来的 JSON 失败: %s" (Json.render value)

[<EntryPoint>]
let main _ =
    printfn "== 渲染 =="
    check "字符串转义" (Json.render (JStr "a\"b\\c") = "\"a\\\"b\\\\c\"")
    check "控制字符走 \\u" (Json.render (JStr "\n") = "\"\\n\"")
    check "整数不带小数点" (Json.render (JNum 3.0) = "3")
    check "浮点保留精度" (Json.render (JNum 1.5) = "1.5")
    check "NaN 渲染成 null" (Json.render (JNum nan) = "null")
    check "空对象" (Json.render (JObj []) = "{}")
    check "空数组" (Json.render (JArr []) = "[]")
    check "中文原样输出" (Json.render (JStr "测试用") = "\"测试用\"")

    printfn "== 解析 =="
    check "解析扁平对象" (
        match Json.parse """{"x":12,"name":"农场"}""" with
        | Some v ->
            (Json.tryField "x" v |> Option.bind Json.asNumber = Some 12.0)
            && (Json.tryField "name" v |> Option.bind Json.asString = Some "农场")
        | None -> false
    )
    check "解析布尔" (
        match Json.parse """{"ok":true}""" with
        | Some v -> Json.tryField "ok" v |> Option.bind Json.asBool = Some true
        | None -> false
    )
    check "解析字符串数组" (
        match Json.parse """{"buttons":["w","left"]}""" with
        | Some v ->
            match Json.tryField "buttons" v with
            | Some b -> Json.asStringList b = [ "w"; "left" ]
            | None -> false
        | None -> false
    )
    check "单个字符串也当列表" (Json.asStringList (JStr "use") = [ "use" ])
    check "空对象体" (
        match Json.parse "{}" with
        | Some(JObj []) -> true
        | _ -> false
    )
    check "空 body 不抛异常" (
        try
            Json.parse "" |> ignore
            true
        with _ ->
            false
    )
    check "空 body 取不到字段" (
        match Json.parse "" with
        | Some v -> Json.tryField "x" v |> Option.isNone
        | None -> true
    )

    printfn "== 往返 =="
    let sample =
        JObj
            [ "ok", JBool true
              "ms", JNum 1200
              "button", JStr "use"
              "nested", JObj [ "x", JNum 5; "y", JNum 6 ]
              "list", JArr [ JStr "a"; JNum 2 ] ]

    let back = round sample
    check "顶层键还在" (Json.tryField "ok" back |> Option.bind Json.asBool = Some true)
    check "嵌套对象还能取" (
        match Json.tryField "nested" back with
        | Some n -> Json.tryField "y" n |> Option.bind Json.asNumber = Some 6.0
        | None -> false
    )
    check "数组长度对" (
        match Json.tryField "list" back with
        | Some(JArr items) -> List.length items = 2
        | _ -> false
    )
    check "取不到的键给 None" (Json.tryField "nope" back |> Option.isNone)

    printfn ""
    if failed = 0 then
        printfn "JSON 断言全部通过"
        0
    else
        printfn "%d 条失败" failed
        1
