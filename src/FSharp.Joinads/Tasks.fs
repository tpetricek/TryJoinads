namespace FSharp.Extensions.Joinads

open System
open System.Threading.Tasks

//#if SILVERLIGHT
[<AutoOpen>]
module TaskExtensions =
  type System.Threading.Tasks.Task with
    static member WhenAll([<ParamArray>] tasks:Task<'T> []) =
      let tcs = TaskCompletionSource<_>()
      let res = Array.map (fun _ -> None) tasks
      let lockObj = obj()
      let setResult (index:int) (task:Task<'T>) = lock lockObj (fun () -> 
        try
          res.[index] <- Some task.Result
          if Seq.forall Option.isSome res then 
            tcs.TrySetResult(Array.map Option.get res) |> ignore
        with e -> tcs.TrySetException(e) |> ignore )

      tasks |> Array.iteri (fun i task ->
        task.ContinueWith(fun (t:Task<'T>) -> setResult i t) |> ignore)
      tcs.Task

    static member WhenAny([<ParamArray>] tasks:Task<'T> []) =
      let tcs = TaskCompletionSource<_>()
      let res = Array.map (fun _ -> Choice1Of3()) tasks
      let lockObj = obj()
      let setResult (index:int) (task:Task<'T>) = lock lockObj (fun () -> 
        res.[index] <- try Choice2Of3 task.Result with e -> Choice3Of3 e
        let succ = Seq.tryFindIndex (function Choice2Of3 _ -> true | _ -> false) res
        if succ.IsSome then 
          tcs.TrySetResult(tasks.[succ.Value]) |> ignore
        elif Seq.forall (function Choice3Of3 _ -> true | _ -> false) res then 
          tcs.TrySetException(Array.map (function 
            | Choice3Of3 e -> e | _ -> failwith "invalid operation") res) |> ignore )
       
      tasks |> Array.iteri (fun i task ->
        task.ContinueWith(fun (t:Task<'T>) -> setResult i t) |> ignore)
      tcs.Task
//#endif

type FutureBuilder() = 
  member x.Bind(t:Task<'T>, f:'T -> Task<'R>) =
    t.ContinueWith(fun (t:Task<_>) -> f t.Result).Unwrap()
  member x.Bind(t:Task, f:unit -> Task<'R>) =
    t.ContinueWith(fun (t:Task) -> f ()).Unwrap()
  member x.Return(v:'T) = Task.Factory.StartNew(fun () -> v)
  member x.ReturnFrom(t:Task<'T>) = t

  member x.Fail() : Task<'T> = 
    let tcs = TaskCompletionSource<_>()
    tcs.Task

  member x.Merge(t1:Task<'T1>, t2:Task<'T2>) : Task<'T1 * 'T2> =
    let t1 = t1.ContinueWith(fun (t:Task<'T1>) -> box t.Result)
    let t2 = t2.ContinueWith(fun (t:Task<'T2>) -> box t.Result)
    Task.WhenAll(t1, t2).ContinueWith(fun (t:Task<obj[]>) -> unbox t.Result.[0], unbox t.Result.[1])

  member x.Choose(t1:Task<'T>, t2:Task<'T>) = 
    Task.WhenAny(t1, t2).Unwrap()
        
  member x.Delay(f:unit -> Task<'T>) = fun () -> Task.Factory.StartNew(fun () -> f()).Unwrap()
  member x.Run(f) = f()
  member x.Zero() = Task.Factory.StartNew(Func<unit>(fun () -> ()))
  member x.Combine(t1:Task<unit>, f:unit -> Task<'T>) = x.Bind(t1, f)
  member x.For(input:seq<_>, f) = 
    let en = input.GetEnumerator()
    let rec loop () =
      if en.MoveNext() then x.Bind(f en.Current, loop)
      else en.Dispose(); x.Zero()
    loop()

[<AutoOpen>]
module TopLevelValues = 
  let future = FutureBuilder()