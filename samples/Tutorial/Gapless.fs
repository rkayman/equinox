// PoC for managing a contiguous sequence of ids, with a Reserve -> Confirm OR Release flow allowing removal of gaps due to identifiers going unused
// See Sequence.fs, which represents a far simpler and saner form of this
module Gapless

open System

let [<Literal>] Category = "Gapless"
let streamId = Equinox.StreamId.gen SequenceId.toString

// NOTE - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
module Events =

    type Item = { id : int64 }
    type Snapshotted = { reservations : int64[];  nextId : int64 }
    type Event =
        | Reserved of Item
        | Confirmed of Item
        | Released of Item
        | Snapshotted of Snapshotted
        interface TypeShape.UnionContract.IUnionContract
    let codec = FsCodec.SystemTextJson.CodecJsonElement.Create<Event>()

module Fold =

    type State = { reserved : Set<int64>; next : int64 }
    let initial = { reserved = Set.empty; next = 0L }
    module State =
        let ofInternal (lowWatermark : int64) (reserved : int64 seq) (confirmed : int64 seq) (released : int64 seq) : State =
            failwith "TODO"
        type InternalState =
            { reserved : Set<int64>; confirmed : Set<int64>; released : Set<int64>; next : int64 }
            member x.Evolve = function
                | Events.Reserved e -> { x with reserved = x.reserved |> Set.add e.id }
                | Events.Confirmed e -> { x with confirmed = x.confirmed |> Set.add e.id }
                | Events.Released e -> { x with reserved = x.reserved |> Set.remove e.id  }
                | Events.Snapshotted e -> { reserved = set e.reservations; confirmed = Set.empty; released = Set.empty; next = e.nextId }
            member x.ToState() =
                ofInternal x.next x.reserved x.confirmed x.released
        let toInternal (state : State) : InternalState =
            { reserved = state.reserved; confirmed = Set.empty; released = Set.empty; next = state.next }
    let fold (state : State) (xs : Events.Event seq) : State =
        let s = State.toInternal state
        let state' = (s,xs) ||> Seq.fold (fun s -> s.Evolve)
        state'.ToState()
    let isOrigin = function Events.Snapshotted _ -> true | _ -> false
    let snapshot state = Events.Snapshotted { reservations = Array.ofSeq state.reserved; nextId = state.next }

let decideReserve count (state : Fold.State) : int64 list*Events.Event list =
    failwith "TODO"

let decideConfirm item (state : Fold.State) : Events.Event list =
    failwith "TODO"

let decideRelease item (state : Fold.State) : Events.Event list =
    failwith "TODO"

type Service internal (resolve : SequenceId -> Equinox.Decider<Events.Event, Fold.State>) =

    member _.ReserveMany(series,count) : Async<int64 list> =
        let decider = resolve series
        decider.Transact(decideReserve count)

    member x.Reserve(series) : Async<int64> = async {
        let! res = x.ReserveMany(series, 1)
        return List.head res }

    member _.Confirm(series,item) : Async<unit> =
        let decider = resolve series
        decider.Transact(decideConfirm item)

    member _.Release(series,item) : Async<unit> =
        let decider = resolve series
        decider.Transact(decideRelease item)

let [<Literal>] appName = "equinox-tutorial-gapless"

module Cosmos =

    open Equinox.CosmosStore
    let private create (context, cache, accessStrategy) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, TimeSpan.FromMinutes 20.) // OR CachingStrategy.NoCaching
        let cat = CosmosStoreCategory(context, Events.codec, Fold.fold, Fold.initial, cacheStrategy, accessStrategy)
        Service(streamId >> Equinox.Decider.resolve Serilog.Log.Logger cat Category)

    module Snapshot =

        let create (context, cache) =
            let accessStrategy = AccessStrategy.Snapshot (Fold.isOrigin,Fold.snapshot)
            create (context, cache, accessStrategy)

    module RollingUnfolds =

        let create (context, cache) =
            let accessStrategy = AccessStrategy.RollingState Fold.snapshot
            create (context, cache, accessStrategy)
