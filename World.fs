namespace JunimoBridge

open System
open Microsoft.Xna.Framework
open StardewValley

/// 世界类端点：背包、选择物品、箱子。
module World =

    let private itemJson (slot: int) (item: Item) : J =
        JObj
            [ "slot", Json.num slot
              "name", Json.str item.Name
              "count", Json.num item.Stack
              "category", Json.num item.Category
              "quality", Json.num item.Quality
              "isTool", JBool(item :? Tool) ]

    /// 背包（含工具）。slot 就是 `Items` 的下标，`/select` 吃同一个编号。
    let inventory () : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let items = ResizeArray<J>()

                for i in 0 .. player.Items.Count - 1 do
                    let item = player.Items.[i]

                    if not (isNull item) then
                        items.Add(itemJson i item)

                JObj
                    [ "ok", JBool true
                      "count", Json.num items.Count
                      "currentSlot", Json.num player.CurrentToolIndex
                      "items", JArr(List.ofSeq items) ])
            2000

    /// 选中背包里的一件东西。前 12 格是快捷栏，超过 12 格的先换到第 0 格再选中。
    let select (name: string) (slot: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let items = player.Items

                let found =
                    if slot >= 0 && slot < items.Count && not (isNull items.[slot]) then
                        Some slot
                    elif String.IsNullOrWhiteSpace name then
                        None
                    else
                        let mutable hit = -1
                        let mutable exact = -1

                        for i in 0 .. items.Count - 1 do
                            let item = items.[i]

                            if not (isNull item) then
                                if item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) then
                                    if exact < 0 then exact <- i
                                elif item.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 then
                                    if hit < 0 then hit <- i

                        if exact >= 0 then Some exact
                        elif hit >= 0 then Some hit
                        else None

                match found with
                | None -> JObj [ "ok", JBool false; "error", JStr(sprintf "背包里找不到 \"%s\"" name) ]
                | Some index ->
                    let item = items.[index]

                    if index >= 12 then
                        // 快捷栏只有 12 格：和第 0 格换位再选
                        let first = items.[0]
                        items.[0] <- item
                        items.[index] <- first
                        player.CurrentToolIndex <- 0
                    else
                        player.CurrentToolIndex <- index

                    JObj
                        [ "ok", JBool true
                          "selected", JStr item.Name
                          "slot", Json.num (if index >= 12 then 0 else index) ])
            2000

    /// 面前那格上的箱子（读内容）。不是箱子就如实报错，不猜。
    let chest () : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let target = Actions.facingPoint player

                let chest =
                    try
                        match loc.Objects.TryGetValue(Vector2(float32 target.X, float32 target.Y)) with
                        | true, o when (o :? StardewValley.Objects.Chest) -> Some(o :?> StardewValley.Objects.Chest)
                        | _ -> None
                    with _ ->
                        None

                match chest with
                | None ->
                    JObj
                        [ "ok", JBool false
                          "error", JStr(sprintf "(%d,%d) 上不是箱子" target.X target.Y) ]
                | Some box ->
                    let items = ResizeArray<J>()

                    for i in 0 .. box.Items.Count - 1 do
                        let item = box.Items.[i]

                        if not (isNull item) then
                            items.Add(itemJson i item)

                    JObj
                        [ "ok", JBool true
                          "target", JObj [ "x", Json.num target.X; "y", Json.num target.Y ]
                          "count", Json.num items.Count
                          "items", JArr(List.ofSeq items) ])
            2000

    /// 从面前箱子里取一格（可选只取一部分）。取不完/放不下都会如实回报。
    let chestTake (slot: int) (count: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let target = Actions.facingPoint player

                let box =
                    try
                        match loc.Objects.TryGetValue(Vector2(float32 target.X, float32 target.Y)) with
                        | true, o when (o :? StardewValley.Objects.Chest) -> Some(o :?> StardewValley.Objects.Chest)
                        | _ -> None
                    with _ ->
                        None

                match box with
                | None -> JObj [ "ok", JBool false; "error", JStr "面前没有箱子" ]
                | Some chest ->
                    if slot < 0 || slot >= chest.Items.Count || isNull chest.Items.[slot] then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "箱子里第 %d 格是空的" slot) ]
                    else
                        let item = chest.Items.[slot]
                        let take = if count <= 0 then item.Stack else min count item.Stack
                        let original = item.Stack
                        item.Stack <- take

                        let added = player.addItemToInventoryBool(item, false)

                        if added then
                            if take >= original then
                                chest.Items.[slot] <- null

                            JObj
                                [ "ok", JBool true
                                  "took", JStr item.Name
                                  "count", Json.num take ]
                        else
                            item.Stack <- original
                            JObj [ "ok", JBool false; "error", JStr "背包放不下，没动箱子里的东西" ])
            2000

    /// 把背包里的东西放进面前箱子。只做「整叠搬进空格」，不做部分合并 —— 宁可少做也不做错。
    let store (slot: int) (name: string) (count: int) : J =
        Ops.awaitMain
            (fun () ->
                let player = Game1.player
                let loc = player.currentLocation
                let target = Actions.facingPoint player

                let chest =
                    try
                        match loc.Objects.TryGetValue(Vector2(float32 target.X, float32 target.Y)) with
                        | true, o when (o :? StardewValley.Objects.Chest) -> Some(o :?> StardewValley.Objects.Chest)
                        | _ -> None
                    with _ ->
                        None

                match chest with
                | None -> JObj [ "ok", JBool false; "error", JStr "面前没有箱子" ]
                | Some box ->
                    let items = player.Items

                    let index =
                        if slot >= 0 && slot < items.Count && not (isNull items.[slot]) then
                            slot
                        else
                            let mutable hit = -1

                            for i in 0 .. items.Count - 1 do
                                let item = items.[i]

                                if hit < 0 && not (isNull item) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) then
                                    hit <- i

                            hit

                    if index < 0 then
                        JObj [ "ok", JBool false; "error", JStr(sprintf "背包里没有 \"%s\"" name) ]
                    else
                        let item = items.[index]

                        if box.Items.Count >= 70 then
                            JObj [ "ok", JBool false; "error", JStr "箱子满了" ]
                        else
                            box.Items.Add(item)
                            items.[index] <- null

                            JObj
                                [ "ok", JBool true
                                  "stored", JStr item.Name
                                  "count", Json.num item.Stack ])
            2000
