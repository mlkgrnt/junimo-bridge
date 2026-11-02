namespace JunimoBridge

open System
open System.Collections.Concurrent
open System.Threading.Tasks

/// 主线程工作队列。
///
/// 游戏状态只能在游戏循环里碰（HTTP 线程去读 Farmer 的字段是数据竞争）。
/// 所以约定：HTTP 侧 `post` 一个闭包进来，然后 await；游戏侧每帧 `drain` 一次。
///
/// **绝不在这里 sleep**。等待一律表达成「下一帧再检查」，而不是把主循环冻住 ——
/// NagiBridge 的 `/key hold` 就是栽在这上面（睡的时候游戏一帧都没跑，松开时已经晚了）。
module Frame =

    let private queue = ConcurrentQueue<unit -> unit>()

    /// 投一个带返回值的任务进主线程队列。
    let post (work: unit -> 'a) : Task<'a> =
        let tcs = TaskCompletionSource<'a>(TaskCreationOptions.RunContinuationsAsynchronously)

        queue.Enqueue(fun () ->
            try
                tcs.TrySetResult(work ()) |> ignore
            with ex ->
                tcs.TrySetException ex |> ignore)

        tcs.Task

    /// 投一个不关心返回值的任务。
    let postUnit (work: unit -> unit) : Task<unit> =
        post (fun () ->
            work ()
            ())

    /// 在游戏循环里排空队列。异常已经写进各自的 TCS，这里吞掉，
    /// 不能因为一个坏请求把游戏循环带崩。
    let drain () =
        let mutable go = true

        while go do
            match queue.TryDequeue() with
            | true, work ->
                try
                    work ()
                with _ ->
                    ()
            | _ -> go <- false

    /// 还有多少请求没被执行（/status 里能看出主线程是不是卡了）。
    let pending () = queue.Count
