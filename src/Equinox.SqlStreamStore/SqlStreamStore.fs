﻿namespace Equinox.SqlStreamStore

open Equinox.Core
open Serilog
open SqlStreamStore
open SqlStreamStore.Streams
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type EventBody = ReadOnlyMemory<byte>
type EventData = NewStreamMessage
type IEventStoreConnection = IStreamStore
type ResolvedEvent = StreamMessage
type StreamEventsSlice = ReadStreamPage

[<RequireQualifiedAccess>]
type Direction = Forward | Backward with
    override this.ToString() = match this with Forward -> "Forward" | Backward -> "Backward"

module Log =

    /// <summary>Name of Property used for <c>Metric</c> in <c>LogEvent</c>s.</summary>
    let [<Literal>] PropertyTag = "ssEvt"

    [<NoEquality; NoComparison>]
    type Measurement = { stream: string; interval: StopwatchInterval; bytes: int; count: int }
    [<NoEquality; NoComparison>]
    type Metric =
        | WriteSuccess of Measurement
        | WriteConflict of Measurement
        | Slice of Direction * Measurement
        | Batch of Direction * slices: int * Measurement
    let [<return: Struct>] (|MetricEvent|_|) (logEvent: Serilog.Events.LogEvent): Metric voption =
        let mutable p = Unchecked.defaultof<_>
        logEvent.Properties.TryGetValue(PropertyTag, &p) |> ignore
        match p with Log.ScalarValue (:? Metric as e) -> ValueSome e | _ -> ValueNone

    /// Attach a property to the captured event record to hold the metric information
    let internal event (value: Metric) = Internal.Log.withScalarProperty PropertyTag value
    let prop name value (log: ILogger) = log.ForContext(name, value)
    let propEvents name (kvps: System.Collections.Generic.KeyValuePair<string, string> seq) (log: ILogger) =
        let items = seq { for kv in kvps do yield sprintf "{\"%s\": %s}" kv.Key kv.Value }
        log.ForContext(name, sprintf "[%s]" (String.concat ",\n\r" items))
    let propEventData name (events: EventData[]) (log: ILogger) =
        log |> propEvents name (seq {
            for x in events do
                yield System.Collections.Generic.KeyValuePair<_, _>(x.Type, x.JsonData) })
    let propResolvedEvents name (events: ResolvedEvent[]) (log: ILogger) =
        log |> propEvents name (seq {
            for x in events do
                let data = x.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
                yield System.Collections.Generic.KeyValuePair<_, _>(x.Type, data) })

    let withLoggedRetries<'t> retryPolicy (contextLabel: string) (f: ILogger -> CancellationToken -> Task<'t>) log ct: Task<'t> =
        match retryPolicy with
        | None -> f log ct
        | Some retryPolicy ->
            let withLoggingContextWrapping count =
                let log = if count = 1 then log else log |> prop contextLabel count
                f log
            retryPolicy withLoggingContextWrapping
    let (|BlobLen|) = function null -> 0 | (x: byte[]) -> x.Length
    let (|StrLen|) = function null -> 0 | (x: string) -> x.Length

    /// NB Caveat emptor; this is subject to unlimited change without the major version changing - while the `dotnet-templates` repo will be kept in step, and
    /// the ChangeLog will mention changes, it's critical to not assume that the presence or nature of these helpers be considered stable
    module InternalMetrics =

        module Stats =
            let inline (|Stats|) ({ interval = i }: Measurement) = int64 i.ElapsedMilliseconds

            let (|Read|Write|Resync|Rollup|) = function
                | Slice (_, Stats s) -> Read s
                | WriteSuccess (Stats s) -> Write s
                | WriteConflict (Stats s) -> Resync s
                // slices are rolled up into batches so be sure not to double-count
                | Batch (_, _, Stats s) -> Rollup s
            type Counter =
                { mutable count: int64; mutable ms: int64 }
                static member Create() = { count = 0L; ms = 0L }
                member x.Ingest(ms) =
                    Interlocked.Increment(&x.count) |> ignore
                    Interlocked.Add(&x.ms, ms) |> ignore
            type LogSink() =
                static let epoch = System.Diagnostics.Stopwatch.StartNew()
                static member val Read = Counter.Create() with get, set
                static member val Write = Counter.Create() with get, set
                static member val Resync = Counter.Create() with get, set
                static member Restart() =
                    LogSink.Read <- Counter.Create()
                    LogSink.Write <- Counter.Create()
                    LogSink.Resync <- Counter.Create()
                    let span = epoch.Elapsed
                    epoch.Restart()
                    span
                interface Serilog.Core.ILogEventSink with
                    member _.Emit logEvent = logEvent |> function
                        | MetricEvent (Read stats) -> LogSink.Read.Ingest stats
                        | MetricEvent (Write stats) -> LogSink.Write.Ingest stats
                        | MetricEvent (Resync stats) -> LogSink.Resync.Ingest stats
                        | MetricEvent (Rollup _) -> ()
                        | _ -> ()

        /// Relies on feeding of metrics from Log through to Stats.LogSink
        /// Use Stats.LogSink.Restart() to reset the start point (and stats) where relevant
        let dump (log: ILogger) =
            let stats =
              [ "Read", Stats.LogSink.Read
                "Write", Stats.LogSink.Write
                "Resync", Stats.LogSink.Resync ]
            let logActivity name count lat =
                log.Information("{name}: {count:n0} requests; Average latency: {lat:n0}ms",
                    name, count, (if count = 0L then Double.NaN else float lat/float count))
            let mutable rows, totalCount, totalMs = 0, 0L, 0L
            for name, stat in stats do
                if stat.count <> 0L then
                    totalCount <- totalCount + stat.count
                    totalMs <- totalMs + stat.ms
                    logActivity name stat.count stat.ms
                    rows <- rows + 1
            // Yes, there's a minor race here between the use of the values and the reset
            let duration = Stats.LogSink.Restart()
            if rows > 1 then logActivity "TOTAL" totalCount totalMs
            let measures: (string * (TimeSpan -> float)) list = [ "s", fun x -> x.TotalSeconds(*; "m", fun x -> x.TotalMinutes; "h", fun x -> x.TotalHours*) ]
            let logPeriodicRate name count = log.Information("rp{name} {count:n0}", name, count)
            for uom, f in measures do let d = f duration in if d <> 0. then logPeriodicRate uom (float totalCount/d |> int64)

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type EsSyncResult = Written of AppendResult | ConflictUnknown

