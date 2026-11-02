namespace JunimoBridge

open System
open StardewValley

/// 节日（`/festival` 系列）。当前没有活动事件时，NagiBridge 的约定是
/// `{ok:false,error:"No active event"}` —— 这是**正常态**，客户端会原样展示。
module Festival =

    let private currentEvent () =
        let player = Game1.player

        if isNull player || isNull player.currentLocation then
            None
        else
            let ev = player.currentLocation.currentEvent
            if isNull ev then None else Some ev

    /// GET /festival
    let info () : J =
        match currentEvent () with
        | None -> JObj [ "ok", JBool false; "error", JStr "No active event" ]
        | Some ev ->
            let loc = Game1.player.currentLocation

            JObj
                [ "ok", JBool true
                  "id", Json.str ev.id
                  "skippable", JBool ev.skippable
                  "festivalName", Json.str(try ev.FestivalName with _ -> "")
                  "location", Json.str loc.Name ]

    /// POST /festival/interact {name?} —— 站在 NPC 旁边交互；给 name 时会先在附近找这个人。
    let interact (name: string) : J =
        match currentEvent () with
        | None -> JObj [ "ok", JBool false; "error", JStr "No active event" ]
        | Some _ ->
            Ops.awaitMain
                (fun () ->
                    let player = Game1.player
                    let loc = player.currentLocation

                    let target =
                        if String.IsNullOrWhiteSpace name then
                            Some(player.TilePoint.X + (match player.FacingDirection with 1 -> 1 | 3 -> -1 | _ -> 0),
                                 player.TilePoint.Y + (match player.FacingDirection with 2 -> 1 | 0 -> -1 | _ -> 0))
                        else
                            loc.characters
                            |> Seq.tryFind (fun c ->
                                c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                                || c.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            |> Option.map (fun c -> c.TilePoint.X, c.TilePoint.Y)

                    match target with
                    | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "附近找不到 \"%s\"" name) ]
                    | Some(tx, ty) ->
                        let triggered =
                            try
                                loc.checkAction(xTile.Dimensions.Location(tx, ty), Game1.viewport, player)
                            with _ ->
                                false

                        JObj
                            [ "ok", JBool true
                              "triggered", JBool triggered
                              "target", JObj [ "x", Json.num tx; "y", Json.num ty ] ])
                3000

    /// POST /festival/answer {answer} —— 给当前问题选第 N 个回答。
    let answer (index: int) : J =
        match currentEvent () with
        | None -> JObj [ "ok", JBool false; "error", JStr "No active event" ]
        | Some ev ->
            Ops.awaitMain
                (fun () ->
                    // 选项统一交给菜单点击那条路（它已经处理了数字键与控件回落）
                    Actions.menuClick (sprintf "{\"option\":%d}" index))
                3000
