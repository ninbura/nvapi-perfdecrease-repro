// nvapi-perfdecrease-repro v1.1
//
// Samples NvAPI_GPU_GetPerfDecreaseInfo side by side with NVML's clocks-event (throttle) reasons and the graphics clock.
// Purpose: demonstrate NVAPI reporting NV_GPU_PERF_DECREASE_REASON_THERMAL_PROTECTION in short bursts while
// NVML reports no thermal/power slowdown and the graphics clock does not change.
//
// v1.1 adds: the NvAPI return status of every GetPerfDecreaseInfo call, a local wall-clock timestamp per sample
// (for matching against game logs and nvidia-smi), the NVAPI interface version string, and hot-spot and
// memory-junction temperatures.
//
// Read-only: nothing on the GPU is changed. Run it while the GPU is under load (a game or a benchmark);
// on the affected system the bursts do not occur at idle.
//
// usage: nvapi-perfdecrease-repro.exe [seconds=60] [samples.csv] [stop-when-process-exits, e.g. Wow]
//
// NVAPI functions are obtained through nvapi_QueryInterface with the IDs published in NVIDIA's nvapi_interface.h
// (https://github.com/NVIDIA/nvapi). NVML is loaded from C:\Windows\System32\nvml.dll (installed with the driver).
// Hot-spot and memory-junction temperatures use NvAPI_GPU_ThermalGetSensors (0x65FE3AAD), which is not in the public
// header; it is the call that common monitoring tools use for these sensors. If it is unavailable they are reported as n/a.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