module private Write =
    /// Yields `EsSyncResult.Written` or `EsSyncResult.Conflict` to signify WrongExpectedVersion
    let private writeEventsAsync (log: ILogger) (conn: IEventStoreConnection) (streamName: string) (version: int64) (events: EventData[]) ct
        : Task<EsSyncResult> = task {
        try let! wr = conn.AppendToStream(StreamId streamName, (if version = -1L then ExpectedVersion.NoStream else int version), events, ct)
            return EsSyncResult.Written wr
        with :? WrongExpectedVersionException as ex ->
            log.Information(ex, "SqlEs TrySync WrongExpectedVersionException writing {EventTypes}, expected {ExpectedVersion}",
                [| for x in events -> x.Type |], version)
            return EsSyncResult.ConflictUnknown }
    let eventDataBytes events =
        let eventDataLen (x: NewStreamMessage) = match x.JsonData |> System.Text.Encoding.UTF8.GetBytes, x.JsonMetadata |> System.Text.Encoding.UTF8.GetBytes with Log.BlobLen bytes, Log.BlobLen metaBytes -> bytes + metaBytes
        events |> Array.sumBy eventDataLen
    let private writeEventsLogged (conn: IEventStoreConnection) (streamName: string) (version: int64) (events: EventData[]) (log: ILogger) ct
        : Task<EsSyncResult> = task {
        let log = if (not << log.IsEnabled) Events.LogEventLevel.Debug then log else log |> Log.propEventData "Json" events
        let bytes, count = eventDataBytes events, events.Length
        let log = log |> Log.prop "bytes" bytes
        let writeLog = log |> Log.prop "stream" streamName |> Log.prop "expectedVersion" version |> Log.prop "count" count
        let! t, result = writeEventsAsync writeLog conn streamName version events |> Stopwatch.time ct
        let reqMetric: Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let resultLog, evt =
            match result, reqMetric with
            | EsSyncResult.Written x, m ->
                log |> Log.prop "currentVersion" x.CurrentVersion |> Log.prop "currentPosition" x.CurrentPosition, Log.WriteSuccess m
            | EsSyncResult.ConflictUnknown, m ->
                log, Log.WriteConflict m
        (resultLog |> Log.event evt).Information("SqlEs{action:l} count={count} conflict={conflict}",
            "Write", events.Length, match evt with Log.WriteConflict _ -> true | _ -> false)
        return result }
    let writeEvents (log: ILogger) retryPolicy (conn: IEventStoreConnection) (streamName: string) (version: int64) (events: EventData[]) ct
        : Task<EsSyncResult> =
        let call = writeEventsLogged conn streamName version events
        Log.withLoggedRetries retryPolicy "writeAttempt" call log ct

