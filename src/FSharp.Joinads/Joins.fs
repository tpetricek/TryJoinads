// ----------------------------------------------------------------------------
// F# Joinads Samples - Implementation of 'match!' for joins (Joins.fs)
// (c) Tomas Petricek, 2011, Available under Apache 2.0 license.
// ----------------------------------------------------------------------------
#if SILVERLIGHT
#nowarn "66"
#endif
#nowarn "40"
namespace FSharp.Extensions.Joinads

open System
open System.Threading
open System.Collections.Generic
open System.Collections.Concurrent

// ----------------------------------------------------------------------------
// Functionality that's missing (or not working) in Silverlight
// ----------------------------------------------------------------------------

#if SILVERLIGHT

/// Reimplements F# Event<_>, but exposes Silverlight-compatible IObservable<_>
type Event<'T>() = 
  let handlers = ResizeArray<Handler<_>>()
  member x.Trigger(arg:'T) = 
    handlers |> List.ofSeq |> Seq.iter (fun h -> h.Invoke(null, arg))
  member x.Publish =
    { new IObservable<'T> with
        member e.Subscribe(observer) = 
            let h = new Handler<_>(fun sender args -> observer.OnNext(args))
            handlers.Add(h)
            { new System.IDisposable with 
                member x.Dispose() = handlers.Remove(h) |> ignore } }
                
module Observable =
  /// Reimplemetns Observable.merge for the Silverlight-compatible IObservable<_> 
  let merge (w1: IObservable<'T>) (w2: IObservable<'T>) =
      { new IObservable<_> with 
          member x.Subscribe(observer) =
            let stopped = ref false
            let completed1 = ref false
            let completed2 = ref false
            let createHandler completed = 
              { new IObserver<'T> with  
                  member x.OnNext(v) = 
                    if not !stopped then observer.OnNext v
                  member x.OnError(e) = 
                    if not !stopped then stopped := true; observer.OnError(e)
                  member x.OnCompleted() = 
                    if not !stopped then 
                      completed := true; 
                      if !completed1 && !completed2 then 
                        stopped := true; observer.OnCompleted() } 
            
            let h1 = w1.Subscribe (createHandler completed1)              
            let h2 = w2.Subscribe (createHandler completed2)
            { new IDisposable with 
                  member x.Dispose() = h1.Dispose(); h2.Dispose() } }

/// Inefficient implementation of simple CountdownEvent
type CountdownEvent(count) = 
  let lockObj = new obj()
  let count = ref count
  member x.Signal() = 
    lock lockObj (fun () -> decr count; !count = 0)
#endif

// ----------------------------------------------------------------------------
// Helpers (that are used in the implementation of joins)
// ----------------------------------------------------------------------------

/// Represents a simple 'transactional' asynchronous workflow
/// A value is given to the continuation, together with a function
/// that should be called to accept or reject the value.
type TransactionalAsync<'T> = 
  TA of (('T * (bool -> unit) -> unit) -> unit)


/// Represents a signal that has some Boolean state and 
/// triggers an event when the state changes to true.
type ISignal =
  abstract Signalled : IObservable<unit>
  abstract State : bool


/// Concrete signal that can be signalled using the 'Set' method
type Signal() = 
  [<VolatileField>]
  let mutable state = false
  let signalled = new Event<_>()
  
  member x.Set(newState) =
    state <- newState
    if state then signalled.Trigger()

  member x.Publish = 
    { new ISignal with 
        member x.Signalled = signalled.Publish :> IObservable<_>
        member x.State = state }

/// Operations for working with signals
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Signal = 
  /// Create a signal whose state is calculated from the state of two other singals
  let merge op (sig1:ISignal) (sig2:ISignal) = 
    { new ISignal with
        member x.State = op sig1.State sig2.State
        member x.Signalled = 
          Observable.merge sig1.Signalled sig2.Signalled }
//          |> Observable.filter (fun () -> x.State) }

  /// Signalled when both signals are signalled
  let both sig1 sig2 = merge (&&) sig1 sig2
  /// Signalled when any of the signals is signalled
  let either sig1 sig2 = merge (||) sig1 sig2


[<AutoOpen>]
module AsyncExtensions = 
  type ISignal with 
    /// Creates an asynchronous workflow that waits until a signal is signalled.
    member x.AsyncAwait() =
      Async.FromContinuations(fun (cont,econt,ccont) -> 
        // If already signalled, then run immediately
        if x.State then cont()
        else
          let lockObj = new obj()
          let called = ref false

          // Guarantees that the continuation is called only once
          let contOnce () = 
            if not called.Value then
              let doCall =
                lock lockObj (fun () ->
                  if not called.Value then 
                    called := true; true
                  else false )
              if doCall then cont()

          // When signalled, call continuation & cleanup
          let rec finish() = 
            if remover <> null then remover.Dispose()
            contOnce()
            called := true
          and remover : IDisposable = 
            x.Signalled.Subscribe(finish)

          // When signalled while registering, call & cleanup
          if x.State then 
            remover.Dispose()
            contOnce() )


  type Microsoft.FSharp.Control.Async with
    /// Waits until transactional async produces a value 
    /// accepts it and returns it to a workflow.
    static member AwaitTransactionalAsync(TA ta) = 
      Async.FromContinuations(fun (cont, _, _) ->
        ta (fun (value, commit) ->
          commit true
          cont value ))


// ----------------------------------------------------------------------------
// Representation of a join pattern body
// ----------------------------------------------------------------------------

/// A reply channel that is used for replying from synchronous channels
type IReplyChannel<'T> = 
  /// Creates a join pattern operation that sends reply back to the caller
  abstract Reply : 'T -> unit

// ----------------------------------------------------------------------------
// Implementation of Join channels
// ----------------------------------------------------------------------------

/// An abstract representation of a channels that are used in join calculus.
/// It provides a signal signalling whether channel contains a value and an
/// operation that can be used to request the value (implementing two-phase
/// commit protocol)
type IChannel<'T> = 
  /// Signalled when the channel contains a value
  abstract Available : ISignal

  /// When called, returns a transactional async containing the 
  /// value (when available) or None.
  abstract Query : unit -> TransactionalAsync<option<'T>>


[<AutoOpen>]
module ChannelExtensions = 
  type IChannel<'T> with 
    /// Asynchronously get the next available value from a channel.
    member x.Receive() = async {
      do! x.Available.AsyncAwait()
      let! v = Async.AwaitTransactionalAsync(x.Query())
      match v with
      | None -> 
          return! x.Receive()
      | Some v -> return v }

/// Represents a concrete channel that can be used in join patterns
type Channel<'T>() =
  let queue = new Queue<'T>()
  let evt = new Signal()
  let lockObj = new obj()
  let synchronized f = lock lockObj f
  
  /// Send message to the channel (without blocking)
  member x.Put(message:'T) = x.Call(message)

  /// Send message to the channel (without blocking)
  /// (This function should be used from outside of join pattern body)
  member x.Call(message:'T) =
    synchronized (fun () ->
      queue.Enqueue(message))
    evt.Set(true)

  interface IChannel<'T> with
    /// Signalled when the channel contains a value
    member x.Available = evt.Publish

    /// When called, returns a transactional async containing the 
    /// value (when available) or None.
    member x.Query() = TA (fun tcont ->
      Monitor.Enter(lockObj)

      let commit b1 b2 =
        // If commited, then remove the value
        if b1 && b2 then queue.Dequeue() |> ignore
        if queue.Count = 0 then evt.Set(false) |> ignore
        Monitor.Exit(lockObj)

      if queue.Count > 0 then 
        tcont (Some(queue.Peek()), commit true)
      else tcont (None, commit false))
 

/// Represents a channel created by a projection 
/// (The actual value is left in the source channel)
type ProjectionChannel<'T, 'R>(f:'T -> 'R, channel:IChannel<'T>) =
  interface IChannel<'R> with 
    member x.Available = channel.Available 
    member x.Query() = TA (fun tcont ->
      let (TA ta) = channel.Query() 
      ta (fun (value, commit) -> tcont (Option.map f value, commit)) )

/// Represents a channel created by a merging of two channels
/// (The actual values are left in the source channels)    
type MergeChannel<'T1, 'T2>(channel1:IChannel<'T1>, channel2:IChannel<'T2>) =
  interface IChannel<'T1 * 'T2> with
    member x.Available = Signal.both channel1.Available channel2.Available
    member x.Query() = TA (fun tcont ->
      
      // Query both channels and emit value only when both
      // contain a value. Comitting removes values from both sources.
      let (TA ta1) = channel1.Query()
      let (TA ta2) = channel2.Query()

      let evt = new CountdownEvent(2)
      let value1 = ref None
      let value2 = ref None
      let commit1 = ref None
      let commit2 = ref None
      
      // Comit to get the value from both channels
      let finalCommit b1 b2 = 
        commit1.Value.Value(b1 && b2)
        commit2.Value.Value(b1 && b2)
      
      // Called when value from a channel is received
      let continuation commitTo valueTo (value, commit) = 
        commitTo := Some commit
        valueTo := value
        if evt.Signal() then 
          match value1.Value, value2.Value with
          | Some v1, Some v2 -> 
              tcont (Some(v1, v2), finalCommit true)
          | _ ->
              tcont (None, finalCommit false)
      
      ta1 (continuation commit1 value1)
      ta2 (continuation commit2 value2) )


/// Represents a channel created by choosing between two channels
/// (The actual values are left in the source channels)
type ChoiceChannel<'T>(channel1:IChannel<'T>, channel2:IChannel<'T>) =
  interface IChannel<'T> with
    member x.Available = Signal.either channel1.Available channel2.Available
    member x.Query() = TA (fun tcont ->

      // Query both channels, return the first non-None value that is 
      // available or 'None' if there is no value in eiter channel.
      let (TA ta1) = channel1.Query()
      let (TA ta2) = channel2.Query()
      
      let lockObj = new obj()
      let synchronized f = lock lockObj f
      let called = ref false
      let counter = ref 0

      // Call the resulting continuation with a first non-None result
      // (use locks to avoid races and calling the continuation twice)
      let continuation (value:option<_>, commit) =
        let call = synchronized(fun () ->
          incr counter
          // If we have not called continuation yet and
          // 1) we've got a value or 2) we've got second None
          if not called.Value && (value.IsSome || !counter = 2) then
            called := true
            (fun () -> tcont (value, fun b -> 
              commit (b && value.IsSome)))
          else 
            (fun () -> commit false))
        call ()

      ta1 continuation
      ta2 continuation)

// ----------------------------------------------------------------------------
// Simpler interface for channels
// ----------------------------------------------------------------------------

/// A synchronous channel. Calling the channel eventually gives a result, 
/// so the call is logically blocking. (Thanks to async workflows, no actual
/// threads are blocked when waiting)
type ReplyChannel<'TRes>() =
  inherit Channel<IReplyChannel<'TRes>>()

  /// Send message to the channel and resume the returned
  /// asynchronous workflow when a reply is available
  member x.AsyncCall() =
    Async.FromContinuations(fun (cont, _, _) ->
      x.Call({ new IReplyChannel<_> with
                 member x.Reply(v) = cont v }))


/// A synchronous channel with argument. Calling the channel eventually gives a 
/// result, so the call is logically blocking. (Thanks to async workflows, no actual
/// threads are blocked when waiting)
type ReplyChannel<'TArg, 'TRes>() =
  inherit Channel<'TArg * IReplyChannel<'TRes>>()

  /// Send message to the channel and resume the returned
  /// asynchronous workflow when a reply is available
  member x.AsyncCall(arg) =
    Async.FromContinuations(fun (cont, _, _) ->
      x.Call(arg, { new IReplyChannel<_> with
                      member x.Reply(v) = cont v }))

module Channel = 
  /// Creates an aliased channel that contains projected values
  let map f channel = 
    (new ProjectionChannel<_, _>(f, channel)) :> IChannel<_>

  /// Creates an aliased channel that merges two channels
  let merge ch1 ch2 = 
    (new MergeChannel<_, _>(ch1, ch2)) :> IChannel<_>

  /// Creates an aliased channel that contains values from either channel
  let choice ch1 ch2 =
    (new ChoiceChannel<_>(ch1, ch2)) :> IChannel<_>

  /// Runs a join program that is represented as a channel yielding reactions
  let run (join:IChannel<unit -> unit>) =
    async { while true do 
              // Get the next body and start it in background
              let! jops = join.Receive()
              Async.Start(async { do jops() }) }
    |> Async.Start


// ----------------------------------------------------------------------------
// Computation expression builders for joins & replies and public operations
// ----------------------------------------------------------------------------

[<AutoOpen>]
module Builders =
  type JoinBuilder() =
    member x.Choose(a, b) = Channel.choice a b
    member x.Merge(a, b) = Channel.merge a b
    member x.Run(a) = Channel.run a
    member x.Select(a, f:_ -> unit) = Channel.map (fun v () -> f v) a

//  type JoinReplyBuilder() = 
//    member x.Yield(op:JoinOperation) : JoinReaction = [ op ]
//    member x.Combine(op1:JoinReaction, op2:JoinReaction) : JoinReaction = (op1 @ op2)
//    member x.Delay(f:unit -> JoinReaction) = f()

  /// Computation expression builder that can be used 
  /// for encoding join patterns
  let join = JoinBuilder()

  /// Computation expression builder that is used for 
  /// writing reactions in join patterns
  // let react = JoinReplyBuilder()