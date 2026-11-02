namespace JunimoBridge

open System
open System.Globalization
open System.Text

/// 手写 JSON 值。刻意不用 System.Text.Json：游戏目录里自带一份，
/// 版本和 SMAPI 实际加载的那份未必一致，为几十行序列化多担一个程序集冲突的风险不值得。
type J =
    | JBool of bool
    | JNum of float
    | JStr of string
    | JNull
    | JArr of J list
    | JObj of (string * J) list

module Json =

    let private escape (s: string) =
        let sb = StringBuilder(s.Length + 8)
        sb.Append '"' |> ignore

        for ch in s do
            match ch with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append '"' |> ignore
        sb.ToString()

    let rec render (value: J) : string =
        match value with
        | JBool b -> if b then "true" else "false"
        | JNum n ->
            if Double.IsNaN n || Double.IsInfinity n then "null"
            else n.ToString("R", CultureInfo.InvariantCulture)
        | JStr s -> escape s
        | JNull -> "null"
        | JArr items -> "[" + String.Join(",", items |> List.map render) + "]"
        | JObj fields ->
            "{"
            + String.Join(",", fields |> List.map (fun (k, v) -> escape k + ":" + render v))
            + "}"

    /// 解析请求体。只覆盖我们自己会收到的形状（扁平对象 + 标量 + 字符串数组），
    /// 不是通用 JSON 解析器 —— 但也不需要是。
    let parse (text: string) : J option =
        let len = text.Length
        let mutable i = 0

        let peek () = if i < len then text.[i] else '\000'

        let skipWs () =
            while i < len && (text.[i] = ' ' || text.[i] = '\n' || text.[i] = '\r' || text.[i] = '\t') do
                i <- i + 1

        let readString () =
            // 进来时 text.[i] 必须是 "
            i <- i + 1
            let sb = StringBuilder()
            let mutable fin = false

            while not fin && i < len do
                let c = text.[i]

                if c = '"' then
                    fin <- true
                    i <- i + 1
                elif c = '\\' then
                    i <- i + 1

                    if i < len then
                        let e = text.[i]
                        i <- i + 1

                        match e with
                        | '"' -> sb.Append '"' |> ignore
                        | '\\' -> sb.Append '\\' |> ignore
                        | '/' -> sb.Append '/' |> ignore
                        | 'n' -> sb.Append '\n' |> ignore
                        | 'r' -> sb.Append '\r' |> ignore
                        | 't' -> sb.Append '\t' |> ignore
                        | 'u' ->
                            if i + 3 < len then
                                let hex = text.Substring(i, 4)
                                i <- i + 4
                                let mutable code = 0

                                if Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, &code) then
                                    sb.Append(char code) |> ignore
                            else
                                i <- len
                        | other -> sb.Append other |> ignore
                else
                    sb.Append c |> ignore
                    i <- i + 1

            sb.ToString()

        let readNumber () =
            let start = i

            while i < len
                  && (Char.IsDigit text.[i]
                      || text.[i] = '-'
                      || text.[i] = '+'
                      || text.[i] = '.'
                      || text.[i] = 'e'
                      || text.[i] = 'E') do
                i <- i + 1

            match Double.TryParse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, v -> JNum v
            | _ -> JNull

        let rec readValue () : J =
            skipWs ()

            match peek () with
            | '"' -> JStr(readString ())
            | '{' -> readObject ()
            | '[' -> readArray ()
            | 't' when i + 3 < len && text.Substring(i, 4) = "true" ->
                i <- i + 4
                JBool true
            | 'f' when i + 4 < len && text.Substring(i, 5) = "false" ->
                i <- i + 5
                JBool false
            | 'n' when i + 3 < len && text.Substring(i, 4) = "null" ->
                i <- i + 4
                JNull
            | c when Char.IsDigit c || c = '-' || c = '+' -> readNumber ()
            | _ ->
                i <- i + 1
                JNull

        and readArray () : J =
            i <- i + 1
            let items = ResizeArray<J>()
            skipWs ()

            while i < len && peek () <> ']' do
                items.Add(readValue ())
                skipWs ()

                if i < len && peek () = ',' then
                    i <- i + 1
                    skipWs ()

            if i < len then i <- i + 1
            JArr(List.ofSeq items)

        and readObject () : J =
            i <- i + 1
            let fields = ResizeArray<string * J>()
            skipWs ()

            while i < len && peek () <> '}' do
                skipWs ()

                if i < len && peek () = '"' then
                    let key = readString ()
                    skipWs ()

                    if i < len && peek () = ':' then
                        i <- i + 1
                        fields.Add(key, readValue ())
                    else
                        ()
                else
                    i <- i + 1

                skipWs ()

                if i < len && peek () = ',' then
                    i <- i + 1

            if i < len then i <- i + 1
            JObj(List.ofSeq fields)

        let value = readValue ()
        Some value

    // ---- 取值小工具（解析后按名字取，取不到给默认值） ----

    let tryField (name: string) (value: J) : J option =
        match value with
        | JObj fields -> fields |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
        | _ -> None

    let asString (value: J) : string option =
        match value with
        | JStr s -> Some s
        | _ -> None

    let asNumber (value: J) : float option =
        match value with
        | JNum n -> Some n
        | _ -> None

    let asBool (value: J) : bool option =
        match value with
        | JBool b -> Some b
        | _ -> None

    let asStringList (value: J) : string list =
        match value with
        | JArr items -> items |> List.choose asString
        | JStr s -> [ s ]
        | _ -> []

    // ---- 构造小工具 ----

    let num (n: int) : J = JNum(float n)
    let fnum (n: float) : J = JNum n
    let str (s: string) : J = if isNull s then JNull else JStr s
    let optStr (s: string option) : J = match s with Some v -> JStr v | None -> JNull
