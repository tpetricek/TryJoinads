namespace FSharp.Extensions.Joinads

open System

type Async =
  /// Creates an asynchronous workflow that non-deterministically returns the 
  /// result of one of the two specified workflows (the one that completes
  /// first). This is similar to Task.WhenAny.
  static member WhenAny([<ParamArray>] works:Async<'T>[]) : Async<'T> = 
    Async.FromContinuations(fun (cont, econt, ccont) ->
      // Results from the two 
      let results = Array.map (fun _ -> Choice1Of3()) works
      let handled = ref false
      let lockObj = new obj()
      let synchronized f = lock lockObj f

      // Called when one of the workflows completes
      let complete () = 
        let op =
          synchronized (fun () ->
            // If we already handled result (and called continuation)
            // then ignore. Otherwise, if the computation succeeds, then
            // run the continuation and mark state as handled.
            // Only throw if all workflows failed.
            if !handled then ignore
            else
              let succ = Seq.tryPick (function Choice2Of3 v -> Some v | _ -> None) results
              match succ with 
              | Some value -> handled := true; (fun () -> cont value)
              | _ ->
                  if Seq.forall (function Choice3Of3 _ -> true | _ -> false) results then
                    let exs = Array.map (function Choice3Of3 ex -> ex | _ -> failwith "!") results
                    (fun () -> econt (AggregateException(exs)))
                  else ignore ) 
        // Actually run the continuation
        // (this shouldn't be done in the lock)
        op() 

      // Run a workflow and write result (or exception to a ref cell)
      let run index workflow = async {
        try
          let! res = workflow
          synchronized (fun () -> results.[index] <- Choice2Of3 res)
        with e -> 
          synchronized (fun () -> results.[index] <- Choice3Of3 e)
        complete() }

      // Start all work items - using StartImmediate, because it
      // should be started on the current synchronization context
      works |> Seq.iteri (fun index work -> 
        Async.StartImmediate(run index work)) )

  static member StartChildImmediate(work) = async {
    let agent = MailboxProcessor.Start(fun inbox ->
      inbox.Scan(function 
        | Choice1Of2 value -> Some(async {
            while true do
              let! msg = inbox.Receive()
              match msg with 
              | Choice2Of2 (repl:AsyncReplyChannel<_>) ->
                  repl.Reply(value)
              | _ -> failwith "Invalid operation" })
        | _ -> None))
    async { let! res = work
            agent.Post(Choice1Of2 res) }
    |> Async.StartImmediate
    return agent.PostAndAsyncReply(Choice2Of2) }

  static member ParallelImmediate([<ParamArray>] works:Async<'T>[]) : Async<'T[]> = 
    Async.FromContinuations(fun (cont, econt, ccont) ->
      let results = Array.map (fun _ -> Choice1Of3()) works
      let lockObj = new obj()
      let synchronized f = lock lockObj f

      // Called when one of the workflows completes
      let complete () =
        let op = synchronized (fun () ->
          if Seq.exists (function Choice1Of3 _ -> true | _ -> false) results then
            // Some computations are still running
            ignore
          elif Seq.forall (function Choice2Of3 _ -> true | _ -> false) results then
            // All computations successfully completed
            (fun () -> cont (Array.map (function Choice2Of3 v -> v | _ -> failwith "Error") results))
          else // Some computation has failed
            (fun () ->
                let exns = Seq.choose (function Choice3Of3 (e:exn) -> Some e | _ -> None) results 
                econt (new Exception(sprintf "Multiple errors: %A" exns))) )
        op()

      // Run a workflow and write result (or exception to a ref cell)
      let run index workflow = async {
        try
          let! res = workflow
          synchronized (fun () -> results.[index] <- Choice2Of3 res)
        with e -> 
          synchronized (fun () -> results.[index] <- Choice3Of3 e)
        complete() }

      // Start all work items - using StartImmediate, because it
      // should be started on the current synchronization context
      works |> Seq.iteri (fun index work -> 
        Async.StartImmediate(run index work)) )

      

[<AutoOpen>]
module AsyncTopLevel = 
  module Async = 
    let map f work = async {
      let! res = work
      return f res }

  type Microsoft.FSharp.Control.AsyncBuilder with
    
    member x.Fail<'T>() : Async<'T> = async { 
      return failwith "failed!" }

    member x.Choose(a, b) = Async.WhenAny(a, b)

    member x.Merge(a, b) = async {
      let works = [| Async.map box a; Async.map box b |]
      let! res = Async.ParallelImmediate works
      return unbox res.[0], unbox res.[1] }

    member x.Alias(a) = Async.StartChildImmediate(a)
