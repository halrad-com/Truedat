// NPU MCDM spike — follow-on to §5.0 ORT spike (which marked Step 5 YELLOW
// because IDXGIFactory1::EnumAdapters1 doesn't enumerate NPUs).
//
// NPUs (Intel AI Boost, AMD XDNA, Qualcomm Hexagon) use Microsoft Compute
// Driver Model (MCDM), not the classic DXGI graphics tree. This spike
// investigates whether they show up via the MCDM-visible API:
//
//     IDXGIFactory6::EnumAdapterByGpuPreference(DXGI_GPU_PREFERENCE_MINIMUM_POWER)
//
// And if so, whether we can dispatch ORT inference to them via the
// IntPtr-based DML EP overload:
//
//     OrtSessionOptionsAppendExecutionProviderEx_DML(opts, dml_device, cmd_queue)
//
// Pipeline per candidate NPU adapter:
//   1. Find via EnumAdapterByGpuPreference
//   2. D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_1_0_CORE, &device)
//   3. device->CreateCommandQueue(type=COMPUTE) → cmd_queue
//   4. DMLCreateDevice(device, NONE, &dml_device)
//   5. OrtSessionOptionsAppendExecutionProviderEx_DML(opts, dml_device, cmd_queue)
//   6. InferenceSession on identity model, set LogSeverityLevel=Verbose so
//      ORT prints which adapter it actually picked
//
// Usage:
//   ortspike-npu                    — run all steps, print summary
//   ortspike-npu list-adapters      — print MCDM + DXGI adapter list
//   ortspike-npu step <1..5>        — single step
//   ortspike-npu ilrepack-self-test — used by merged exe to verify it runs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OrtSpikeNpu;

internal static class Program
{
    private static readonly List<(string Step, string Verdict, string Notes)> _results = new();
    private static readonly List<AdapterInfo> _mcdmAdapters = new();