module private Read =
    open FSharp.Control
    let private readSliceAsync (conn: IEventStoreConnection) (streamName: string) (direction: Direction) (batchSize: int) (startPos: int64) ct
        : Task<StreamEventsSlice> =
        match direction with
        | Direction.Forward ->  conn.ReadStreamForwards(streamName, int startPos, batchSize, ct)
        | Direction.Backward -> conn.ReadStreamBackwards(streamName, int startPos, batchSize, ct)
    let (|ResolvedEventLen|) (x: StreamMessage) =
        let data = x.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
        match data, x.JsonMetadata with Log.StrLen bytes, Log.StrLen metaBytes -> bytes + metaBytes
    let private loggedReadSlice conn streamName direction batchSize startPos (log: ILogger) ct: Task<ReadStreamPage> = task {
        let! t, slice = readSliceAsync conn streamName direction batchSize startPos |> Stopwatch.time ct
        let bytes, count = slice.Messages |> Array.sumBy (|ResolvedEventLen|), slice.Messages.Length
        let reqMetric: Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count }
        let evt = Log.Slice (direction, reqMetric)
        let log = if (not << log.IsEnabled) Events.LogEventLevel.Debug then log else log |> Log.propResolvedEvents "Json" slice.Messages
        (log |> Log.prop "startPos" startPos |> Log.prop "bytes" bytes |> Log.event evt).Information("SqlEs{action:l} count={count} version={version}",
            "Read", count, slice.LastStreamVersion)
        return slice }
    let private readBatches (log: ILogger) (readSlice: int64 -> ILogger -> CancellationToken -> Task<StreamEventsSlice>)
            (maxPermittedBatchReads: int option) (startPosition: int64) ct
        : IAsyncEnumerable<int64 option * ResolvedEvent[]> =
        let rec loop batchCount pos: IAsyncEnumerable<int64 option * ResolvedEvent[]> = taskSeq {
            match maxPermittedBatchReads with
            | Some mpbr when batchCount >= mpbr -> log.Information "batch Limit exceeded"; invalidOp "batch Limit exceeded"
            | _ -> ()

            let batchLog = log |> Log.prop "batchIndex" batchCount
            let! slice = readSlice pos batchLog ct
            match slice.Status with
            | PageReadStatus.StreamNotFound -> yield Some (int64 ExpectedVersion.EmptyStream), Array.empty // NB NoStream in ES version= -1
            | PageReadStatus.Success ->
                let version = if batchCount = 0 then Some (int64 slice.LastStreamVersion) else None
                yield version, slice.Messages
                if not slice.IsEnd then
                    yield! loop (batchCount + 1) (int64 slice.NextStreamVersion)
            | x -> raise <| ArgumentOutOfRangeException("SliceReadStatus", x, "Unknown result value") }
        loop 0 startPosition
    let resolvedEventBytes events = events |> Array.sumBy (|ResolvedEventLen|)
    let logBatchRead direction streamName t events batchSize version (log: ILogger) =
        let bytes, count = resolvedEventBytes events, events.Length
        let reqMetric: Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let batches = (events.Length - 1)/batchSize + 1
        let action = match direction with Direction.Forward -> "LoadF" | Direction.Backward -> "LoadB"
        let evt = Log.Metric.Batch (direction, batches, reqMetric)
        (log |> Log.prop "bytes" bytes |> Log.event evt).Information(
            "SqlEs{action:l} stream={stream} count={count}/{batches} version={version}",
            action, streamName, count, batches, version)
    let loadForwardsFrom (log: ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName startPosition ct
        : Task<int64 * ResolvedEvent[]> = task {
        let mergeBatches (batches: IAsyncEnumerable<int64 option * ResolvedEvent[]>) = task {
            let mutable versionFromStream = None
            let! (events: ResolvedEvent[]) =
                batches
                |> TaskSeq.collectSeq (function None, events -> events | Some _ as reportedVersion, events -> versionFromStream <- reportedVersion; events)
                |> TaskSeq.toArrayAsync
            let version = match versionFromStream with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, events }
        let call = loggedReadSlice conn streamName Direction.Forward batchSize
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let direction = Direction.Forward
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "direction" direction |> Log.prop "stream" streamName
        let batches ct: IAsyncEnumerable<int64 option * ResolvedEvent[]> = readBatches log retryingLoggingReadSlice maxPermittedBatchReads startPosition ct
        let! t, (version, events) = (batches >> mergeBatches) |> Stopwatch.time ct
        log |> logBatchRead direction streamName t events batchSize version
        return version, events }
    let partitionPayloadFrom firstUsedEventNumber: ResolvedEvent[] -> int * int =
        let acc (tu, tr) (ResolvedEventLen bytes as y) = if y.Position < firstUsedEventNumber then tu, tr + bytes else tu + bytes, tr
        Array.fold acc (0, 0)
    let loadBackwardsUntilCompactionOrStart (log: ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName (tryDecode, isOrigin) ct
        : Task<int64 * struct (ResolvedEvent * 'event voption)[]> = task {
        let mergeFromCompactionPointOrStartFromBackwardsStream (log: ILogger) (batchesBackward: IAsyncEnumerable<int64 option * ResolvedEvent[]>)
            : Task<int64 * struct (ResolvedEvent*'event voption)[]> = task {
            let versionFromStream, lastBatch = ref None, ref None
            let! tempBackward =
                batchesBackward
                |> TaskSeq.collectSeq (fun batch ->
                    match batch with
                    | None, events -> lastBatch.Value <- Some events; events
                    | Some _ as reportedVersion, events -> versionFromStream.Value <- reportedVersion; lastBatch.Value <- Some events; events
                    |> Array.map (fun e -> struct (e, tryDecode e)))
                |> TaskSeq.takeWhileInclusive (function
                    | x, ValueSome e when isOrigin e ->
                        match lastBatch.Value with
                        | None -> log.Information("SqlEsStop stream={stream} at={eventNumber}", streamName, x.Position)
                        | Some batch ->
                            let used, residual = batch |> partitionPayloadFrom x.Position
                            log.Information("SqlEsStop stream={stream} at={eventNumber} used={used} residual={residual}", streamName, x.Position, used, residual)
                        false
                    | _ -> true) // continue the search
                |> TaskSeq.toArrayAsync
            let eventsForward = Array.Reverse(tempBackward); tempBackward // sic - relatively cheap, in-place reverse of something we own
            let version = match versionFromStream.Value with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, eventsForward }
        let call = loggedReadSlice conn streamName Direction.Backward batchSize
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "stream" streamName
        let startPosition = int64 Position.End
        let direction = Direction.Backward
        let readlog = log |> Log.prop "direction" direction
        let batchesBackward ct: IAsyncEnumerable<int64 option * ResolvedEvent[]> = readBatches readlog retryingLoggingReadSlice maxPermittedBatchReads startPosition ct
        let! t, (version, events) = (batchesBackward >> mergeFromCompactionPointOrStartFromBackwardsStream log) |> Stopwatch.time ct
        log |> logBatchRead direction streamName t (Array.map ValueTuple.fst events) batchSize version
        return version, events }

module UnionEncoderAdapters =

    let (|Bytes|) = function null -> null | (s: string) -> System.Text.Encoding.UTF8.GetBytes s
    let encodedEventOfResolvedEvent (e: StreamMessage): FsCodec.ITimelineEvent<EventBody> =
        let (Bytes data) = e.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
        let (Bytes meta) = e.JsonMetadata
        let ts = e.CreatedUtc |> DateTimeOffset
        let inline len (xs: byte[]) = if xs = null then 0 else xs.Length
        let size = len data + len meta + e.Type.Length
        // TOCONSIDER wire x.CorrelationId, x.CausationId into x.Meta["$correlationId"] and ["$causationId"]
        // https://eventstore.org/docs/server/metadata-and-reserved-names/index.html#event-metadata
        FsCodec.Core.TimelineEvent.Create(int64 e.StreamVersion, e.Type, data, meta, e.MessageId, null, null, ts, size = size)
    let eventDataOfEncodedEvent (x: FsCodec.IEventData<EventBody>) =
        // SQLStreamStore rejects IsNullOrEmpty data value.
        // TODO: Follow up on inconsistency with ES
        let mapData (x: EventBody) = if x.IsEmpty then "{}" else System.Text.Encoding.UTF8.GetString(x.Span)
        let mapMeta (x: EventBody) = if x.IsEmpty then null else System.Text.Encoding.UTF8.GetString(x.Span)
        // TOCONSIDER wire x.CorrelationId, x.CausationId into x.Meta["$correlationId"] and ["$causationId"]
        // https://eventstore.org/docs/server/metadata-and-reserved-names/index.html#event-metadata
        NewStreamMessage(x.EventId, x.EventType, mapData x.Data, mapMeta x.Meta)

type Position = { streamVersion: int64; compactionEventNumber: int64 option; batchCapacityLimit: int option }
type Token = { pos: Position }

module Token =
    let private create compactionEventNumber batchCapacityLimit streamVersion: StreamToken =
        {   value = box { pos = { streamVersion = streamVersion; compactionEventNumber = compactionEventNumber; batchCapacityLimit = batchCapacityLimit } }
            // In this impl, the StreamVersion matches the SqlStreamStore (and EventStore) StreamVersion in being -1-based
            // Version however is the representation that needs to align with ISyncContext.Version
            version = streamVersion + 1L
            // TOCONSIDER Could implement accumulating the size as it's loaded (but should stop counting when it hits the origin event)
            streamBytes = -1 }
    /// No batching / compaction; we only need to retain the StreamVersion
    let ofNonCompacting streamVersion: StreamToken =
        create None None streamVersion
    // headroom before compaction is necessary given the stated knowledge of the last (if known) `compactionEventNumberOption`
    let private batchCapacityLimit compactedEventNumberOption unstoredEventsPending (batchSize: int) (streamVersion: int64): int =
        match compactedEventNumberOption with
        | Some (compactionEventNumber: int64) -> (batchSize - unstoredEventsPending) - int (streamVersion - compactionEventNumber + 1L) |> max 0
        | None -> (batchSize - unstoredEventsPending) - (int streamVersion + 1) - 1 |> max 0
    let (*private*) ofCompactionEventNumber compactedEventNumberOption unstoredEventsPending batchSize streamVersion: StreamToken =
        let batchCapacityLimit = batchCapacityLimit compactedEventNumberOption unstoredEventsPending batchSize streamVersion
        create compactedEventNumberOption (Some batchCapacityLimit) streamVersion
    /// Assume we have not seen any compaction events; use the batchSize and version to infer headroom
    let ofUncompactedVersion batchSize streamVersion: StreamToken =
        ofCompactionEventNumber None 0 batchSize streamVersion
    let (|Unpack|) (x: StreamToken): Position = let t = unbox<Token> x.value in t.pos
    /// Use previousToken plus the data we are adding and the position we are adding it to infer a headroom
    let ofPreviousTokenAndEventsLength (Unpack previousToken) eventsLength batchSize streamVersion: StreamToken =
        let compactedEventNumber = previousToken.compactionEventNumber
        ofCompactionEventNumber compactedEventNumber eventsLength batchSize streamVersion
    /// Use an event just read from the stream to infer headroom
    let ofCompactionResolvedEventAndVersion (compactionEvent: ResolvedEvent) batchSize streamVersion: StreamToken =
        ofCompactionEventNumber (compactionEvent.StreamVersion |> int64 |> Some) 0 batchSize streamVersion
    /// Use an event we are about to write to the stream to infer headroom
    let ofPreviousStreamVersionAndCompactionEventDataIndex (Unpack token) compactionEventDataIndex eventsLength batchSize streamVersion': StreamToken =
        ofCompactionEventNumber (Some (token.streamVersion + 1L + int64 compactionEventDataIndex)) eventsLength batchSize streamVersion'
    let isStale current candidate = current.version > candidate.version

type SqlStreamStoreConnection(readConnection, [<O; D(null)>]?writeConnection, [<O; D(null)>]?readRetryPolicy, [<O; D(null)>]?writeRetryPolicy) =
    member _.ReadConnection = readConnection
    member _.ReadRetryPolicy = readRetryPolicy
    member _.WriteConnection = defaultArg writeConnection readConnection
    member _.WriteRetryPolicy = writeRetryPolicy

type BatchOptions(getBatchSize: Func<int>, [<O; D(null)>]?batchCountLimit) =
    new (batchSize) = BatchOptions(fun () -> batchSize)
    member _.BatchSize = getBatchSize.Invoke()
    member _.MaxBatches = batchCountLimit

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type GatewaySyncResult = Written of StreamToken | ConflictUnknown

type SqlStreamStoreContext(connection: SqlStreamStoreConnection, batchOptions: BatchOptions) =

    let isResolvedEventEventType (tryDecode, predicate) (e: StreamMessage) =
        let data = e.GetJsonData() |> Async.AwaitTaskCorrect |> Async.RunSynchronously
        predicate (tryDecode data)
    let tryIsResolvedEventEventType predicateOption = predicateOption |> Option.map isResolvedEventEventType
    let conn requireLeader = if requireLeader then connection.WriteConnection else connection.ReadConnection

    new (   connection: SqlStreamStoreConnection,
            // Max number of Events to retrieve in a single batch. Also affects frequency of RollingSnapshots. Default: 500.
            [<O; D null>] ?batchSize) =
        SqlStreamStoreContext(connection, BatchOptions(batchSize = defaultArg batchSize 500))
    member val BatchOptions = batchOptions

    member internal _.TokenEmpty = Token.ofUncompactedVersion batchOptions.BatchSize -1L
    member internal _.LoadBatched(log, streamName, requireLeader, tryDecode, isCompactionEventType, ct): Task<StreamToken * 'event[]> = task {
        let! version, events = Read.loadForwardsFrom log connection.ReadRetryPolicy (conn requireLeader) batchOptions.BatchSize batchOptions.MaxBatches streamName 0L ct
        match tryIsResolvedEventEventType isCompactionEventType with
        | None -> return Token.ofNonCompacting version, Array.chooseV tryDecode events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack isCompactionEvent with
            | None -> return Token.ofUncompactedVersion batchOptions.BatchSize version, Array.chooseV tryDecode events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batchOptions.BatchSize version, Array.chooseV tryDecode events }
    member internal _.LoadBackwardsStoppingAtCompactionEvent(log, streamName, requireLeader, tryDecode, isOrigin, ct): Task<StreamToken * 'event []> = task {
        let! version, events =
            Read.loadBackwardsUntilCompactionOrStart log connection.ReadRetryPolicy (conn requireLeader) batchOptions.BatchSize batchOptions.MaxBatches streamName (tryDecode, isOrigin) ct
        match Array.tryHead events |> Option.filter (function _, ValueSome e -> isOrigin e | _ -> false) with
        | None -> return Token.ofUncompactedVersion batchOptions.BatchSize version, Array.chooseV ValueTuple.snd events
        | Some (resolvedEvent, _) -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batchOptions.BatchSize version, Array.chooseV ValueTuple.snd events }
    member internal _.Reload(log, streamName, requireLeader, (Token.Unpack token as streamToken), tryDecode, isCompactionEventType, ct)
        : Task<StreamToken * 'event[]> = task {
        let streamPosition = token.streamVersion + 1L
        let! version, events = Read.loadForwardsFrom log connection.ReadRetryPolicy (conn requireLeader) batchOptions.BatchSize batchOptions.MaxBatches streamName streamPosition ct
        match isCompactionEventType with
        | None -> return Token.ofNonCompacting version, Array.chooseV tryDecode events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack (fun re -> match tryDecode re with ValueSome e -> isCompactionEvent e | _ -> false) with
            | None -> return Token.ofPreviousTokenAndEventsLength streamToken events.Length batchOptions.BatchSize version, Array.chooseV tryDecode events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batchOptions.BatchSize version, Array.chooseV tryDecode events }
    member internal _.TrySync(log, streamName, (Token.Unpack pos as streamToken), events, encodedEvents: EventData[], isCompactionEventType, ct): Task<GatewaySyncResult> = task {
        match! Write.writeEvents log connection.WriteRetryPolicy connection.WriteConnection streamName pos.streamVersion encodedEvents ct with
        | EsSyncResult.Written wr ->
            let version' = wr.CurrentVersion |> int64
            let token =
                match isCompactionEventType with
                | None -> Token.ofNonCompacting version'
                | Some isCompactionEvent ->
                    match events |> Array.tryFindIndexBack isCompactionEvent with
                    | None -> Token.ofPreviousTokenAndEventsLength streamToken encodedEvents.Length batchOptions.BatchSize version'
                    | Some compactionEventIndex ->
                        Token.ofPreviousStreamVersionAndCompactionEventDataIndex streamToken compactionEventIndex encodedEvents.Length batchOptions.BatchSize version'
            return GatewaySyncResult.Written token
        | EsSyncResult.ConflictUnknown ->
            return GatewaySyncResult.ConflictUnknown }

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type AccessStrategy<'event, 'state> =
    /// Load only the single most recent event defined in <c>'event</c> and trust that doing a <c>fold</c> from any such event
    /// will yield a correct and complete state
    /// In other words, the <c>fold</c> function should not need to consider either the preceding <c>'state</state> or <c>'event</c>s.
    | LatestKnownEvent
    /// Ensures a snapshot/compaction event from which the state can be reconstituted upon decoding is always present
    /// (embedded in the stream as an event), generated every <c>batchSize</c> events using the supplied <c>toSnapshot</c> function
    /// Scanning for events concludes when any event passes the <c>isOrigin</c> test.
    /// Related: https://eventstore.org/docs/event-sourcing-basics/rolling-snapshots/index.html
    | RollingSnapshots of isOrigin: ('event -> bool) * toSnapshot: ('state -> 'event)

type private CompactionContext(eventsLen: int, capacityBeforeCompaction: int) =
    /// Determines whether writing a Compaction event is warranted (based on the existing state and the current accumulated changes)
    member _.IsCompactionDue = eventsLen > capacityBeforeCompaction

type private Category<'event, 'state, 'context>(context: SqlStreamStoreContext, codec: FsCodec.IEventCodec<_, _, 'context>, fold, initial, access) =
    let tryDecode (e: ResolvedEvent) = e |> UnionEncoderAdapters.encodedEventOfResolvedEvent |> codec.TryDecode
    let isOrigin =
        match access with
        | None | Some AccessStrategy.LatestKnownEvent -> fun _ -> true
        | Some (AccessStrategy.RollingSnapshots (isValid, _)) -> isValid
    let loadAlgorithm log streamName requireLeader ct =
        match access with
        | None -> context.LoadBatched(log, streamName, requireLeader, tryDecode, None, ct)
        | Some AccessStrategy.LatestKnownEvent
        | Some (AccessStrategy.RollingSnapshots _) -> context.LoadBackwardsStoppingAtCompactionEvent(log, streamName, requireLeader, tryDecode, isOrigin, ct)
    let compactionPredicate =
        match access with
        | None -> None
        | Some AccessStrategy.LatestKnownEvent -> Some (fun _ -> true)
        | Some (AccessStrategy.RollingSnapshots (isValid, _)) -> Some isValid
    let fetch state f = task { let! token', events = f in return struct (token', fold state (Seq.ofArray events)) }
    let reload (log, sn, leader, token, state) ct = fetch state (context.Reload(log, sn, leader, token, tryDecode, compactionPredicate, ct))
    interface ICategory<'event, 'state, 'context> with
        member _.Load(log, _categoryName, _streamId, streamName, _maxAge, requireLeader, ct) =
            fetch initial (loadAlgorithm log streamName requireLeader ct)
        member _.TrySync(log, _categoryName, _streamId, streamName, ctx, _maybeInit, (Token.Unpack token as streamToken), state, events, ct) = task {
            let events =
                match access with
                | None | Some AccessStrategy.LatestKnownEvent -> events
                | Some (AccessStrategy.RollingSnapshots (_, compact)) ->
                    let cc = CompactionContext(Array.length events, token.batchCapacityLimit.Value)
                    if cc.IsCompactionDue then Array.append events (fold state events |> compact |> Array.singleton) else events
            let encode e = codec.Encode(ctx, e)
            let encodedEvents: EventData[] = events |> Array.map (encode >> UnionEncoderAdapters.eventDataOfEncodedEvent)
            match! context.TrySync(log, streamName, streamToken, events, encodedEvents, compactionPredicate, ct) with
            | GatewaySyncResult.Written token' ->  return SyncResult.Written  (token', fold state events)
            | GatewaySyncResult.ConflictUnknown -> return SyncResult.Conflict (reload (log, streamName, (*requireLeader*)true, streamToken, state)) }
    interface Caching.IReloadable<'state> with member _.Reload(log, sn, leader, token, state, ct) = reload (log, sn, leader, token, state) ct

