open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading.Tasks

module Native =
    [<Literal>]
    let RelationProcessorCore = 0

    [<Literal>]
    let ERROR_INSUFFICIENT_BUFFER = 122

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        nativeint buffer,
        uint32& returnedLength
    )

    [<DllImport("kernel32.dll")>]
    extern nativeint GetCurrentThread()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint SetThreadAffinityMask(nativeint hThread, nativeint dwThreadAffinityMask)

module PowerPlan =
    let private runPowerCfg (args: string) =
        try
            let psi = ProcessStartInfo("powercfg", args)
            psi.CreateNoWindow <- true
            psi.UseShellExecute <- false
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true

            use p = Process.Start(psi)
            p.WaitForExit()
            p.ExitCode = 0
        with _ ->
            false

    let setHighPerformance () =
        // Try to switch to High performance and push CPU min/max throttle to 100.
        let steps =
            [| "/S SCHEME_MAX"
               "/SETACVALUEINDEX SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100"
               "/SETACVALUEINDEX SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100"
               "/SETDCVALUEINDEX SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100"
               "/SETDCVALUEINDEX SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100"
               "/S SCHEME_CURRENT" |]

        let results = steps |> Array.map runPowerCfg

        if results |> Array.forall id then
            printfn "[Power] CPU policy configured for benchmarking (high performance)."
        else
            printfn "[Power] Some power settings could not be applied. Try running as Administrator."

type CoreTopology = {
    EfficiencyClass: byte
    LogicalProcessors: int array
}

module CpuTopology =
    let private collectSetBits64 (mask: uint64) =
        [|
            for bit in 0 .. 63 do
                if ((mask >>> bit) &&& 1UL) = 1UL then
                    bit
        |]

    let getCoreTopologies () =
        let mutable bytesRequired = 0u
        let ok = Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, nativeint 0, &bytesRequired)

        if ok then
            failwith "Unexpected success querying logical processor info with empty buffer."

        let err = Marshal.GetLastWin32Error()

        if err <> Native.ERROR_INSUFFICIENT_BUFFER then
            failwithf "GetLogicalProcessorInformationEx failed. Win32Error=%d" err

        let buffer = Marshal.AllocHGlobal(int bytesRequired)

        try
            let ok2 = Native.GetLogicalProcessorInformationEx(Native.RelationProcessorCore, buffer, &bytesRequired)

            if not ok2 then
                let err2 = Marshal.GetLastWin32Error()
                failwithf "GetLogicalProcessorInformationEx failed. Win32Error=%d" err2

            let tops = ResizeArray<CoreTopology>()
            let mutable offset = 0

            while offset < int bytesRequired do
                let rel = Marshal.ReadInt32(buffer, offset)
                let size = Marshal.ReadInt32(buffer, offset + 4)

                if rel = Native.RelationProcessorCore then
                    // Layout for PROCESSOR_RELATIONSHIP inside SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX.
                    let efficiency = byte (Marshal.ReadByte(buffer, offset + 9))
                    let groupCount = int (uint16 (Marshal.ReadInt16(buffer, offset + 30)))

                    let logical =
                        [|
                            for g in 0 .. groupCount - 1 do
                                let baseOff = offset + 32 + (g * 16)
                                let mask = uint64 (Marshal.ReadInt64(buffer, baseOff))
                                let group = int (uint16 (Marshal.ReadInt16(buffer, baseOff + 8)))

                                if group = 0 then
                                    yield! collectSetBits64 mask
                        |]

                    tops.Add({ EfficiencyClass = efficiency; LogicalProcessors = logical })

                offset <- offset + size

            tops.ToArray()
        finally
            Marshal.FreeHGlobal(buffer)

    let classifyPE () =
        let tops = getCoreTopologies ()
        let byClass = Dictionary<byte, ResizeArray<int>>()

        for t in tops do
            if not (byClass.ContainsKey(t.EfficiencyClass)) then
                byClass[t.EfficiencyClass] <- ResizeArray<int>()

            for lp in t.LogicalProcessors do
                byClass[t.EfficiencyClass].Add(lp)

        let classes = byClass.Keys |> Seq.sort |> Seq.toArray

        if classes.Length < 2 then
            printfn "[CPU] Hybrid P/E topology not detected (only one efficiency class found)."
            [||], [||]
        else
            let pClass = classes[0]
            let eClass = classes[classes.Length - 1]

            let pCores = byClass[pClass] |> Seq.distinct |> Seq.sort |> Seq.toArray
            let eCores = byClass[eClass] |> Seq.distinct |> Seq.sort |> Seq.toArray
            pCores, eCores

