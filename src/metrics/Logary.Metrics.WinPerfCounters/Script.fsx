#I "bin/Release"
#r "NodaTime.dll"
#r "Hopac.Core.dll"
#r "Hopac.dll"
#r "Logary.dll"
#r "Logary.Metrics.WinPerfCounters.dll"
open Hopac
open Logary
open Logary.Metric
open Logary.Metrics
open Logary.Metrics.WinPerfCounters

[<RequireQualifiedAccess>]
type Scope =
  /// Automatically tracks this perf counter for this process
  | Process
  /// A specific set of instances
  | Set of instances:(unit -> Set<string>)
  /// Find all individual instances for the counter and track them.
  | All
  /// The instance that is the sum of all instances. It's the literal `_Total`.
  | Total

type WPC =
  private {
    values : Stream<Message>
  }

type CreateResult =
  | CategoryDoesNotExist
  | CounterDoesNotExist
  | NoInstancesFound
  | Success of WPC

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module WPC =

  type internal LoopConf =
    { category : Category
      counter : string
      scope : Scope
      units : Units }

  /// Welcome to Gary's Ghastly Grotto!
  module internal Impl =
    open NodaTime
    open System
    open System.Text
    open System.Diagnostics
    open Hopac
    open Hopac.Infixes
    open Logary
    open Logary.Message
    open Logary.Internals

    let processCategory =
      // Assume there exists a Process counter, even on Mono! Otherwise
      // we'll when the assembly's statics are lazily initialised.
      Category.createForce "Process"

    let cleanNameSegment =
      let replacements =
        Map [
          '.', '-'
        ]
      fun (str : string) ->
        let sb = StringBuilder()
        for c in str do
          for KeyValue (from, too) in replacements do
            if c = from then
              sb.Append too |> ignore
            else
              sb.Append c |> ignore
        sb.ToString()

    /// Recurses until successful to find the current process' instance name.
    let rec findProcessInstanceName (ilogger : Logger) =
      // Assume this does not throw.
      let pid = Process.GetCurrentProcess().Id
      try
        let instances =
          // Does not throw
          processCategory.GetInstanceNames()
          // Assume c'tor does not throw
          |> Array.map (fun iname -> new PerformanceCounter("Process", "ID Process", iname, true), iname)
          // .NextValue() may throw InvalidOp... if process has exited, caught below
          |> Array.filter (fun (wpc, iname) -> try int (wpc.NextValue()) = pid with _ -> false)

        match instances with
        | [||] ->
          // Process was renumbered while executing; recurse to try again
          findProcessInstanceName ilogger
        | [| _, iname |] ->
          // Successful; only a single pid should equal a single instance name
          iname
        | more ->
          // Multiple results?! Mind: Boom! Process was renumbered while querying for
          // the individual pids. Recurse to try again.
          findProcessInstanceName ilogger

      with
      | :? InvalidOperationException as e ->
        // Oh, well, we tried...
        ilogger.debug (
          eventX "Got InvalidOperationException while querying Windows Performance Counters, retrying."
          >> addExn e)
        // Up and at 'em!
        findProcessInstanceName ilogger

    /// Never throws (based on assumptions about APIs being called), eventually returns.
    ///  1: This process
    ///  2: All (will contain .Total too)/Set
    ///  3: Total
    let rec build (ilogger : Logger) (category : Category, counter) =
      let create (iname : string) =
        // Assume c'tor never throws.
        new PerformanceCounter(category.CategoryName, counter, iname, true)

      function
      | Scope.Process ->
        // Recurses until successful, thus never throws
        let iname = findProcessInstanceName ilogger
        Choice1Of3 (create iname)

      | Scope.All ->
        // Assume never throws.
        Choice2Of3 (category.GetInstanceNames() |> Array.map create)

      | Scope.Set inamesFactory as set ->
        // Assume inamesFactory never throws (it's up to the programmer/caller to make sure)
        inamesFactory ()
        |> Seq.map create
        |> Array.ofSeq
        |> Choice2Of3

      | Scope.Total ->
        // Assume there is always a _Total counter (???)
        Choice3Of3 (create "_Total")

    /// Create a point name that's the category and counter.
    let createPointName (category : Category) counter =
      PointName [|
        cleanNameSegment category.CategoryName
        cleanNameSegment counter
      |]

    let loop (conf : LoopConf)
             (ri : RuntimeInfo)
             (queryCh : Ch<_>) // will re-query directly after replying with a value
             (shutdownCh : Ch<IVar<unit>>)
             (lastWill : obj -> Job<unit>)
             (will : obj option)
             : Job<unit> =

      let baseName = createPointName conf.category conf.counter
      // TODO: ensure callers/instantiators of this loop add the process name to the Message

      // Properly name the internal log messages from this Metric
      let ilogger =
        ri.logger
        |> Logger.apply (setName (PointName.parse "Logary.Metrics.WinPerfCounters.WPC.loop"))

      /// In the `init` state, the counter depending on its Scope.
      let rec init () =
        ilogger.verbose (eventX "Initialising {wpc}." >> setFieldFromObject "wpc" conf)
        match build ilogger (conf.category, conf.counter) conf.scope with
        | Choice1Of3 single ->
          acquireSingle single
        | Choice2Of3 many ->
          acquireMany many
        | Choice3Of3 total ->
          acquireTotal total

      // In the `acquireSingle` state, the counter is polled and placed as the main
      // point value.
      and acquireSingle (pc : PerformanceCounter) =
        try
          let value = pc.NextValue()
          let message = gaugeWithUnit baseName conf.units (Float (float value))
          // Be ready to hand off the message
          respond message
        with e ->
          ilogger.debug (
            eventX "Unhandled exception while calling pc.NextValue() in acquireSingle"
            >> addExn e)
          // Reinitialise for the current process
          init ()

      // In the `acquireTotal` state, the counter is polled and placed as the main
      // point value, but suffixed ".Total".
      and acquireTotal (pc : PerformanceCounter) =
        try
          let value = pc.NextValue()
          let name = baseName |> PointName.setEnding "Total"
          let message = gaugeWithUnit name conf.units (Float (float value))
          // Be ready to hand off the message
          respond message
        with e ->
          ilogger.debug (
            eventX "Unhandled exception while calling pc.NextValue() in acquireSingle"
            >> addExn e)
          // Reinitialise for the current process
          init ()

      /// In the `acquireMany` state, the counters are all added as fields with values
      /// to the Message and the PointValue of the Message is set to 1 as well as specified
      /// to be ignored.
      and acquireMany (pc : PerformanceCounter[]) =
        try
          let values = pc |> Array.map (fun pc ->
            ilogger.verbose (
              eventX "Querying {wpc} with {instanceName}."
              >> setFieldFromObject "wpc" conf
              >> setField "instanceName" pc.InstanceName)
            pc.InstanceName,
            Float (float (pc.NextValue())))

          let initial =
            { Message.gaugeWithUnit baseName conf.units (Int64 1L) with
                context =
                  Map [ KnownLiterals.TagsContextName,
                        Array [ String KnownLiterals.SuppressPointValue ] ]
            }

          let setField m (pn, value) =
            let field = Field (value, Some conf.units)
            m |> setFieldValue pn field

          let message = Array.fold setField initial values

          respond message

        with e ->
          ilogger.debug (
            eventX "Unhandled exception while calling Array.map (fun pc -> pc.NextValue())"
            >> addExn e)
          init ()

      and respond (message : Message) =
        Alt.choose [
          queryCh ^=> fun respCh ->
            (respCh *<- message) ^=> init

          shutdownCh ^=> fun ack ->
            ack *<= ()
        ] :> Job<_>

      init ()

  // Create

  let forProcess (category, counter, units) : CreateResult =
    let shutdownCh = Ch ()
    let requestCh = Ch ()
    CategoryDoesNotExist

  let forSet (category, counter, units) queryInstances : CreateResult =
    CategoryDoesNotExist

  let forAll (category, counter, units) : CreateResult =
    CategoryDoesNotExist

  let totalFor (category, counter) : CreateResult =
    CategoryDoesNotExist

  // Convert

  let toMetric (w : WPC) : Job<Metric> =
    (* let reducer state msg = state
    let ticker (state : WinPerfCounterInstance []) =
      state, state |> Array.map Helpers.toValue
    Metric.create reducer counters ticker *)
    Unchecked.defaultof<Job<Metric>>


module Metric =
  let ofWPC (w : WPC) : Job<Metric> =
    WPC.toMetric w