type SqlStreamStoreCategory<'event, 'state, 'context> internal (resolveInner, empty) =
    inherit Equinox.Category<'event, 'state, 'context>(resolveInner, empty)
    new(context: SqlStreamStoreContext, codec: FsCodec.IEventCodec<_, _, 'context>, fold, initial,
        // For SqlStreamStore, caching is less critical than it is for e.g. CosmosDB
        // As such, it can often be omitted, particularly if streams are short, or events are small and/or database latency aligns with request latency requirements
        [<O; D(null)>]?caching,
        [<O; D(null)>]?access) =
        do  match access with
            | Some AccessStrategy.LatestKnownEvent when Option.isSome caching ->
                "Equinox.SqlStreamStore does not support (and it would make things _less_ efficient even if it did)"
                + "mixing AccessStrategy.LatestKnownEvent with Caching at present."
                |> invalidOp
            | _ -> ()
        let cat = Category<'event, 'state, 'context>(context, codec, fold, initial, access) |> Caching.apply Token.isStale caching
        let resolveInner categoryName streamId = struct (cat, StreamName.render categoryName streamId, ValueNone)
        let empty = struct (context.TokenEmpty, initial)
        SqlStreamStoreCategory(resolveInner, empty)

[<AbstractClass>]
type ConnectorBase([<O; D(null)>]?readRetryPolicy, [<O; D(null)>]?writeRetryPolicy) =

    abstract member Connect: unit -> Async<IStreamStore>

    member x.Establish(): Async<SqlStreamStoreConnection> = async {
        let! store = x.Connect()
        return SqlStreamStoreConnection(readConnection=store, writeConnection=store, ?readRetryPolicy=readRetryPolicy, ?writeRetryPolicy=writeRetryPolicy) }