module Affinity =
    let tryBindToLogicalProcessor (logicalProcessor: int) =
        if logicalProcessor < 0 || logicalProcessor > 63 then
            false
        else
            let mask = 1UL <<< logicalProcessor
            let thread = Native.GetCurrentThread()
            let prev = Native.SetThreadAffinityMask(thread, nativeint (int64 mask))
            prev <> nativeint 0

module MonteCarlo =
    let runWorker (samples: int64) (logicalProcessor: int) (seed: int) =
        Task.Factory.StartNew(
            (fun () ->
                let _ = Affinity.tryBindToLogicalProcessor logicalProcessor
                let rng = Random(seed)
                let mutable inside = 0L
                let mutable i = 0L

                while i < samples do
                    let x = rng.NextDouble()
                    let y = rng.NextDouble()

                    if (x * x + y * y) <= 1.0 then
                        inside <- inside + 1L

                    i <- i + 1L

                inside),
            TaskCreationOptions.LongRunning
        )

type BenchmarkResult = {
    Name: string
    Workers: int
    Samples: int64
    Pi: float
    Elapsed: TimeSpan
    ThroughputM: float
}

let splitSamples (totalSamples: int64) (workers: int) =
    let baseN = totalSamples / int64 workers
    let extra = totalSamples % int64 workers

    [|
        for i in 0 .. workers - 1 ->
            baseN + (if int64 i < extra then 1L else 0L)
    |]

let runBenchmark (name: string) (logicalProcessors: int array) (totalSamples: int64) =
    if logicalProcessors.Length = 0 then
        None
    else
        let chunks = splitSamples totalSamples logicalProcessors.Length
        let sw = Stopwatch.StartNew()

        let tasks =
            logicalProcessors
            |> Array.mapi (fun i lp ->
                let seed = 1337 + (i * 7919)
                MonteCarlo.runWorker chunks[i] lp seed)

        Task.WaitAll(tasks)
        sw.Stop()

        let inside = tasks |> Array.sumBy (fun t -> t.Result)
        let pi = 4.0 * float inside / float totalSamples
        let throughputM = float totalSamples / sw.Elapsed.TotalSeconds / 1_000_000.0

        Some {
            Name = name
            Workers = logicalProcessors.Length
            Samples = totalSamples
            Pi = pi
            Elapsed = sw.Elapsed
            ThroughputM = throughputM
        }

let parseArgInt64 (args: string array) (name: string) (defaultValue: int64) =
    let idx = args |> Array.tryFindIndex ((=) name)

    match idx with
    | Some i when i + 1 < args.Length ->
        match Int64.TryParse(args[i + 1]) with
        | true, v when v > 0L -> v
        | _ -> defaultValue
    | _ -> defaultValue

let printResult (r: BenchmarkResult) =
    printfn "% -16s %3d workers | samples=%11d | pi=%1.10f | time=%8.3fs | throughput=%8.2f M/s" r.Name r.Workers r.Samples r.Pi r.Elapsed.TotalSeconds r.ThroughputM

[<EntryPoint>]
let main argv =
    let samples = parseArgInt64 argv "--samples" 50_000_000L

    printfn "CPU P-core / E-core Monte Carlo Benchmark"
    printfn "Samples per benchmark: %d" samples
    printfn ""

    PowerPlan.setHighPerformance ()

    let pCores, eCores = CpuTopology.classifyPE ()
    let mixed = Array.append pCores eCores |> Array.distinct |> Array.sort

    printfn "[CPU] P logical processors: %A" pCores
    printfn "[CPU] E logical processors: %A" eCores
    printfn "[CPU] Mixed logical processors: %A" mixed
    printfn ""

    let cases =
        [| "P cores parallel", pCores
           "E cores parallel", eCores
           "P/E mixed parallel", mixed
           "Single P core", (if pCores.Length > 0 then [| pCores[0] |] else [||])
           "Single E core", (if eCores.Length > 0 then [| eCores[0] |] else [||]) |]

    printfn "Results"
    printfn "-------"

    let mutable ranAny = false

    for name, lps in cases do
        match runBenchmark name lps samples with
        | Some r ->
            ranAny <- true
            printResult r
        | None ->
            printfn "% -16s skipped (no available logical processors)" name

    if not ranAny then
        printfn "No benchmark case could run. This machine may not expose hybrid P/E core topology."

    0