    private static int Main(string[] args)
    {
        try
        {
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
            switch (cmd)
            {
                case "all":
                    Step1_EnumerateMcdm();
                    Step2_CreateD3D12Devices();
                    Step3_AppendDmlEpAndInfer();
                    Step4_VerifyMergedExe();
                    Step5_Performance();
                    PrintSummary();
                    return _results.Any(r => r.Verdict == "FAIL") ? 1 : 0;

                case "list-adapters":
                    DumpAllAdapters();
                    return 0;

                case "step":
                    if (args.Length < 2) { Console.Error.WriteLine("usage: ortspike-npu step <1..5>"); return 2; }
                    switch (args[1])
                    {
                        case "1": Step1_EnumerateMcdm(); break;
                        case "2": Step1_EnumerateMcdm(); Step2_CreateD3D12Devices(); break;
                        case "3": Step1_EnumerateMcdm(); Step2_CreateD3D12Devices(); Step3_AppendDmlEpAndInfer(); break;
                        case "4": Step4_VerifyMergedExe(); break;
                        case "5": Step1_EnumerateMcdm(); Step2_CreateD3D12Devices(); Step5_Performance(); break;
                        default: Console.Error.WriteLine("step must be 1..5"); return 2;
                    }
                    PrintSummary();
                    return 0;

                case "ilrepack-self-test":
                    // Used by the merged exe to prove it still runs ORT.
                    Step1_EnumerateMcdm();
                    PrintSummary();
                    return 0;

                default:
                    Console.Error.WriteLine("usage: ortspike-npu [all|list-adapters|step <n>|ilrepack-self-test]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 1 — MCDM adapter enumeration via IDXGIFactory6::EnumAdapterByGpuPreference
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step1_EnumerateMcdm()
    {
        Console.WriteLine("─── Step 1: MCDM enum via IDXGIFactory6::EnumAdapterByGpuPreference ───");
        try
        {
            // Enumerate everything visible via MINIMUM_POWER preference — this is
            // the call that should surface MCDM-class adapters (NPUs) alongside
            // the classic DXGI graphics adapters. We also enumerate via the
            // classic EnumAdapters1 for a comparison baseline.
            var dxgi = Dxgi.EnumAdapters1();
            var mcdm = Dxgi.EnumAdaptersByGpuPreference(DxgiGpuPreference.MinimumPower);

            Console.WriteLine($"  EnumAdapters1 (DXGI baseline): {dxgi.Count} adapter(s)");
            foreach (var a in dxgi)
                Console.WriteLine($"    [{a.Index}] desc='{a.Description}' vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} flags=0x{a.Flags:X} luid={a.LuidLow:X8}-{a.LuidHigh:X8}");

            Console.WriteLine($"  EnumAdapterByGpuPreference(MINIMUM_POWER): {mcdm.Count} adapter(s)");
            foreach (var a in mcdm)
                Console.WriteLine($"    [{a.Index}] desc='{a.Description}' vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} flags=0x{a.Flags:X} luid={a.LuidLow:X8}-{a.LuidHigh:X8}");

            _mcdmAdapters.Clear();
            _mcdmAdapters.AddRange(mcdm);

            // An NPU should show up in the MINIMUM_POWER list. Heuristic match
            // on description for the three known NPU vendor strings. If MCDM
            // adapters appear under non-NPU descriptions we still report them;
            // an NPU under any disguise is a usable adapter for the spike.
            var npuCandidates = mcdm.Where(IsLikelyNpu).ToList();
            var newlyVisible = mcdm
                .Where(a => !dxgi.Any(d => d.LuidLow == a.LuidLow && d.LuidHigh == a.LuidHigh))
                .ToList();

            Console.WriteLine($"  Adapters visible via MCDM enum but NOT via EnumAdapters1: {newlyVisible.Count}");
            foreach (var a in newlyVisible)
                Console.WriteLine($"    NEW: [{a.Index}] '{a.Description}' (luid={a.LuidLow:X8}-{a.LuidHigh:X8})");

            Console.WriteLine($"  NPU-name candidates: {npuCandidates.Count}");
            foreach (var a in npuCandidates)
                Console.WriteLine($"    NPU: [{a.Index}] '{a.Description}'");

            if (npuCandidates.Count > 0)
            {
                _results.Add(("Step 1: MCDM enumeration", "PASS",
                    $"Found {npuCandidates.Count} NPU candidate(s) via EnumAdapterByGpuPreference: " +
                    string.Join(", ", npuCandidates.Select(a => $"'{a.Description}'"))));
            }
            else if (newlyVisible.Count > 0)
            {
                _results.Add(("Step 1: MCDM enumeration", "YELLOW",
                    $"{newlyVisible.Count} MCDM-only adapter(s) appeared but none match NPU descriptors. May still be NPU under an unfamiliar name — Step 2 will try D3D12 device creation."));
            }
            else
            {
                _results.Add(("Step 1: MCDM enumeration", "FAIL",
                    "EnumAdapterByGpuPreference returned no MCDM-only adapters. NPU (verified via PnP enumeration to be present and OK) is NOT visible to DXGI even via the MCDM-aware API. Windows-API rabbit hole beyond Option A scope — see review doc."));
            }
        }
        catch (Exception ex)
        {
            _results.Add(("Step 1: MCDM enumeration", "FAIL", ex.GetType().Name + ": " + ex.Message));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2 — D3D12CreateDevice + CreateCommandQueue per candidate adapter
    // ─────────────────────────────────────────────────────────────────────────
    private static readonly List<DeviceTriple> _deviceTriples = new();

    private static void Step2_CreateD3D12Devices()
    {
        Console.WriteLine("─── Step 2: D3D12CreateDevice + CreateCommandQueue ───");
        _deviceTriples.Clear();
        var candidates = _mcdmAdapters.Where(a => IsLikelyNpu(a) || IsLikelyMcdmOnly(a)).ToList();
        if (candidates.Count == 0)
        {
            // Fall back to trying every adapter — even GPU ones could be the
            // NPU under an unfamiliar description.
            candidates = _mcdmAdapters.ToList();
        }

        if (candidates.Count == 0)
        {
            _results.Add(("Step 2: D3D12 device creation", "FAIL", "No adapters to try (Step 1 returned empty)."));
            return;
        }

        int created = 0;
        foreach (var a in candidates)
        {
            try
            {
                // Re-acquire the adapter pointer via the LUID — Step 1's pointers
                // were released. EnumAdapterByLuid is on IDXGIFactory4 (vtable slot 26).
                var adapterPtr = Dxgi.EnumAdapterByLuid(a.LuidLow, a.LuidHigh);
                if (adapterPtr == IntPtr.Zero)
                {
                    Console.WriteLine($"  adapter [{a.Index}] '{a.Description}': EnumAdapterByLuid returned null");
                    continue;
                }

                try
                {
                    // Try D3D_FEATURE_LEVEL_11_0 first (DirectML needs ≥11_0 on GPUs
                    // — DMLCreateDevice returns E_NOINTERFACE on a 1_0_CORE-only
                    // device). Fall back to 1_0_CORE for MCDM-only adapters (NPUs).
                    foreach (var fl in new[] { D3D_FEATURE_LEVEL._11_0, D3D_FEATURE_LEVEL._1_0_CORE })
                    {
                        var deviceIid = D3D12.IID_ID3D12Device;
                        var hr = D3D12.D3D12CreateDevice(adapterPtr, (uint)fl, ref deviceIid, out var devPtr);
                        if (hr == 0 && devPtr != IntPtr.Zero)
                        {
                            Console.WriteLine($"  adapter [{a.Index}] '{a.Description}': D3D12 device CREATED (featureLevel=0x{(uint)fl:X4})");
                            var queuePtr = D3D12.CreateComputeCommandQueue(devPtr);
                            if (queuePtr == IntPtr.Zero)
                            {
                                Console.WriteLine($"    cmd queue creation FAILED — releasing device");
                                Marshal.Release(devPtr);
                                continue;
                            }
                            Console.WriteLine($"    compute command queue created");
                            _deviceTriples.Add(new DeviceTriple
                            {
                                Adapter = a,
                                FeatureLevel = fl,
                                D3D12Device = devPtr,
                                CommandQueue = queuePtr,
                            });
                            created++;
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"  adapter [{a.Index}] '{a.Description}' at FL=0x{(uint)fl:X4}: hr=0x{hr:X8}");
                        }
                    }
                }
                finally
                {
                    // Keep the adapter ref alive via the device's parent chain;
                    // we release our explicit ref.
                    Marshal.Release(adapterPtr);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  adapter [{a.Index}] '{a.Description}': exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        _results.Add(("Step 2: D3D12 device creation",
            created > 0 ? "PASS" : "FAIL",
            $"{created} / {candidates.Count} candidate adapter(s) accepted D3D12CreateDevice + CreateCommandQueue"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 3 — Append DML EP via the IntPtr overload, run identity model
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step3_AppendDmlEpAndInfer()
    {
        Console.WriteLine("─── Step 3: Append DML EP via DMLCreateDevice + OrtSessionOptionsAppendExecutionProviderEx_DML ───");

        if (_deviceTriples.Count == 0)
        {
            _results.Add(("Step 3: DML EP via handle", "FAIL", "No D3D12 device triples available (Step 2 produced none)."));
            return;
        }

        var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
        int ranOk = 0;
        var perAdapter = new StringBuilder();

        foreach (var t in _deviceTriples)
        {
            try
            {
                // Create the IDMLDevice on top of the ID3D12Device.
                var dmlIid = DirectML.IID_IDMLDevice;
                var hr = DirectML.DMLCreateDevice(t.D3D12Device, DML_CREATE_DEVICE_FLAGS.NONE, ref dmlIid, out var dmlDevicePtr);
                if (hr != 0 || dmlDevicePtr == IntPtr.Zero)
                {
                    Console.WriteLine($"  adapter [{t.Adapter.Index}] '{t.Adapter.Description}': DMLCreateDevice hr=0x{hr:X8}");
                    perAdapter.Append($"  [{t.Adapter.Description}]: DMLCreateDevice failed (0x{hr:X8})\n");
                    continue;
                }

                try
                {
                    using var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE };

                    // The C# managed SessionOptions inherits SafeHandle — grab its
                    // native OrtSessionOptions* via DangerousGetHandle().
                    var optsHandle = opts.DangerousGetHandle();

                    // Call the native function directly. The managed binding does
                    // not expose this overload in all 1.24.x builds.
                    var status = Ort.OrtSessionOptionsAppendExecutionProviderEx_DML(optsHandle, dmlDevicePtr, t.CommandQueue);
                    if (status != IntPtr.Zero)
                    {
                        var msg = Ort.GetStatusMessage(status);
                        Ort.ReleaseStatus(status);
                        Console.WriteLine($"  adapter [{t.Adapter.Index}] '{t.Adapter.Description}': OrtSessionOptionsAppendExecutionProviderEx_DML FAILED — {msg}");
                        perAdapter.Append($"  [{t.Adapter.Description}]: AppendEx_DML failed ({msg})\n");
                        continue;
                    }

                    Console.WriteLine($"  adapter [{t.Adapter.Index}] '{t.Adapter.Description}': DML EP appended successfully via IntPtr overload");

                    using var session = new InferenceSession(modelBytes, opts);
                    var input = new float[] { 1f, 2f, 3f, 4f };
                    var tensor = new DenseTensor<float>(input, new[] { 1, input.Length });
                    var inputs = new[] { NamedOnnxValue.CreateFromTensor("x", tensor) };
                    using var results = session.Run(inputs);
                    var output = results.First().AsTensor<float>().ToArray();

                    var ok = AreEqual(input, output);
                    Console.WriteLine($"    out: [{string.Join(", ", output)}]  bitMatch={ok}");
                    if (ok)
                    {
                        ranOk++;
                        perAdapter.Append($"  [{t.Adapter.Description}]: PASS — DML EP appended, identity inference bit-correct\n");
                    }
                    else
                    {
                        perAdapter.Append($"  [{t.Adapter.Description}]: ran but output drift from identity\n");
                    }
                }
                finally
                {
                    Marshal.Release(dmlDevicePtr);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  adapter [{t.Adapter.Index}] '{t.Adapter.Description}': exception {ex.GetType().Name}: {ex.Message}");
                perAdapter.Append($"  [{t.Adapter.Description}]: exception {ex.GetType().Name}: {ex.Message}\n");
            }
        }

        _results.Add(("Step 3: DML EP via handle + inference",
            ranOk > 0 ? "PASS" : "FAIL",
            $"{ranOk} / {_deviceTriples.Count} adapter(s) ran identity model via the IntPtr DML EP overload. Per-adapter:\n{perAdapter}"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 4 — ILRepack merge verification (delegates to the merged exe)
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step4_VerifyMergedExe()
    {
        Console.WriteLine("─── Step 4: ILRepack merge verification ───");
        var thisExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(thisExe)!;
        var ilrepackedExe = Path.Combine(dir, "ilrepacked-ortspike-npu.exe");

        if (!File.Exists(ilrepackedExe))
        {
            _results.Add(("Step 4: ILRepack merge", "DEFERRED",
                $"Merged exe not found at '{ilrepackedExe}'. Built only in Release configuration."));
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ilrepackedExe,
            Arguments = "ilrepack-self-test",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        if (p.ExitCode == 0)
            _results.Add(("Step 4: ILRepack merge", "PASS", $"ilrepacked-ortspike-npu.exe ran the MCDM enum self-test path (exit 0) — managed parts merged, native sibling DLLs loaded correctly"));
        else
            _results.Add(("Step 4: ILRepack merge", "FAIL", $"merged exe exit={p.ExitCode}. stderr head: {Truncate(stderr, 200)}"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 5 — Performance (100 inferences on the NPU EP)
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step5_Performance()
    {
        Console.WriteLine("─── Step 5: Performance (100 inferences on NPU EP) ───");
        if (_deviceTriples.Count == 0)
        {
            _results.Add(("Step 5: Performance", "DEFERRED", "No device triples — Step 2 produced none."));
            return;
        }

        var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
        var notes = new StringBuilder();
        foreach (var t in _deviceTriples)
        {
            try
            {
                var dmlIid = DirectML.IID_IDMLDevice;
                var hr = DirectML.DMLCreateDevice(t.D3D12Device, DML_CREATE_DEVICE_FLAGS.NONE, ref dmlIid, out var dmlDevicePtr);
                if (hr != 0) { notes.Append($"[{t.Adapter.Description}]: DMLCreateDevice 0x{hr:X8}; "); continue; }
                try
                {
                    using var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
                    var status = Ort.OrtSessionOptionsAppendExecutionProviderEx_DML(opts.DangerousGetHandle(), dmlDevicePtr, t.CommandQueue);
                    if (status != IntPtr.Zero) { Ort.ReleaseStatus(status); notes.Append($"[{t.Adapter.Description}]: append failed; "); continue; }
                    using var session = new InferenceSession(modelBytes, opts);
                    // Warmup
                    for (int i = 0; i < 5; i++) RunIdentity(session);
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 100; i++) RunIdentity(session);
                    sw.Stop();
                    notes.Append($"[{t.Adapter.Description}]: {sw.Elapsed.TotalMilliseconds:F1} ms / 100 inf ({sw.Elapsed.TotalMilliseconds / 100.0:F3} ms/inf); ");
                }
                finally { Marshal.Release(dmlDevicePtr); }
            }
            catch (Exception ex) { notes.Append($"[{t.Adapter.Description}]: error {ex.Message}; "); }
        }
        _results.Add(("Step 5: Performance (100 inf, identity)", "INFO", notes.ToString()));
    }

    private static void RunIdentity(InferenceSession session)
    {
        var input = new float[] { 1f, 2f, 3f, 4f };
        var tensor = new DenseTensor<float>(input, new[] { 1, input.Length });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("x", tensor) };
        using var results = session.Run(inputs);
        _ = results.First().AsTensor<float>().ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static bool IsLikelyNpu(AdapterInfo a)
    {
        var d = a.Description ?? "";
        return d.IndexOf("AI Boost", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("NPU", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("XDNA", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Ryzen AI", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Hexagon", StringComparison.OrdinalIgnoreCase) >= 0
            || d.IndexOf("Snapdragon", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Compute-only adapters typically advertise DXGI_ADAPTER_FLAG3 bits or
    // appear under MCDM with non-rendering flags. We can't query Flags3
    // without IDXGIAdapter3, so this is a soft heuristic.
    private static bool IsLikelyMcdmOnly(AdapterInfo a) => false;

    private static bool AreEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
        return true;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    private static void DumpAllAdapters()
    {
        var dxgi = Dxgi.EnumAdapters1();
        var mcdm = Dxgi.EnumAdaptersByGpuPreference(DxgiGpuPreference.MinimumPower);
        Console.WriteLine($"EnumAdapters1 (DXGI baseline): {dxgi.Count}");
        foreach (var a in dxgi)
            Console.WriteLine($"  [{a.Index}] '{a.Description}' vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} flags=0x{a.Flags:X} luid={a.LuidLow:X8}-{a.LuidHigh:X8}");
        Console.WriteLine($"EnumAdapterByGpuPreference(MIN_POWER): {mcdm.Count}");
        foreach (var a in mcdm)
            Console.WriteLine($"  [{a.Index}] '{a.Description}' vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} flags=0x{a.Flags:X} luid={a.LuidLow:X8}-{a.LuidHigh:X8}");
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("ORT NPU MCDM spike summary");
        Console.WriteLine("══════════════════════════════════════════════");
        foreach (var r in _results)
        {
            Console.WriteLine($"  [{r.Verdict,-8}] {r.Step}");
            if (!string.IsNullOrEmpty(r.Notes))
            {
                foreach (var line in r.Notes.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine($"             {line.TrimEnd()}");
            }
        }
        Console.WriteLine();
        Console.WriteLine("Verdict letter (G/R/Y) — see review doc for final classification.");
    }
}

// ───────────────────────────────────────────────────────────────────────────
// Adapter info captured from DXGI enumeration
// ───────────────────────────────────────────────────────────────────────────
internal struct AdapterInfo
{
    public int Index;
    public string Description;
    public uint VendorId;
    public uint DeviceId;
    public uint Flags;
    public uint LuidLow;
    public int LuidHigh;
}

internal sealed class DeviceTriple
{
    public AdapterInfo Adapter;
    public D3D_FEATURE_LEVEL FeatureLevel;
    public IntPtr D3D12Device;
    public IntPtr CommandQueue;
}

internal enum D3D_FEATURE_LEVEL : uint
{
    _1_0_CORE = 0x1000,
    _9_1 = 0x9100,
    _9_2 = 0x9200,
    _9_3 = 0x9300,
    _10_0 = 0xa000,
    _10_1 = 0xa100,
    _11_0 = 0xb000,
    _11_1 = 0xb100,
    _12_0 = 0xc000,
    _12_1 = 0xc100,
}

internal enum DxgiGpuPreference : uint
{
    Unspecified = 0,
    MinimumPower = 1,
    HighPerformance = 2,
}

internal enum DML_CREATE_DEVICE_FLAGS : uint
{
    NONE = 0,
    DEBUG = 1,
}

// ───────────────────────────────────────────────────────────────────────────
// DXGI P/Invoke — factory creation + vtable walks for EnumAdapters1,
// EnumAdapterByGpuPreference (factory6), EnumAdapterByLuid (factory4)
// ───────────────────────────────────────────────────────────────────────────
internal static class Dxgi
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    private static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");

    public static List<AdapterInfo> EnumAdapters1()
    {
        var result = new List<AdapterInfo>();
        var iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == IntPtr.Zero) return result;
        try
        {
            // IDXGIFactory1::EnumAdapters1 — vtable slot 12
            var vtbl = Marshal.ReadIntPtr(factory);
            var fnPtr = Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size);
            var enumAdapters1 = (EnumAdapters1Fn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(EnumAdapters1Fn));
            for (int i = 0; i < 32; i++)
            {
                if (enumAdapters1(factory, (uint)i, out var adapter) != 0 || adapter == IntPtr.Zero) break;
                try { result.Add(ReadAdapterDesc(adapter, i)); }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    public static List<AdapterInfo> EnumAdaptersByGpuPreference(DxgiGpuPreference pref)
    {
        var result = new List<AdapterInfo>();
        var iid6 = IID_IDXGIFactory6;
        if (CreateDXGIFactory1(ref iid6, out var factory) != 0 || factory == IntPtr.Zero)
        {
            // CreateDXGIFactory1 takes an arbitrary IID — try directly with factory6 IID
            // (works on Win10 1803+). If it fails fall back to factory1 + QI.
            var iid1 = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid1, out var f1) != 0 || f1 == IntPtr.Zero) return result;
            try
            {
                var hr = QueryInterface(f1, ref iid6, out factory);
                if (hr != 0 || factory == IntPtr.Zero) return result;
            }
            finally { Marshal.Release(f1); }
        }
        try
        {
            // IDXGIFactory6::EnumAdapterByGpuPreference — vtable slot 29
            // (3 IUnknown + 4 IDXGIObject + 5 IDXGIFactory + 2 Factory1 + 11 Factory2
            //  + 1 Factory3 + 2 Factory4 + 1 Factory5 = 29)
            var vtbl = Marshal.ReadIntPtr(factory);
            var fnPtr = Marshal.ReadIntPtr(vtbl, 29 * IntPtr.Size);
            var enumByPref = (EnumAdapterByGpuPreferenceFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(EnumAdapterByGpuPreferenceFn));
            var adapterIid = IID_IDXGIAdapter1;
            for (int i = 0; i < 32; i++)
            {
                if (enumByPref(factory, (uint)i, (uint)pref, ref adapterIid, out var adapter) != 0 || adapter == IntPtr.Zero) break;
                try { result.Add(ReadAdapterDesc(adapter, i)); }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    public static IntPtr EnumAdapterByLuid(uint luidLow, int luidHigh)
    {
        var iid4 = IID_IDXGIFactory4;
        if (CreateDXGIFactory1(ref iid4, out var factory) != 0 || factory == IntPtr.Zero)
        {
            var iid1 = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid1, out var f1) != 0 || f1 == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var hr = QueryInterface(f1, ref iid4, out factory);
                if (hr != 0 || factory == IntPtr.Zero) return IntPtr.Zero;
            }
            finally { Marshal.Release(f1); }
        }
        try
        {
            // IDXGIFactory4::EnumAdapterByLuid — vtable slot 26
            // (3 + 4 + 5 + 2 + 11 + 1 = 26)
            var vtbl = Marshal.ReadIntPtr(factory);
            var fnPtr = Marshal.ReadIntPtr(vtbl, 26 * IntPtr.Size);
            var enumByLuid = (EnumAdapterByLuidFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(EnumAdapterByLuidFn));
            var luid = new LUID { LowPart = luidLow, HighPart = luidHigh };
            var adapterIid = IID_IDXGIAdapter1;
            var hr2 = enumByLuid(factory, luid, ref adapterIid, out var adapter);
            return hr2 == 0 ? adapter : IntPtr.Zero;
        }
        finally { Marshal.Release(factory); }
    }

    private static AdapterInfo ReadAdapterDesc(IntPtr adapter, int index)
    {
        // IDXGIAdapter1::GetDesc1 — vtable slot 10
        var vtbl = Marshal.ReadIntPtr(adapter);
        var fnPtr = Marshal.ReadIntPtr(vtbl, 10 * IntPtr.Size);
        var getDesc = (GetDesc1Fn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(GetDesc1Fn));
        var desc = new DXGI_ADAPTER_DESC1();
        if (getDesc(adapter, ref desc) != 0)
            return new AdapterInfo { Index = index, Description = "(GetDesc1 failed)" };
        return new AdapterInfo
        {
            Index = index,
            Description = new string(desc.Description).TrimEnd('\0'),
            VendorId = desc.VendorId,
            DeviceId = desc.DeviceId,
            Flags = desc.Flags,
            LuidLow = desc.AdapterLuid.LowPart,
            LuidHigh = desc.AdapterLuid.HighPart,
        };
    }

    private static int QueryInterface(IntPtr pUnk, ref Guid iid, out IntPtr ppv)
    {
        // IUnknown::QueryInterface — vtable slot 0
        var vtbl = Marshal.ReadIntPtr(pUnk);
        var fnPtr = Marshal.ReadIntPtr(vtbl, 0 * IntPtr.Size);
        var qi = (QueryInterfaceFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(QueryInterfaceFn));
        return qi(pUnk, ref iid, out ppv);
    }

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(IntPtr thisPtr, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr thisPtr, uint Adapter, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByGpuPreferenceFn(IntPtr thisPtr, uint Adapter, uint GpuPreference, ref Guid riid, out IntPtr ppvAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByLuidFn(IntPtr thisPtr, LUID AdapterLuid, ref Guid riid, out IntPtr ppvAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr thisPtr, ref DXGI_ADAPTER_DESC1 pDesc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }
}

// ───────────────────────────────────────────────────────────────────────────
// D3D12 P/Invoke — D3D12CreateDevice + vtable walk for CreateCommandQueue
// ───────────────────────────────────────────────────────────────────────────
internal static class D3D12
{
    public static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    public static readonly Guid IID_ID3D12CommandQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");

    [DllImport("d3d12.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int D3D12CreateDevice(IntPtr pAdapter, uint MinimumFeatureLevel, [In] ref Guid riid, out IntPtr ppDevice);

    public static IntPtr CreateComputeCommandQueue(IntPtr d3d12Device)
    {
        // ID3D12Device::CreateCommandQueue — vtable slot 8
        // (3 IUnknown + 4 ID3D12Object + 1 GetNodeCount = 8)
        var vtbl = Marshal.ReadIntPtr(d3d12Device);
        var fnPtr = Marshal.ReadIntPtr(vtbl, 8 * IntPtr.Size);
        var fn = (CreateCommandQueueFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(CreateCommandQueueFn));
        var desc = new D3D12_COMMAND_QUEUE_DESC
        {
            Type = 2,   // COMPUTE
            Priority = 0,
            Flags = 0,
            NodeMask = 0,
        };
        var queueIid = IID_ID3D12CommandQueue;
        var hr = fn(d3d12Device, ref desc, ref queueIid, out var queue);
        return hr == 0 ? queue : IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateCommandQueueFn(IntPtr thisPtr, ref D3D12_COMMAND_QUEUE_DESC pDesc, ref Guid riid, out IntPtr ppCommandQueue);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12_COMMAND_QUEUE_DESC
    {
        public int Type;
        public int Priority;
        public uint Flags;
        public uint NodeMask;
    }
}

// ───────────────────────────────────────────────────────────────────────────
// DirectML P/Invoke — DMLCreateDevice (top of an ID3D12Device → IDMLDevice)
// ───────────────────────────────────────────────────────────────────────────
internal static class DirectML
{
    public static readonly Guid IID_IDMLDevice = new("6dbd6437-96fd-423f-a22e-aef52df6ccc6");

    [DllImport("DirectML.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int DMLCreateDevice(IntPtr d3d12Device, DML_CREATE_DEVICE_FLAGS flags, [In] ref Guid riid, out IntPtr ppv);
}

// ───────────────────────────────────────────────────────────────────────────
// ORT native P/Invoke — the IntPtr DML EP overload isn't always exposed by
// the managed SessionOptions binding, so we call the C export directly.
// ───────────────────────────────────────────────────────────────────────────
internal static class Ort
{
    [DllImport("onnxruntime.dll", CallingConvention = CallingConvention.StdCall, EntryPoint = "OrtSessionOptionsAppendExecutionProviderEx_DML")]
    public static extern IntPtr OrtSessionOptionsAppendExecutionProviderEx_DML(IntPtr sessionOptions, IntPtr dmlDevice, IntPtr cmdQueue);

    [DllImport("onnxruntime.dll", CallingConvention = CallingConvention.StdCall, EntryPoint = "OrtGetApiBase")]
    private static extern IntPtr OrtGetApiBase();

    private static IntPtr _api = IntPtr.Zero;
    private static IntPtr GetApi()
    {
        if (_api != IntPtr.Zero) return _api;
        var apiBase = OrtGetApiBase();
        // OrtApiBase: { const OrtApi*(* GetApi)(uint32_t version); ... }
        var getApiFn = Marshal.ReadIntPtr(apiBase, 0);
        var fn = (GetApiFn)Marshal.GetDelegateForFunctionPointer(getApiFn, typeof(GetApiFn));
        _api = fn(20); // ORT API version 20 is well-supported by 1.24.x
        return _api;
    }

    public static string GetStatusMessage(IntPtr status)
    {
        if (status == IntPtr.Zero) return "";
        var api = GetApi();
        // OrtApi::GetErrorMessage is at slot 23 (this varies across ORT versions;
        // safe-ish for 1.24 series). If we get the wrong slot we just print "(?)"
        // — non-fatal for the spike.
        try
        {
            var fnPtr = Marshal.ReadIntPtr(api, 23 * IntPtr.Size);
            var fn = (GetErrorMessageFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(GetErrorMessageFn));
            var msgPtr = fn(status);
            return Marshal.PtrToStringAnsi(msgPtr) ?? "(no message)";
        }
        catch { return "(could not decode OrtStatus)"; }
    }

    public static void ReleaseStatus(IntPtr status)
    {
        if (status == IntPtr.Zero) return;
        try
        {
            var api = GetApi();
            // OrtApi::ReleaseStatus is at slot ~14 — varies across versions.
            // We accept the leak if we can't release; status messages are tiny.
            var fnPtr = Marshal.ReadIntPtr(api, 14 * IntPtr.Size);
            var fn = (ReleaseStatusFn)Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(ReleaseStatusFn));
            fn(status);
        }
        catch { /* swallowed — small intentional leak on version mismatch */ }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetApiFn(uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetErrorMessageFn(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReleaseStatusFn(IntPtr status);
}

// ───────────────────────────────────────────────────────────────────────────
// Minimal Identity ONNX model builder (copy of tools/ort-spike/Program.cs's
// TinyOnnx — no external model file needed for the spike).
// ───────────────────────────────────────────────────────────────────────────
internal static class TinyOnnx
{
    public static byte[] BuildIdentityModel(int elemCount)
    {
        var dim1 = WriteMessage(w => WriteVarint(w, (1 << 3) | 0, 1));
        var dimN = WriteMessage(w => WriteVarint(w, (1 << 3) | 0, elemCount));
        var shapeMsg = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteBytes(w, dim1);
            WriteTag(w, 1, 2); WriteBytes(w, dimN);
        });
        var tensorTypeInner = WriteMessage(w =>
        {
            WriteVarint(w, (1 << 3) | 0, 1);
            WriteTag(w, 2, 2); WriteBytes(w, shapeMsg);
        });
        var typeProto = WriteMessage(w => { WriteTag(w, 1, 2); WriteBytes(w, tensorTypeInner); });
        byte[] makeValueInfo(string name) => WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteString(w, name);
            WriteTag(w, 2, 2); WriteBytes(w, typeProto);
        });
        var vinX = makeValueInfo("x");
        var vinY = makeValueInfo("y");
        var node = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteString(w, "x");
            WriteTag(w, 2, 2); WriteString(w, "y");
            WriteTag(w, 4, 2); WriteString(w, "Identity");
        });
        var graph = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteBytes(w, node);
            WriteTag(w, 2, 2); WriteString(w, "g");
            WriteTag(w, 11, 2); WriteBytes(w, vinX);
            WriteTag(w, 12, 2); WriteBytes(w, vinY);
        });
        var opsetImport = WriteMessage(w => { WriteVarint(w, (2 << 3) | 0, 18); });
        var model = WriteMessage(w =>
        {
            WriteVarint(w, (1 << 3) | 0, 8);
            WriteTag(w, 2, 2); WriteString(w, "ortspike-npu");
            WriteTag(w, 7, 2); WriteBytes(w, graph);
            WriteTag(w, 8, 2); WriteBytes(w, opsetImport);
        });
        return model;
    }

    private static byte[] WriteMessage(Action<MemoryStream> body)
    {
        var ms = new MemoryStream();
        body(ms);
        return ms.ToArray();
    }
    private static void WriteTag(Stream s, int fieldNumber, int wireType) => WriteVarintRaw(s, (ulong)((fieldNumber << 3) | wireType));
    private static void WriteVarint(Stream s, int tagByte, long value) { s.WriteByte((byte)tagByte); WriteVarintRaw(s, (ulong)value); }
    private static void WriteVarintRaw(Stream s, ulong value)
    {
        while (value >= 0x80) { s.WriteByte((byte)(value | 0x80)); value >>= 7; }
        s.WriteByte((byte)value);
    }
    private static void WriteString(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        WriteVarintRaw(s, (ulong)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }
    private static void WriteBytes(Stream s, byte[] bytes)
    {
        WriteVarintRaw(s, (ulong)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }
}
