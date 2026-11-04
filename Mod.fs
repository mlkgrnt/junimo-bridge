namespace JunimoBridge

open System
open HarmonyLib
open StardewModdingAPI
open StardewModdingAPI.Events

/// SMAPI 入口。只做四件事：读版本、起 HTTP、打输入补丁、挂每帧钩子。
type BridgeMod() =
    inherit Mod()

    let harmony = Harmony("mlkgrnt.JunimoBridge")

    override this.Entry(helper: IModHelper) =
        Runtime.startedAt <- DateTime.UtcNow
        Runtime.modVersion <- string this.ModManifest.Version
        Runtime.apiVersion <- string (typeof<IModHelper>.Assembly.GetName().Version)

        match Server.start Runtime.port with
        | Ok () ->
            this.Monitor.Log(
                sprintf "JunimoBridge %s 已启动，HTTP 监听 http://localhost:%d/" Runtime.modVersion Runtime.port,
                LogLevel.Alert
            )
        | Error err -> this.Monitor.Log("JunimoBridge HTTP 起不来: " + err, LogLevel.Error)

        // 虚拟输入的两个前缀。挂不上也不影响只读端点，所以失败只报错不抛。
        try
            harmony.PatchAll()
            let patched = harmony.GetPatchedMethods() |> Seq.length

            // 补丁名前缀列出来（数出来几个就报几个）——别写死名单：
            // 早先这里固定写着"（InputState.GetMouseState / GetKeyboardState）"，
            // 后来加了 Tool.DoFunction 的抑制补丁，日志就变成"2 个补丁（……只有一个名字）"的谎报。
            let names =
                harmony.GetPatchedMethods()
                |> Seq.map (fun m -> m.DeclaringType.Name + "." + m.Name)
                |> Seq.sort
                |> String.concat " / "

            this.Monitor.Log(sprintf "JunimoBridge 已应用 %d 个 Harmony 补丁（%s）" patched names, LogLevel.Info)
        with ex ->
            this.Monitor.Log("JunimoBridge Harmony 补丁失败: " + ex.Message, LogLevel.Error)

        helper.Events.GameLoop.UpdateTicking.Add(fun (_: UpdateTickingEventArgs) ->
            Runtime.tick <- Runtime.tick + 1L
            Runtime.isBackground <- not (Native.isForeground ())
            Background.tick ()
            Fishing.tickFinish ()

            // 冻结时间：/pause 之后每帧按回去，游戏自己以为时间没走
            if Runtime.wakePending > 0 then
                Runtime.wakePending <- Runtime.wakePending - 1

                if not (isNull StardewValley.Game1.player) then
                    StardewValley.Game1.player.isInBed.Value <- false

            if Runtime.timeFrozen then
                StardewValley.Game1.timeOfDay <- Runtime.frozenAt

            // 「按掉下一发落地」的兜底超时：动画那一发如果根本没来（比如切了地图），
            // 标记不能一直挂着 —— 挂久了会误按玩家自己下一次真正挥工具的那一发。
            if Runtime.suppressTicks > 0 then
                Runtime.suppressTicks <- Runtime.suppressTicks - 1

                if Runtime.suppressTicks = 0 then
                    Runtime.suppressToolUse <- false

            // 顺序有讲究：先处理按住到期（否则会多按一帧），再排空队列（动作当帧生效）
            Input.tick ()
            Frame.drain ()
            Ops.tick ())

        // 读档 / 回标题时把按住清掉，别把按键状态带进下一个场景
        helper.Events.GameLoop.SaveLoaded.Add(fun (_: SaveLoadedEventArgs) ->
            Input.release ()
            // /sleep 会手工把 isInBed 置 true；读档后晚几帧再清（那时 player 才真的就位）
            Runtime.wakePending <- 30)

        helper.Events.GameLoop.DayStarted.Add(fun (_: DayStartedEventArgs) ->
            if not (isNull StardewValley.Game1.player) then
                StardewValley.Game1.player.isInBed.Value <- false)
        helper.Events.GameLoop.ReturnedToTitle.Add(fun (_: ReturnedToTitleEventArgs) -> Input.release ())
