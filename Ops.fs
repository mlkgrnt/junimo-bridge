namespace JunimoBridge

open System
open System.Threading.Tasks

/// 每帧操作引擎。
///
/// 动作类请求（走位、挥工具、睡觉）都要跨很多帧，而且**不能 sleep**。
/// 这里把「一个需要跨帧完成的动作」表达成一个 step 闭包：每帧被调用一次，
/// 返回 Some 结果就表示完成；返回 None 表示还没好。
///
/// 同一时刻只允许一个操作（只有一个玩家，排队没有意义，反而会让状态更乱）。
module Ops =

    let mutable private running: (int -> J option) option = None
    let mutable private frames = 0
    let mutable private waiter: TaskCompletionSource<J> option = None

    /// 当前是否有操作在跑。
    let busy () = running.IsSome

    /// 已经开始多少帧（诊断用）。
    let elapsed () = frames

    /// 停掉当前操作。**必须结账**：不结账的话等待方要一直熬到自己的超时
    /// （真机抓到：/stop 之后 walk_to 还傻等 25 秒才回「没完成」）。
    let abort (reason: string) =
        match waiter with
        | Some tcs ->
            tcs.TrySetResult(JObj [ "ok", JBool false; "aborted", JBool true; "error", JStr reason ])
            |> ignore
        | None -> ()

        running <- None
        waiter <- None
        frames <- 0

    /// 启动一个跨帧操作，返回它的 Task。HTTP 侧自己决定等多久。
    let run (step: int -> J option) : Task<J> =
        let tcs = TaskCompletionSource<J>(TaskCreationOptions.RunContinuationsAsynchronously)
        running <- Some step
        waiter <- Some tcs
        frames <- 0
        tcs.Task

    /// 主线程每帧调用：推进当前操作，完成就结账。
    let tick () =
        match running with
        | None -> ()
        | Some step ->
            frames <- frames + 1

            let result =
                try
                    step frames
                with ex ->
                    Some(JObj [ "ok", JBool false; "error", JStr ex.Message ])

            match result with
            | None -> ()
            | Some value ->
                running <- None
                frames <- 0

                match waiter with
                | Some tcs ->
                    tcs.TrySetResult value |> ignore
                    waiter <- None
                | None -> ()

    /// 把一段「必须在游戏线程跑」的只读/即时工作丢过去并等结果（默认 2s）。
    /// 和 run/tick 的区别：这里的工作当帧就结束，不是跨帧操作。
    let awaitMain (work: unit -> J) (timeoutMs: int) : J =
        try
            let task = Frame.post work

            if not (task.Wait timeoutMs) then
                JObj [ "ok", JBool false; "error", JStr(sprintf "游戏线程 %dms 内没响应" timeoutMs) ]
            elif task.IsCompletedSuccessfully then
                task.Result
            else
                let inner =
                    if isNull (box task.Exception) then "unknown"
                    else task.Exception.GetBaseException().Message

                JObj [ "ok", JBool false; "error", JStr inner ]
        with ex ->
            JObj [ "ok", JBool false; "error", JStr ex.Message ]

    /// 等待一个操作的结果，带超时。超时会 abort 掉它，避免它继续动。
    let await (task: Task<J>) (timeoutMs: int) : J =
        if task.Wait(timeoutMs) && task.IsCompletedSuccessfully then
            task.Result
        else
            abort (sprintf "操作 %dms 内没完成，已中止" timeoutMs)

            JObj
                [ "ok", JBool false
                  "error", JStr(sprintf "操作 %dms 内没完成，已中止" timeoutMs) ]
