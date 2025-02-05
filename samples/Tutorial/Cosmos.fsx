﻿#if !LOCAL
// Compile Tutorial.fsproj by either a) right-clicking or b) typing
// dotnet build samples/Tutorial before attempting to send this to FSI with Alt-Enter
#if VISUALSTUDIO
#r "netstandard"
#endif
#I "bin/Debug/net6.0/"
#r "Serilog.dll"
#r "Serilog.Sinks.Console.dll"
#r "Newtonsoft.Json.dll"
#r "TypeShape.dll"
#r "Equinox.dll"
#r "Equinox.Core.dll"
#r "FSharp.UMX.dll"
#r "FsCodec.dll"
#r "FsCodec.SystemTextJson.dll"
#r "FSharp.Control.TaskSeq.dll"
#r "Microsoft.Azure.Cosmos.Client.dll"
#r "System.Net.Http"
#r "Serilog.Sinks.Seq.dll"
#r "Equinox.CosmosStore.dll"
#else
#r "nuget:Serilog.Sinks.Console"
#r "nuget:Serilog.Sinks.Seq"
#r "nuget:Equinox.CosmosStore, *-*"
#r "nuget:FsCodec.SystemTextJson, *-*"
#endif

module Log =

    open Serilog
    open Serilog.Events
    let verbose = true // false will remove lots of noise
    let log =
        let c = LoggerConfiguration()
        let c = if verbose then c.MinimumLevel.Debug() else c
        let c = c.WriteTo.Sink(Equinox.CosmosStore.Core.Log.InternalMetrics.Stats.LogSink()) // to power Log.InternalMetrics.dump
        let c = c.WriteTo.Seq("http://localhost:5341") // https://getseq.net
        let c = c.WriteTo.Console(if verbose then LogEventLevel.Debug else LogEventLevel.Information)
        c.CreateLogger()
    let dumpMetrics () = Equinox.CosmosStore.Core.Log.InternalMetrics.dump log

module Favorites =

    let Category = "Favorites"
    let streamId = Equinox.StreamId.gen id

    module Events =

        type Item = { sku : string }
        type Event =
            | Added of Item
            | Removed of Item
            interface TypeShape.UnionContract.IUnionContract
        let codec = FsCodec.SystemTextJson.CodecJsonElement.Create<Event>()

    module Fold =

        type State = string list
        let initial : State = []
        let evolve state = function
            | Events.Added {sku = sku } -> sku :: state
            | Events.Removed {sku = sku } -> state |> List.filter (fun x -> x <> sku)
        let fold s xs = Seq.fold evolve s xs

    type Command =
        | Add of string
        | Remove of string
    let interpret command state =
        match command with
        | Add sku -> if state |> List.contains sku then [] else [ Events.Added {sku = sku}]
        | Remove sku -> if state |> List.contains sku then [ Events.Removed {sku = sku}] else []

    type Service internal (resolve : string -> Equinox.Decider<Events.Event, Fold.State>) =

        member _.Favorite(clientId, sku) =
            let decider = resolve clientId
            decider.Transact(interpret (Add sku))
        member _.Unfavorite(clientId, skus) =
            let decider = resolve clientId
            decider.Transact(interpret (Remove skus))
        member _.List clientId: Async<string list> =
            let decider = resolve clientId
            decider.Query id

    let create resolve = Service(streamId >> resolve Category)

    module Cosmos =

        open Equinox.CosmosStore // Everything outside of this module is completely storage agnostic so can be unit tested simply and/or bound to any store
        let accessStrategy = AccessStrategy.Unoptimized // Or Snapshot etc https://github.com/jet/equinox/blob/master/DOCUMENTATION.md#access-strategies
        let create (context, cache) =
            let cacheStrategy = CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.) // OR CachingStrategy.NoCaching
            CosmosStoreCategory(context, Events.codec, Fold.fold, Fold.initial, cacheStrategy, accessStrategy)
            |> Equinox.Decider.resolve Log.log
            |> create

let [<Literal>] appName = "equinox-tutorial"

module Store =

    open Equinox.CosmosStore

    let read key = System.Environment.GetEnvironmentVariable key |> Option.ofObj |> Option.get
    // default connection mode is `Direct`; we use Gateway mode here to reduce connectivity potential issues. Ideally you want to remove that for production for perf reasons
    let discovery = Discovery.ConnectionString (read "EQUINOX_COSMOS_CONNECTION")
    let connector = CosmosStoreConnector(discovery, System.TimeSpan.FromSeconds 5., 2, System.TimeSpan.FromSeconds 5., Microsoft.Azure.Cosmos.ConnectionMode.Gateway)

    let storeClient = CosmosStoreClient.Connect(connector.CreateAndInitialize, read "EQUINOX_COSMOS_DATABASE", read "EQUINOX_COSMOS_CONTAINER") |> Async.RunSynchronously
    let context = CosmosStoreContext(storeClient, tipMaxEvents = 10)
    let cache = Equinox.Cache(appName, 20)

let service = Favorites.Cosmos.create (Store.context, Store.cache)

let client = "ClientJ"

service.Favorite(client, "a") |> Async.RunSynchronously
service.Favorite(client, "b") |> Async.RunSynchronously
service.List(client) |> Async.RunSynchronously

service.Unfavorite(client, "b") |> Async.RunSynchronously
service.List(client) |> Async.RunSynchronously

Log.dumpMetrics ()

(* EXAMPLE OUTPUT

[13:48:33 INF] EqxCosmos Response 5/5 Backward 189ms i=0 rc=3.43
[13:48:33 INF] EqxCosmos QueryB Favorites-ClientJ v5 5/1 190ms rc=3.43
[13:48:33 DBG] No events generated
[13:48:33 INF] EqxCosmos Tip 200 90ms rc=1
[13:48:33 INF] EqxCosmos Response 0/0 Forward 179ms i=null rc=3.62
[13:48:33 INF] EqxCosmos QueryF Favorites-ClientJ v0 0/1 179ms rc=3.62
[13:48:33 INF] EqxCosmos Sync: Conflict writing ["Added"]
[13:48:33 INF] EqxCosmos Sync 1+0 90ms rc=5.4
[13:48:33 INF] EqxCosmos Tip 200 86ms rc=1
[13:48:33 INF] EqxCosmos Response 5/5 Forward 184ms i=0 rc=4.37
[13:48:33 INF] EqxCosmos QueryF Favorites-ClientJ v5 5/1 185ms rc=4.37
[13:48:33 DBG] Resyncing and retrying
[13:48:33 INF] EqxCosmos Sync 1+0 96ms rc=37.67
[13:48:34 INF] EqxCosmos Tip 304 90ms rc=1
[13:48:34 INF] EqxCosmos Tip 304 92ms rc=1
[13:48:34 INF] EqxCosmos Sync 1+0 96ms rc=37.33
[13:48:34 INF] EqxCosmos Tip 304 87ms rc=1
[13:48:34 INF] Read: 8 requests costing 16 RU (average: 2.05); Average latency: 125ms
[13:48:34 INF] Write: 3 requests costing 80 RU (average: 26.80); Average latency: 94ms
[13:48:34 INF] TOTAL: 11 requests costing 97 RU (average: 8.80); Average latency: 116ms
[13:48:34 INF] rps 2 = ~21 RU *)
