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

            this.Monitor.Log(
                sprintf "JunimoBridge 已应用 %d 个 Harmony 补丁（InputState.GetMouseState / GetKeyboardState）" patched,
                LogLevel.Info
            )
        with ex ->
            this.Monitor.Log("JunimoBridge Harmony 补丁失败: " + ex.Message, LogLevel.Error)

        helper.Events.GameLoop.UpdateTicking.Add(fun (_: UpdateTickingEventArgs) ->
            Runtime.tick <- Runtime.tick + 1L

            // 冻结时间：/pause 之后每帧按回去，游戏自己以为时间没走
            if Runtime.wakePending > 0 then
                Runtime.wakePending <- Runtime.wakePending - 1

                if not (isNull StardewValley.Game1.player) then
                    StardewValley.Game1.player.isInBed.Value <- false

            if Runtime.timeFrozen then
                StardewValley.Game1.timeOfDay <- Runtime.frozenAt

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
