namespace FSharp.Extensions.Joinads

type Clauses<'TMsg, 'TResult> = 
  Clauses of MailboxProcessor<'TMsg> * list<'TMsg -> Async<Async<'TResult>>>

type AgentBuilder() = 
  member x.Bind(v, f) = async.Bind(v, f)
  member x.ReturnFrom(v) = async.ReturnFrom(v)
  member x.Return(v) = async.Return(v)
  member x.Zero() = async.Zero()  
  member x.Delay(f) = async.Delay(f)
  member x.Run(a:Async<'T>) = a

  /// Binding on the inbox returns a clause. The body either
  /// fails (pattern matching failed) or returns an asynchronous
  /// workflow representing the body.
  member x.Bind(inbox:MailboxProcessor<'T>, f) = Clauses(inbox, [f])
  /// Aggregate clauses into a single Clauses value
  member x.Choose(Clauses(inbox, cl1), Clauses(_, cl2)) = Clauses(inbox, cl1 @ cl2)
  /// Fails...
  member x.Fail() = async { return failwith "!" }

  /// Binds on the collection of clauses
  member x.Bind(Clauses(inbox, clauses), f) = 
    inbox.Scan(fun msg ->
      clauses |> Seq.tryPick (fun clause ->
        try Some(Async.RunSynchronously(clause msg))
        with e -> None)) |> f

[<AutoOpen>]
module TopLevelAgentValues = 
  let agent = AgentBuilder()

