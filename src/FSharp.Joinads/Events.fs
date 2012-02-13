namespace FSharp.Extensions.Joinads

open System
open System.Reactive.Linq

type ObservableBuilder() = 
  member x.Bind(v, f) = Observable.SelectMany(v, Func<'T, IObservable<'R>>(f))
  member x.YieldFrom(v) = v
  member x.Yield(v) = Observable.Return(v)
  member x.Zero() = Observable.Empty()
  member x.Combine(o1, o2) = Observable.Merge [| o1; o2 |]
  member x.Delay(f) = f()

  member x.Return(v) = Observable.Return(v)
  member x.Choose(o1, o2) = Observable.Merge [| o1; o2 |]
  member x.Merge(o1, o2) = Observable.CombineLatest(o1, o2, fun a b -> a, b)
  member x.Fail() = Observable.Throw(new Exception("Failed"))

[<AutoOpen>]
module TopLevelEventValues = 
  let event = ObservableBuilder()
  
  type System.IObservable<'T> with
    member x.Add(f) = 
      x.Subscribe
        ({ new System.IObserver<'T> with
             member x.OnNext(v) = f v
             member x.OnError(e) = ()
             member x.OnCompleted() = () })

  type IEvent<'Delegate,'Args when 'Delegate : delegate<'Args,unit> and 'Delegate :> System.Delegate > with
    member x.AsObservable() =
      { new System.IObservable<_> with
          member x.Subscribe(obs) = 
            let nobs = 
              { new System.IObserver<_> with 
                  member x.OnNext(v) = obs.OnNext(v)
                  member x.OnError(v) = obs.OnError(v)
                  member x.OnCompleted() = obs.OnCompleted() }

            x.Subscribe(nobs) }

  

