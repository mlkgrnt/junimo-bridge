namespace JunimoBridge

open System
open System.Collections.Generic
open Microsoft.Xna.Framework
open StardewValley
open StardewValley.Objects

/// 箱子口径：**哪些容器算「我们的仓库」、按什么名字报给上层、怎么打自动标签**。
///
/// 为什么单独一层：这几条判据在上游是散在各端点里的，而且每一条都是踩坑改出来的 ——
/// 内置冰箱不在 `loc.objects` 里、迷你出货箱不能当仓库、`Chest.Name` 对默认箱是英文名。
/// 一旦口径分叉，就会出现「`/scan_chests` 列得出、`/store` 却打不着」这类病，
/// 所以采集 / 取名 / 打标签**只在这里写一份**，`/chest`·`/scan_chests`·`/store`·
/// `/chest_take`·`/chest_take_list` 全部走它。
module Chests =

    /// 「够得着」的切比雪夫距离。问的是**伸手够不够**，不是「结构上能不能走到」，
    /// 所以跟中间有没有墙无关，一个减法就够。
    let reachTiles = 4

    let private safe (value: unit -> 'a) (fallback: 'a) =
        try
            value ()
        with _ ->
            fallback

    // ---------------------------------------------------------------- 身份

    /// 游戏默认箱名（含中文本地化）。认不全的后果：玩家「清掉标记」的箱子会被当成
    /// 自定义名，把自动标签整个压掉。
    let private defaultChestNames =
        HashSet<string>(
            [ "Chest"
              "Big Chest"
              "BigChest"
              "Stone Chest"
              "Big Stone Chest"
              "Mini-Fridge"
              "Mini Fridge"
              "Fridge"
              "Mini-Shipping Bin"
              "Junimo Chest"
              "大箱子"
              "宝箱"
              "石箱"
              "大石箱"
              "迷你冰箱"
              "冰箱"
              "迷你出货箱"
              "祝尼魔箱"
              "收纳箱"
              "储物箱" ],
            StringComparer.OrdinalIgnoreCase
        )

    let private isDefaultItemName (n: string) =
        if String.IsNullOrEmpty n then true else defaultChestNames.Contains n

    /// 是不是「我们的仓库」。三类要排除，理由各不相同：
    ///   · 祝尼魔箱：跨地图共享，写进去玩家在那个界面里翻不到；
    ///   · 迷你出货箱：当夜自动出货，存进去等于卖掉；
    ///   · 加料机：塞进去就变成产品，不是储存。
    /// 兜底两条来自真机：迷你出货箱 `itemId=248`；Town 里还有两口 `itemId=-1` 的
    /// **裸箱子**（代码直接造的，玩家点不开）—— 不排除的话会往里存东西、还专程走过去。
    let isStorageChest (box: Chest) : bool =
        let special =
            safe
                (fun () ->
                    match typeof<Chest>.GetProperty("SpecialChestType") with
                    | null -> "None"
                    | p ->
                        match p.GetValue box with
                        | null -> "None"
                        | v -> v.ToString())
                "None"

        if special = "JunimoChest" || special = "MiniShippingBin" || special = "Enricher" then
            false
        else
            let qid = safe (fun () -> box.QualifiedItemId) ""
            not (qid = "(BC)248" || qid.StartsWith "(-1")

    /// 当前场景的存储容器（`loc.objects` 里的 + 农舍/Cabin 内置冰箱）。
    /// 返回 `(箱, 瓦片, 内置标签)` —— 标签只给冰箱用（"内置冰箱"），其余是空串。
    let collectStorageChests (loc: GameLocation) : (Chest * Vector2 * string) list =
        let list = ResizeArray<Chest * Vector2 * string>()

        if not (isNull loc) then
            safe
                (fun () ->
                    for kv in loc.Objects.Pairs do
                        match kv.Value with
                        | :? Chest as box when isStorageChest box -> list.Add(box, kv.Key, "")
                        | _ -> ())
                ()

            // 内置冰箱：`FarmHouse.fridge` 是**构造时就建好的** NetRef，永远非 null；
            // 但它的坐标靠扫地图上的冰箱贴图算（`fridgePosition`），**没厨房时是 Point.Zero**
            // —— 那格是墙。只判「对象在不在」⇒ 会把玩家看不见也打不开的容器当仓库摆给上层。
            // 📌 通式：「这个对象存在」≠「它在世界里」。
            match loc with
            | :? StardewValley.Locations.FarmHouse as fh ->
                let fridge = safe (fun () -> fh.fridge.Value) null
                let pos = safe (fun () -> fh.fridgePosition) Point.Zero

                if not (isNull fridge) && isStorageChest fridge && pos <> Point.Zero then
                    list.Add(fridge, Vector2(float32 pos.X, float32 pos.Y), "内置冰箱")
            | _ -> ()

        List.ofSeq list

    /// 按格子找容器。先查 `objects`（快、覆盖绝大多数），再回到采集表里按坐标找
    /// —— 内置冰箱不在 `objects` 里，只查前者就「列得出、打不着」。
    let findAt (loc: GameLocation) (x: int) (y: int) : Chest option =
        if isNull loc then
            None
        else
            let direct =
                safe
                    (fun () ->
                        match loc.Objects.TryGetValue(Vector2(float32 x, float32 y)) with
                        | true, o ->
                            match o with
                            | :? Chest as box -> Some box
                            | _ -> None
                        | _ -> None)
                    None

            match direct with
            | Some box -> Some box
            | None ->
                collectStorageChests loc
                |> List.tryPick (fun (box, tile, _) ->
                    if int tile.X = x && int tile.Y = y then Some box else None)

    /// 够不够得着（同图 + 切比雪夫距离 ≤ `reachTiles`）。
    let withinReach (player: Farmer) (loc: GameLocation) (tile: Vector2) : bool =
        not (isNull player)
        && (isNull loc || obj.ReferenceEquals(player.currentLocation, loc))
        && abs (player.TilePoint.X - int tile.X) <= reachTiles
        && abs (player.TilePoint.Y - int tile.Y) <= reachTiles

    // ---------------------------------------------------------------- 取名 / 标签

    /// 自定义名（把「默认物品名」视为没有名字）。
    let private customName (box: Chest) : string =
        safe
            (fun () ->
                let n = box.Name
                if String.IsNullOrWhiteSpace n || isDefaultItemName n then "" else n)
            ""

    /// 报给上层的箱子名：自定义名 > 内置标签（冰箱）> 空串。
    let displayName (box: Chest) (label: string) : string =
        let custom = customName box
        if custom <> "" then custom else label

    /// 箱子颜色（玩家涂的），没涂过是空串。
    let colorHex (box: Chest) : string =
        let toHex (c: Color) =
            if c.A = 0uy then "" else sprintf "#%02X%02X%02X" c.R c.G c.B

        safe
            (fun () ->
                match typeof<Chest>.GetField("playerChoiceColor") with
                | null -> ""
                | f ->
                    match f.GetValue box with
                    | :? Color as c -> toHex c
                    | null -> ""
                    | v ->
                        match v.GetType().GetProperty("Value") with
                        | null -> ""
                        | p ->
                            match p.GetValue v with
                            | :? Color as c -> toHex c
                            | _ -> "")
            ""

    /// 物品粗类目（自动标签的原料）。优先级：类型/类目整数 > 品类名子串（中英）> 名字子串。
    /// 刻意只用**多字词**（"矿"、"花"这种单字会大面积误判）。
    let itemBucket (item: Item) : string =
        if isNull item then
            "杂"
        else
            let cn =
                (if isNull (safe (fun () -> item.getCategoryName()) "") then ""
                 else (safe (fun () -> item.getCategoryName()) "").ToLowerInvariant())

            let nm = if isNull item.Name then "" else item.Name.ToLowerInvariant()
            let dn = if isNull item.DisplayName then "" else item.DisplayName.ToLowerInvariant()
            let has (s: string) (keys: string array) = keys |> Array.exists (fun k -> s.Contains k)
            let cat = safe (fun () -> item.Category) 0

            if (item :? Tool) || (item :? StardewValley.Tools.MeleeWeapon) then "装备"
            elif cat = -99 || cat = -98 || cat = -21 || cat = -22 then "装备"
            elif has cn [| "ring"; "戒指" |] then "装备"
            elif has cn [| "fish"; "鱼" |] || has nm [| "roe" |] || has dn [| "鱼子"; "鲑鱼子" |] then "鱼"
            elif cat = -23 || has cn [| "artifact"; "古物" |] then "古物"
            elif cat = -12 || cat = -2 then "矿"
            elif has cn [| "mineral"; "gem"; "ore"; "geo"; "矿物"; "宝石"; "矿石" |]
                 || has nm [| "ore" |]
                 || has dn [| "矿石"; "矿" |] then "矿"
            elif has cn [| "seed"; "种子" |] || has nm [| "seed" |] || has dn [| "种子"; "籽" |] then "种子"
            elif has cn [| "vegetab"; "fruit"; "flower"; "forage"; "green"; "作物"; "蔬菜"; "水果"; "花"; "采集" |]
                 || has dn [| "作物"; "蔬菜"; "水果"; "花"; "采集" |] then "作物"
            elif has cn [| "egg"; "milk"; "animal"; "artisan"; "cheese"; "mayo"; "honey"; "蛋"; "奶"; "加工"; "奶酪"; "蛋黄酱"; "蜂蜜" |]
                 || has dn [| "蛋"; "奶"; "奶酪"; "蛋黄酱"; "蜂蜜" |] then "农产"
            elif has cn [| "resource"; "basic"; "sell"; "fiber"; "wood"; "stone"; "clay"; "建材"; "资源"; "木材"; "石头"; "纤维" |]
                 || has nm [| "wood"; "stone"; "clay"; "fiber"; "bar" |]
                 || has dn [| "木材"; "石头"; "粘土"; "纤维"; "锭"; "木板" |] then "建材"
            elif has cn [| "cook"; "料理"; "烹饪"; "菜肴" |] then "料理"
            else "杂"

    /// 按内容自动归类：某类占的**格子数**过半数才标（不看堆叠量 —— 999 木料会掩盖 5 类作物）。
    /// 混放 / 最高也是"杂" / 空箱 ⇒ 空串（不标）。
    let autoTag (box: Chest) : string =
        let counts = Dictionary<string, int>()
        let mutable total = 0

        for it in box.Items do
            if not (isNull it) then
                total <- total + 1
                let bucket = itemBucket it
                counts.[bucket] <- (if counts.ContainsKey bucket then counts.[bucket] else 0) + 1

        if total = 0 then
            ""
        else
            let top = counts |> Seq.maxBy (fun kv -> kv.Value)

            if top.Key = "杂" || float top.Value / float total <= 0.5 then "" else top.Key

    // ---------------------------------------------------------------- 报文

    /// 箱子里的物品。**格号在过滤前取**：`slot` 认的是真实 `Items[i]`，
    /// 先滤再编号的话，上层拿它去指哪一摞必然指错（同名两摞就是这么丢的）。
    let itemsJson (box: Chest) : J =
        let items = ResizeArray<J>()

        for i in 0 .. box.Items.Count - 1 do
            let item = box.Items.[i]

            if not (isNull item) then
                let quality =
                    match item with
                    | :? StardewValley.Object as o -> o.Quality
                    | _ -> 0

                items.Add(
                    JObj
                        [ "name", Json.str item.Name
                          "displayName", Json.str item.DisplayName
                          "count", Json.num item.Stack
                          "qualifiedId", Json.str item.QualifiedItemId
                          "slot", Json.num i
                          "quality", Json.num quality ]
                )

        JArr(List.ofSeq items)

    let capacity (box: Chest) : int = safe (fun () -> box.GetActualCapacity()) 0

    /// 一个箱子的完整描述（`/scan_chests` 的 chests 元素）。
    let chestJson (box: Chest) (tile: Vector2) (label: string) (locName: string) : J =
        let items = itemsJson box
        let used = safe (fun () -> box.Items.Count) 0
        let cap = capacity box

        JObj
            [ "x", Json.num (int tile.X)
              "y", Json.num (int tile.Y)
              "items", items
              "capacity", Json.num cap
              "used", Json.num used
              "freeSlots", Json.num (cap - used)
              "location", Json.str locName
              "name", Json.str (displayName box label)
              // 机器认 id、人认中文名 —— 两样都给，省得上层为了分辨"这是台小冰箱"去比坐标
              "typeId", Json.str (safe (fun () -> box.QualifiedItemId) "")
              "typeName", Json.str (safe (fun () -> box.DisplayName) "")
              "color", Json.str (colorHex box)
              "autoTag", Json.str (autoTag box) ]