unsafe static class Repro
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);

    // ---- NVAPI interface IDs (nvapi_interface.h) ----
    const uint ID_NvAPI_Initialize = 0x0150E828;
    const uint ID_NvAPI_GetInterfaceVersionString = 0x01053FA5;
    const uint ID_NvAPI_EnumPhysicalGPUs = 0xE5AC921F;
    const uint ID_NvAPI_GPU_GetFullName = 0xCEEE8E9F;
    const uint ID_NvAPI_GPU_GetPerfDecreaseInfo = 0x7F7F4600;
    const uint ID_NvAPI_GPU_GetCurrentPstate = 0x927DA4F6;
    const uint ID_NvAPI_GPU_GetThermalSettings = 0xE3640A56;
    const uint ID_NvAPI_GPU_GetAllClockFrequencies = 0xDCB616C3;
    const uint ID_NvAPI_GPU_ThermalGetSensors = 0x65FE3AAD; // not in the public header
    const int SENSOR_HOTSPOT = 1, SENSOR_MEMJUNCTION = 9;

    // NV_GPU_PERF_DECREASE_REASON_* (NVAPI documentation)
    static string PerfFlags(uint f)
    {
        if (f == 0) return "NONE";
        var l = new List<string>();
        if ((f & 0x1) != 0) l.Add("THERMAL_PROTECTION");
        if ((f & 0x2) != 0) l.Add("POWER_CONTROL");
        if ((f & 0x4) != 0) l.Add("AC_BATT");
        if ((f & 0x8) != 0) l.Add("API_TRIGGERED");
        if ((f & 0x10) != 0) l.Add("INSUFFICIENT_POWER");
        if ((f & 0x80000000) != 0) l.Add("UNKNOWN");
        uint rest = f & ~0x8000001Fu; if (rest != 0) l.Add($"0x{rest:X}");
        return string.Join("+", l);
    }

    // nvmlClocksEventReasons* bits (nvml.h). 0x400 is displayed by nvidia-smi as "Reliability".
    static string NvmlReasons(ulong r)
    {
        if (r == 0) return "None";
        var l = new List<string>();
        (ulong bit, string name)[] known = {
            (0x1, "GpuIdle"), (0x2, "ApplicationsClocksSetting"), (0x4, "SwPowerCap"), (0x8, "HwSlowdown"),
            (0x10, "SyncBoost"), (0x20, "SwThermalSlowdown"), (0x40, "HwThermalSlowdown"), (0x80, "HwPowerBrakeSlowdown"),
            (0x100, "DisplayClockSetting"), (0x400, "Reliability(nvidia-smi)") };
        ulong seen = 0;
        foreach (var k in known) if ((r & k.bit) != 0) { l.Add(k.name); seen |= k.bit; }
        if ((r & ~seen) != 0) l.Add($"0x{r & ~seen:X}");
        return string.Join("+", l);
    }
    const ulong NvmlThermalOrPowerSlowdown = 0x4 | 0x8 | 0x20 | 0x40 | 0x80;

    struct Sample
    {
        public double Ms; public int PerfStatus; public uint Perf; public ulong NvmlReasons; public uint NvmlGfx;
        public int NvapiTemp; public uint NvapiGfx; public uint NvmlUtil; public uint NvmlPowerMw; public float HotSpot; public float MemJunction;
    }

    static string MinMax(IEnumerable<float> v) { var l = v.Where(x => !float.IsNaN(x)).ToList(); return l.Count == 0 ? "n/a" : $"{l.Min():F1}-{l.Max():F1} C"; }

    static int Main(string[] args)
    {
        double seconds = args.Length >= 1 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 60;
        string csv = args.Length >= 2 && args[1] != "-" ? args[1] : null;
        string untilExit = args.Length >= 3 ? args[2] : null;

        // ---- NVAPI ----
        var nvapi = LoadLibraryW("nvapi64.dll");
        if (nvapi == IntPtr.Zero) { Console.WriteLine("nvapi64.dll not found"); return 1; }
        var qi = (delegate* unmanaged<uint, IntPtr>)GetProcAddress(nvapi, "nvapi_QueryInterface");
        IntPtr Q(uint id, string name) { var p = qi(id); if (p == IntPtr.Zero) throw new Exception("nvapi_QueryInterface failed for " + name); return p; }
        var NvAPI_Initialize = (delegate* unmanaged<int>)Q(ID_NvAPI_Initialize, "NvAPI_Initialize");
        var NvAPI_GetInterfaceVersionString = (delegate* unmanaged<byte*, int>)Q(ID_NvAPI_GetInterfaceVersionString, "NvAPI_GetInterfaceVersionString");
        var NvAPI_EnumPhysicalGPUs = (delegate* unmanaged<IntPtr*, uint*, int>)Q(ID_NvAPI_EnumPhysicalGPUs, "NvAPI_EnumPhysicalGPUs");
        var NvAPI_GPU_GetFullName = (delegate* unmanaged<IntPtr, byte*, int>)Q(ID_NvAPI_GPU_GetFullName, "NvAPI_GPU_GetFullName");
        var NvAPI_GPU_GetPerfDecreaseInfo = (delegate* unmanaged<IntPtr, uint*, int>)Q(ID_NvAPI_GPU_GetPerfDecreaseInfo, "NvAPI_GPU_GetPerfDecreaseInfo");
        var NvAPI_GPU_GetCurrentPstate = (delegate* unmanaged<IntPtr, int*, int>)Q(ID_NvAPI_GPU_GetCurrentPstate, "NvAPI_GPU_GetCurrentPstate");
        var NvAPI_GPU_GetThermalSettings = (delegate* unmanaged<IntPtr, uint, byte*, int>)Q(ID_NvAPI_GPU_GetThermalSettings, "NvAPI_GPU_GetThermalSettings");
        var NvAPI_GPU_GetAllClockFrequencies = (delegate* unmanaged<IntPtr, byte*, int>)Q(ID_NvAPI_GPU_GetAllClockFrequencies, "NvAPI_GPU_GetAllClockFrequencies");
        var thermalSensorsPtr = qi(ID_NvAPI_GPU_ThermalGetSensors);
        int st = NvAPI_Initialize(); if (st != 0) { Console.WriteLine($"NvAPI_Initialize failed: {st}"); return 1; }
        var verBuf = stackalloc byte[64]; string nvapiVersion = NvAPI_GetInterfaceVersionString(verBuf) == 0 ? Marshal.PtrToStringAnsi((IntPtr)verBuf) : "n/a";
        var handles = stackalloc IntPtr[64]; uint count = 0;
        st = NvAPI_EnumPhysicalGPUs(handles, &count); if (st != 0 || count == 0) { Console.WriteLine($"NvAPI_EnumPhysicalGPUs failed: {st}, count {count}"); return 1; }
        var gpu = handles[0];
        var nameBuf = stackalloc byte[64]; NvAPI_GPU_GetFullName(gpu, nameBuf);
        string gpuName = Marshal.PtrToStringAnsi((IntPtr)nameBuf);

        // NV_GPU_THERMAL_SETTINGS_V2: version, count, sensor[3] {controller, defaultMinTemp, defaultMaxTemp, currentTemp, target}
        var therm = stackalloc byte[68];
        int NvapiTemp() { NativeMemory.Clear(therm, 68); *(uint*)therm = 68u | (2u << 16); return NvAPI_GPU_GetThermalSettings(gpu, 15 /*NVAPI_THERMAL_TARGET_ALL*/, therm) == 0 ? *(int*)(therm + 8 + 12) : int.MinValue; }
        // NV_GPU_CLOCK_FREQUENCIES_V2 (struct version 3): version, ClockType, domain[32] {bIsPresent, frequency kHz}
        var clk = stackalloc byte[264];
        uint NvapiClock(uint type, int domain) { NativeMemory.Clear(clk, 264); *(uint*)clk = 264u | (3u << 16); *(uint*)(clk + 4) = type; if (NvAPI_GPU_GetAllClockFrequencies(gpu, clk) != 0) return 0; byte* e = clk + 8 + 8 * domain; return (*(uint*)e & 1) != 0 ? *(uint*)(e + 4) / 1000 : 0; }

        // Thermal sensors (struct v2, 168 bytes): version, mask, reserved[8], temperatures[32] in 1/256 C
        var sens = stackalloc byte[168]; uint sensMask = 0;
        var ThermalGetSensors = (delegate* unmanaged<IntPtr, byte*, int>)thermalSensorsPtr;
        int ReadSensors(uint mask) { NativeMemory.Clear(sens, 168); *(uint*)sens = 168u | (2u << 16); *(uint*)(sens + 4) = mask; return ThermalGetSensors(gpu, sens); }
        int sensStatus0 = int.MinValue;
        if (thermalSensorsPtr != IntPtr.Zero)
        {
            sensStatus0 = ReadSensors(1u);
            for (int bit = 0; bit < 32; bit++) { if (ReadSensors(1u << bit) == 0) sensMask |= 1u << bit; }
        }
        // Read every supported sensor in one call: the mask must cover contiguous low bits (a mask including an unsupported bit fails).
        uint readMask = 0; while (readMask < 0xFFFFFFFF && (sensMask & (readMask + 1)) != 0) readMask = (readMask << 1) | 1;
        sensMask = readMask;
        bool haveSensors = (sensMask & (1u << SENSOR_HOTSPOT)) != 0;
        bool haveMemJ = (sensMask & (1u << SENSOR_MEMJUNCTION)) != 0;
        string sensInfo = thermalSensorsPtr == IntPtr.Zero ? "n/a (function not exported by this driver)"
            : !haveSensors ? $"n/a (status {sensStatus0}, mask 0x{sensMask:X})"
            : $"hot spot = sensor 1 (mask 0x{sensMask:X}); memory junction {(haveMemJ ? "= sensor 9" : "not exposed by this driver")}";
        float Sensor(int idx) => *(int*)(sens + 40 + 4 * idx) / 256f;

        // ---- NVML ----
        var nvml = LoadLibraryW(@"C:\Windows\System32\nvml.dll");
        if (nvml == IntPtr.Zero) { Console.WriteLine("nvml.dll not found in System32"); return 1; }
        IntPtr N(string name) { var p = GetProcAddress(nvml, name); if (p == IntPtr.Zero) throw new Exception("nvml export missing: " + name); return p; }
        var nvmlInit = (delegate* unmanaged<int>)N("nvmlInit_v2");
        var nvmlGetHandle = (delegate* unmanaged<uint, IntPtr*, int>)N("nvmlDeviceGetHandleByIndex_v2");
        var reasonsPtr = GetProcAddress(nvml, "nvmlDeviceGetCurrentClocksEventReasons");
        if (reasonsPtr == IntPtr.Zero) reasonsPtr = N("nvmlDeviceGetCurrentClocksThrottleReasons");
        var nvmlReasons = (delegate* unmanaged<IntPtr, ulong*, int>)reasonsPtr;
        var nvmlClock = (delegate* unmanaged<IntPtr, int, uint*, int>)N("nvmlDeviceGetClockInfo");
        var nvmlUtil = (delegate* unmanaged<IntPtr, uint*, int>)N("nvmlDeviceGetUtilizationRates");
        var nvmlPower = (delegate* unmanaged<IntPtr, uint*, int>)N("nvmlDeviceGetPowerUsage");
        var nvmlDriver = (delegate* unmanaged<byte*, uint, int>)N("nvmlSystemGetDriverVersion");
        var nvmlVbios = (delegate* unmanaged<IntPtr, byte*, uint, int>)N("nvmlDeviceGetVbiosVersion");
        if ((st = nvmlInit()) != 0) { Console.WriteLine($"nvmlInit_v2 failed: {st}"); return 1; }
        IntPtr dev; if ((st = nvmlGetHandle(0, &dev)) != 0) { Console.WriteLine($"nvmlDeviceGetHandleByIndex_v2 failed: {st}"); return 1; }
        var sb = stackalloc byte[96];
        nvmlDriver(sb, 96); string driver = Marshal.PtrToStringAnsi((IntPtr)sb);
        nvmlVbios(dev, sb, 96); string vbios = Marshal.PtrToStringAnsi((IntPtr)sb);
        string nvapiDll = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "nvapi64.dll")).FileVersion;

        int pstate = -1; NvAPI_GPU_GetCurrentPstate(gpu, &pstate);
        Console.WriteLine("nvapi-perfdecrease-repro v1.1");
        Console.WriteLine($"  time        {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"  GPU         {gpuName}  (physical GPUs: {count})");
        Console.WriteLine($"  driver      {driver}   VBIOS {vbios}");
        Console.WriteLine($"  NVAPI       \"{nvapiVersion}\", nvapi64.dll {nvapiDll} (loaded from the driver; no SDK is linked)");
        Console.WriteLine($"  OS          {Environment.OSVersion.VersionString}");
        Console.WriteLine($"  clocks      base {NvapiClock(1, 0)} MHz, boost {NvapiClock(2, 0)} MHz (NVAPI)   P-state at start: P{pstate}");
        Console.WriteLine($"  sensors     hot spot / memory junction: {sensInfo}");
        if (haveSensors && ReadSensors(sensMask) == 0) Console.WriteLine($"              now: core (sensor 0) {Sensor(0):F1} C, hot spot {Sensor(SENSOR_HOTSPOT):F1} C{(haveMemJ ? $", memory junction {Sensor(SENSOR_MEMJUNCTION):F1} C" : "")}");
        Console.WriteLine($"  sampling    up to {seconds:F0} s{(untilExit != null ? $", or until {untilExit} exits" : "")}. Keep the GPU under load (game/benchmark) during the run.");
        Console.WriteLine();

        // ---- sampling loop ----
        // Every iteration: NVAPI perf-decrease flags (+ return status), NVML clocks-event reasons, NVML graphics clock.
        // Every 100 ms: NVAPI temperature, graphics clock, hot spot, memory junction; NVML utilization + power.
        var samples = new List<Sample>(400_000);
        var u = stackalloc uint[2];
        timeBeginPeriod(1); // 1 ms sleep granularity so ~2-3 ms sampling is possible even when no game raises the timer resolution
        var t0 = DateTime.Now; var sw = Stopwatch.StartNew();
        double nextSlow = 0, nextProcCheck = 1000; int lastTemp = int.MinValue; uint lastNvapiGfx = 0, lastUtil = 0, lastPower = 0; float lastHot = float.NaN, lastMem = float.NaN;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var s = new Sample { Ms = sw.Elapsed.TotalMilliseconds };
            uint f = 0; s.PerfStatus = NvAPI_GPU_GetPerfDecreaseInfo(gpu, &f); s.Perf = f;
            ulong r = 0; nvmlReasons(dev, &r); s.NvmlReasons = r;
            uint g = 0; nvmlClock(dev, 0 /*NVML_CLOCK_GRAPHICS*/, &g); s.NvmlGfx = g;
            if (s.Ms >= nextSlow)
            {
                lastTemp = NvapiTemp(); lastNvapiGfx = NvapiClock(0, 0);
                if (nvmlUtil(dev, u) == 0) lastUtil = u[0];
                uint p = 0; if (nvmlPower(dev, &p) == 0) lastPower = p;
                if (haveSensors && ReadSensors(sensMask) == 0) { lastHot = Sensor(SENSOR_HOTSPOT); if (haveMemJ) lastMem = Sensor(SENSOR_MEMJUNCTION); }
                nextSlow = s.Ms + 100;
            }
            s.NvapiTemp = lastTemp; s.NvapiGfx = lastNvapiGfx; s.NvmlUtil = lastUtil; s.NvmlPowerMw = lastPower; s.HotSpot = lastHot; s.MemJunction = lastMem;
            samples.Add(s);
            if (untilExit != null && s.Ms >= nextProcCheck)
            {
                nextProcCheck = s.Ms + 1000;
                if (Process.GetProcessesByName(untilExit).Length == 0) break;
            }
            Thread.Sleep(1);
        }
        timeEndPeriod(1);

        // ---- analysis ----
        int n = samples.Count; double span = samples[^1].Ms - samples[0].Ms;
        Console.WriteLine($"samples: {n} over {span / 1000:F1} s (mean interval {span / Math.Max(1, n - 1):F2} ms), {t0:HH:mm:ss.fff} to {t0.AddMilliseconds(samples[^1].Ms):HH:mm:ss.fff}");
        Console.WriteLine($"GPU load (NVML): avg {samples.Average(x => x.NvmlUtil):F0} %, power avg {samples.Average(x => x.NvmlPowerMw) / 1000:F0} W, " +
                          $"core temp (NVAPI) {samples.Where(x => x.NvapiTemp != int.MinValue).Min(x => x.NvapiTemp)}-{samples.Where(x => x.NvapiTemp != int.MinValue).Max(x => x.NvapiTemp)} C, " +
                          $"hot spot {MinMax(samples.Select(x => x.HotSpot))}, memory junction {MinMax(samples.Select(x => x.MemJunction))}");
        if (samples.Average(x => x.NvmlUtil) < 20) Console.WriteLine("NOTE: GPU was mostly idle during this run.");
        Console.WriteLine();

        Console.WriteLine("NvAPI_GPU_GetPerfDecreaseInfo return status (share of calls):");
        foreach (var g in samples.GroupBy(x => x.PerfStatus).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,4} {(g.Key == 0 ? "NVAPI_OK" : "error"),-40} {g.Count() * 100.0 / n,6:F2} %  ({g.Count()} calls)");
        Console.WriteLine("NvAPI_GPU_GetPerfDecreaseInfo values (raw bitmask, share of samples):");
        foreach (var g in samples.GroupBy(x => x.Perf).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  0x{g.Key:X8} {PerfFlags(g.Key),-40} {g.Count() * 100.0 / n,6:F2} %");
        Console.WriteLine("NVML clocks-event reasons (share of samples):");
        foreach (var g in samples.GroupBy(x => x.NvmlReasons).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  0x{g.Key:X16} {NvmlReasons(g.Key),-40} {g.Count() * 100.0 / n,6:F2} %");
        Console.WriteLine();

        // bursts of THERMAL_PROTECTION (bit 0)
        var bursts = new List<(int start, int end)>();
        for (int i = 0; i < n; i++)
            if ((samples[i].Perf & 1) != 0 && (i == 0 || (samples[i - 1].Perf & 1) == 0))
            { int j = i; while (j + 1 < n && (samples[j + 1].Perf & 1) != 0) j++; bursts.Add((i, j)); i = j; }
        int thermalSamples = samples.Count(x => (x.Perf & 1) != 0);
        Console.WriteLine($"THERMAL_PROTECTION bursts: {bursts.Count} ({bursts.Count / (span / 1000):F2} per second), set in {thermalSamples * 100.0 / n:F2} % of samples");
        if (bursts.Count > 0)
        {
            var durs = bursts.Select(b => (b.end + 1 < n ? samples[b.end + 1].Ms : samples[b.end].Ms) - samples[b.start].Ms).OrderBy(d => d).ToList();
            Console.WriteLine($"  burst duration (upper bound, ms): min {durs[0]:F1}, median {durs[durs.Count / 2]:F1}, max {durs[^1]:F1}");
            var gaps = bursts.Zip(bursts.Skip(1), (a, b) => samples[b.start].Ms - samples[a.start].Ms).OrderBy(d => d).ToList();
            if (gaps.Count > 0) Console.WriteLine($"  interval between bursts (ms): min {gaps[0]:F0}, median {gaps[gaps.Count / 2]:F0}, max {gaps[^1]:F0}");
            var inBurst = bursts.SelectMany(b => Enumerable.Range(b.start, b.end - b.start + 1)).Select(i => samples[i]).ToList();
            Console.WriteLine($"  return status during bursts: {string.Join(", ", inBurst.GroupBy(x => x.PerfStatus).Select(g => $"{g.Key} x{g.Count()}"))}");
            Console.WriteLine($"  NVML reasons during bursts: {string.Join(", ", inBurst.GroupBy(x => x.NvmlReasons).Select(g => $"{NvmlReasons(g.Key)} x{g.Count()}"))}");
            Console.WriteLine($"  NVML graphics clock during bursts: {inBurst.Min(x => x.NvmlGfx)}-{inBurst.Max(x => x.NvmlGfx)} MHz   (whole run: {samples.Min(x => x.NvmlGfx)}-{samples.Max(x => x.NvmlGfx)} MHz)");
            var tb = inBurst.Where(x => x.NvapiTemp != int.MinValue).ToList();
            if (tb.Count > 0) Console.WriteLine($"  core temperature during bursts: {tb.Min(x => x.NvapiTemp)}-{tb.Max(x => x.NvapiTemp)} C");
            Console.WriteLine($"  hot spot during bursts: {MinMax(inBurst.Select(x => x.HotSpot))}, memory junction during bursts: {MinMax(inBurst.Select(x => x.MemJunction))}");
        }
        int nvmlSlow = samples.Count(x => (x.NvmlReasons & NvmlThermalOrPowerSlowdown) != 0);
        Console.WriteLine($"NVML thermal/power slowdown bits (SwPowerCap|HwSlowdown|SwThermal|HwThermal|HwPowerBrake) set in {nvmlSlow} of {n} samples");
        Console.WriteLine();
        Console.WriteLine(bursts.Count > 0 && nvmlSlow == 0
            ? "RESULT: NVAPI reported THERMAL_PROTECTION while NVML reported no thermal/power slowdown and the graphics clock did not drop."
            : bursts.Count == 0 ? "RESULT: no THERMAL_PROTECTION bursts observed in this run."
            : "RESULT: NVAPI THERMAL_PROTECTION bursts observed; NVML also reported slowdown in some samples (see above).");

        if (csv != null)
        {
            var o = new StringBuilder("local_time,t_ms,nvapi_status,nvapi_perf_decrease,nvml_clocks_event_reasons,nvml_gfx_mhz,nvapi_gfx_mhz,nvapi_temp_c,hotspot_c,memjunction_c,nvml_util_pct,nvml_power_w\n");
            foreach (var x in samples)
                o.Append(FormattableString.Invariant($"{t0.AddMilliseconds(x.Ms):yyyy-MM-dd HH:mm:ss.fff},{x.Ms:F2},{x.PerfStatus},0x{x.Perf:X},0x{x.NvmlReasons:X},{x.NvmlGfx},{x.NvapiGfx},{x.NvapiTemp},{(float.IsNaN(x.HotSpot) ? "" : x.HotSpot.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))},{(float.IsNaN(x.MemJunction) ? "" : x.MemJunction.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))},{x.NvmlUtil},{x.NvmlPowerMw / 1000.0:F1}\n"));
            File.WriteAllText(csv, o.ToString());
            Console.WriteLine($"per-sample CSV written to {csv}");
        }
        return 0;
    }
}
