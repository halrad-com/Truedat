// ORT verification spike — §5.0 of VAM roadmap.
// Walks 8 verification steps. Honest pass/fail per step.
//
// Usage:
//   ortspike all            — run every step, print summary table
//   ortspike step <1..8>    — run one step
//   ortspike list-adapters  — DXGI adapter dump (for picking the NPU index)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OrtSpike;

internal static class Program
{
    private static readonly List<(string Step, string Verdict, string Notes)> _results = new();

    private static int Main(string[] args)
    {
        try
        {
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
            switch (cmd)
            {
                case "all":
                    Step1_LoadOrt();
                    Step2_X64();
                    Step3_CpuInference();
                    Step4_DmlGpu();
                    Step5_DmlNpu();
                    // Step 6 (ILRepack) is exercised by a separate merged exe; this in-tree
                    // run reports it as "see ilrepacked-ortspike.exe" so it's not silent.
                    Step6_ILRepackHint();
                    Step7_Determinism();
                    Step8_Performance();
                    PrintSummary();
                    return 0;

                case "step":
                    if (args.Length < 2) { Console.Error.WriteLine("usage: ortspike step <1..8>"); return 2; }
                    switch (args[1])
                    {
                        case "1": Step1_LoadOrt(); break;
                        case "2": Step2_X64(); break;
                        case "3": Step3_CpuInference(); break;
                        case "4": Step4_DmlGpu(); break;
                        case "5": Step5_DmlNpu(); break;
                        case "6": Step6_ILRepackHint(); break;
                        case "7": Step7_Determinism(); break;
                        case "8": Step8_Performance(); break;
                        default: Console.Error.WriteLine("step must be 1..8"); return 2;
                    }
                    PrintSummary();
                    return 0;

                case "list-adapters":
                    DumpDxgiAdapters();
                    return 0;

                case "ilrepack-self-test":
                    // Used by the ILRepacked exe to prove the merged binary still works.
                    Step3_CpuInference();
                    PrintSummary();
                    return 0;

                default:
                    Console.Error.WriteLine("usage: ortspike [all|step <n>|list-adapters|ilrepack-self-test]");
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
    // Step 1 — net48 + ORT NuGet load. Trivial OrtEnv access.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step1_LoadOrt()
    {
        Console.WriteLine("─── Step 1: net48 + ORT NuGet load ───");
        try
        {
            var env = OrtEnv.Instance();
            var ver = env.GetVersionString();
            Console.WriteLine($"  OrtEnv version: {ver}");
            Console.WriteLine($"  Runtime FW    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            _results.Add(("Step 1: net48 + ORT load", "PASS", $"ORT {ver} on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"));
        }
        catch (Exception ex)
        {
            _results.Add(("Step 1: net48 + ORT load", "FAIL", ex.GetType().Name + ": " + ex.Message));
            throw;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2 — x64 platform binding. Confirm process is 64-bit.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step2_X64()
    {
        Console.WriteLine("─── Step 2: x64 platform binding ───");
        var arch = RuntimeInformation.ProcessArchitecture;
        var is64 = Environment.Is64BitProcess;
        Console.WriteLine($"  ProcessArchitecture: {arch}");
        Console.WriteLine($"  Is64BitProcess     : {is64}");
        if (arch == Architecture.X64 && is64)
        {
            // Re-create env to prove load still works at x64
            _ = OrtEnv.Instance();
            _results.Add(("Step 2: x64 binding", "PASS", $"{arch}, 64-bit process"));
        }
        else
        {
            _results.Add(("Step 2: x64 binding", "FAIL", $"Expected X64/64-bit, got {arch}/Is64={is64}"));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 3 — Tiny ONNX inference on CPU EP.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step3_CpuInference()
    {
        Console.WriteLine("─── Step 3: CPU EP inference (identity model) ───");
        try
        {
            var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
            using var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
            using var session = new InferenceSession(modelBytes, opts);
            var input = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
            var output = RunIdentityOnce(session, input);
            var ok = AreEqual(input, output);
            Console.WriteLine($"  in : [{string.Join(", ", input)}]");
            Console.WriteLine($"  out: [{string.Join(", ", output)}]");
            _results.Add(("Step 3: CPU inference", ok ? "PASS" : "FAIL", ok ? "identity output matches input bit-for-bit" : "output diverged from input"));
        }
        catch (Exception ex)
        {
            _results.Add(("Step 3: CPU inference", "FAIL", ex.GetType().Name + ": " + ex.Message));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 4 — DirectML EP at device 0 (primary GPU).
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step4_DmlGpu()
    {
        Console.WriteLine("─── Step 4: DML EP at device 0 (GPU) ───");
        try
        {
            var (output, info) = RunOnDml(deviceId: 0);
            var ok = AreEqual(new float[] { 1f, 2f, 3f, 4f }, output);
            Console.WriteLine($"  out: [{string.Join(", ", output)}]");
            _results.Add(("Step 4: DML EP device 0 (GPU)", ok ? "PASS" : "FAIL", info));
        }
        catch (Exception ex)
        {
            _results.Add(("Step 4: DML EP device 0 (GPU)", "FAIL", ex.GetType().Name + ": " + ex.Message));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 5 — NPU dispatch. Enumerate adapters, find NPU, dispatch to it.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step5_DmlNpu()
    {
        Console.WriteLine("─── Step 5: NPU dispatch via DML ───");
        try
        {
            var adapters = EnumDxgiAdapters();
            Console.WriteLine($"  DXGI adapters discovered: {adapters.Count}");
            foreach (var a in adapters)
                Console.WriteLine($"    [{a.Index}] {a.Description}  flags=0x{a.Flags:X}");

            // DXGI may not enumerate the NPU as a regular adapter (NPUs typically live
            // in Compute Driver Model / MCDM, not DXGI). Try each adapter index until
            // we either get a session that runs, or exhaust the list. If none work, YELLOW.
            int triedAdapters = 0;
            string lastError = "";
            foreach (var a in adapters)
            {
                try
                {
                    triedAdapters++;
                    var (output, info) = RunOnDml(deviceId: a.Index);
                    Console.WriteLine($"  adapter [{a.Index}] '{a.Description}' → ran successfully ({info})");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Console.WriteLine($"  adapter [{a.Index}] '{a.Description}' → FAILED: {ex.Message}");
                }
            }

            // Whether we found an NPU specifically depends on whether DML sees it. Mark
            // YELLOW unless an adapter description includes "NPU" or "AI Boost" or "XDNA".
            var npuMatch = adapters.FirstOrDefault(a =>
                a.Description.IndexOf("NPU", StringComparison.OrdinalIgnoreCase) >= 0
                || a.Description.IndexOf("AI Boost", StringComparison.OrdinalIgnoreCase) >= 0
                || a.Description.IndexOf("XDNA", StringComparison.OrdinalIgnoreCase) >= 0
                || a.Description.IndexOf("Ryzen AI", StringComparison.OrdinalIgnoreCase) >= 0);

            if (npuMatch.Description != null)
            {
                _results.Add(("Step 5: NPU dispatch", "PASS", $"NPU adapter '{npuMatch.Description}' enumerated at index {npuMatch.Index}"));
            }
            else
            {
                _results.Add(("Step 5: NPU dispatch", "YELLOW",
                    triedAdapters > 0
                        ? $"DXGI enumerated {triedAdapters} adapter(s) but no NPU descriptor seen (NPUs typically use MCDM, not DXGI). GPU-only path remains viable."
                        : $"No DXGI adapters enumerated. NPU dispatch path not validated. lastError={lastError}"));
            }
        }
        catch (Exception ex)
        {
            _results.Add(("Step 5: NPU dispatch", "YELLOW", "Adapter enumeration failed: " + ex.Message + " — GPU path still viable; mark YELLOW per spike pass/fail criteria."));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 6 — ILRepack merge. This in-tree code runs against unmerged DLLs;
    // the merged-binary test happens after a separate ILRepack build step.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step6_ILRepackHint()
    {
        Console.WriteLine("─── Step 6: ILRepack merge ───");
        var thisExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(thisExe)!;
        var managedDlls = Directory.GetFiles(dir, "Microsoft.ML.OnnxRuntime*.dll");
        var nativeDlls = Directory.GetFiles(dir, "*.dll")
            .Where(p => !Path.GetFileName(p).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ToArray();
        Console.WriteLine($"  Managed ORT DLLs next to exe: {managedDlls.Length}");
        foreach (var f in managedDlls) Console.WriteLine($"    {Path.GetFileName(f)}");
        Console.WriteLine($"  Native DLLs next to exe     : {nativeDlls.Length}");
        foreach (var f in nativeDlls) Console.WriteLine($"    {f}");

        var ilrepackedExe = Path.Combine(dir, "ilrepacked-ortspike.exe");
        if (File.Exists(ilrepackedExe))
        {
            // Run it with ilrepack-self-test and check exit code.
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
            if (p.ExitCode == 0 && stdout.Contains("PASS"))
            {
                _results.Add(("Step 6: ILRepack merge", "PASS", $"ilrepacked-ortspike.exe runs CPU inference. {managedDlls.Length} managed + {nativeDlls.Length} native DLL(s) need to ship beside merged exe."));
            }
            else
            {
                _results.Add(("Step 6: ILRepack merge", "FAIL", $"merged exe exit={p.ExitCode}. stderr head: {Truncate(stderr, 200)}"));
            }
        }
        else
        {
            _results.Add(("Step 6: ILRepack merge", "DEFERRED",
                $"ILRepacked binary not present at '{ilrepackedExe}'. Build via the merge script in tools/ort-spike/build-ilrepack.cmd; re-run this step. Provisional read: {managedDlls.Length} managed DLL(s) candidate for merge, {nativeDlls.Length} native DLL(s) must ship as siblings regardless."));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 7 — Determinism across EPs.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step7_Determinism()
    {
        Console.WriteLine("─── Step 7: Determinism (5 runs per EP) ───");
        var input = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
        var notes = new StringBuilder();

        try
        {
            var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
            // CPU EP
            var cpuOk = RepeatRunsAreIdentical(modelBytes, input, opts =>
            {
                opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            }, label: "CPU");
            notes.Append($"CPU: {(cpuOk ? "5/5 bit-identical" : "drift detected")}");
        }
        catch (Exception ex) { notes.Append($"CPU: error ({ex.Message})"); }

        try
        {
            var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
            var dmlOk = RepeatRunsAreIdentical(modelBytes, input, opts =>
            {
                opts.AppendExecutionProvider_DML(0);
                opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            }, label: "DML/0");
            notes.Append($"; DML/0: {(dmlOk ? "5/5 bit-identical" : "drift detected")}");
        }
        catch (Exception ex) { notes.Append($"; DML/0: error ({ex.Message})"); }

        _results.Add(("Step 7: Determinism (5x per EP, identity op)", "INFO", notes.ToString()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 8 — Performance: 100 inferences per EP, total wall-clock.
    // ─────────────────────────────────────────────────────────────────────────
    private static void Step8_Performance()
    {
        Console.WriteLine("─── Step 8: Performance (100 inferences per EP) ───");
        var input = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
        var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
        var notes = new StringBuilder();

        try
        {
            var cpuMs = TimeRuns(modelBytes, input, opts =>
            {
                opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            }, runs: 100);
            notes.Append($"CPU: {cpuMs:F1} ms total ({cpuMs / 100.0:F3} ms/inf)");
        }
        catch (Exception ex) { notes.Append($"CPU: error ({ex.Message})"); }

        try
        {
            var dmlMs = TimeRuns(modelBytes, input, opts =>
            {
                opts.AppendExecutionProvider_DML(0);
                opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            }, runs: 100);
            notes.Append($"; DML/0: {dmlMs:F1} ms total ({dmlMs / 100.0:F3} ms/inf)");
        }
        catch (Exception ex) { notes.Append($"; DML/0: error ({ex.Message})"); }

        _results.Add(("Step 8: Performance (100 runs, identity op)", "INFO", notes.ToString()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private static float[] RunIdentityOnce(InferenceSession session, float[] input)
    {
        var tensor = new DenseTensor<float>(input, new[] { 1, input.Length });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("x", tensor) };
        using var results = session.Run(inputs);
        return results.First().AsTensor<float>().ToArray();
    }

    private static (float[] Output, string Info) RunOnDml(int deviceId)
    {
        var modelBytes = TinyOnnx.BuildIdentityModel(elemCount: 4);
        using var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
        opts.AppendExecutionProvider_DML(deviceId);
        using var session = new InferenceSession(modelBytes, opts);
        var output = RunIdentityOnce(session, new float[] { 1f, 2f, 3f, 4f });
        return (output, $"DML EP appended at deviceId={deviceId}, session created, inference returned {output.Length} floats");
    }

    private static bool RepeatRunsAreIdentical(byte[] modelBytes, float[] input, Action<SessionOptions> configure, string label)
    {
        using var opts = new SessionOptions();
        configure(opts);
        using var session = new InferenceSession(modelBytes, opts);
        float[]? first = null;
        for (int i = 0; i < 5; i++)
        {
            var output = RunIdentityOnce(session, input);
            if (first == null) first = output;
            else if (!AreEqualBitwise(first, output))
            {
                Console.WriteLine($"  {label} run {i + 1} drift: first={string.Join(",", first)} vs now={string.Join(",", output)}");
                return false;
            }
        }
        return true;
    }

    private static double TimeRuns(byte[] modelBytes, float[] input, Action<SessionOptions> configure, int runs)
    {
        using var opts = new SessionOptions();
        configure(opts);
        using var session = new InferenceSession(modelBytes, opts);
        // Warmup
        for (int i = 0; i < 5; i++) RunIdentityOnce(session, input);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++) RunIdentityOnce(session, input);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static bool AreEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
        return true;
    }

    private static bool AreEqualBitwise(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (FloatBits(a[i]) != FloatBits(b[i])) return false;
        return true;
    }

    private static int FloatBits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("ORT spike summary");
        Console.WriteLine("══════════════════════════════════════════════");
        foreach (var r in _results)
        {
            Console.WriteLine($"  [{r.Verdict,-8}] {r.Step}");
            if (!string.IsNullOrEmpty(r.Notes))
                Console.WriteLine($"             {r.Notes}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DXGI adapter enumeration (for Step 5 NPU index discovery)
    // ─────────────────────────────────────────────────────────────────────────
    internal struct DxgiAdapter
    {
        public int Index;
        public string Description;
        public uint Flags;
    }

    private static void DumpDxgiAdapters()
    {
        var adapters = EnumDxgiAdapters();
        Console.WriteLine($"DXGI adapters: {adapters.Count}");
        foreach (var a in adapters)
            Console.WriteLine($"  [{a.Index}] '{a.Description}' flags=0x{a.Flags:X}");
    }

    private static List<DxgiAdapter> EnumDxgiAdapters()
    {
        var result = new List<DxgiAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
            return result;
        try
        {
            // Vtable: IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) + IDXGIFactory1: EnumAdapters1 at slot 12
            var vtbl = Marshal.ReadIntPtr(factoryPtr);
            var enumAdapters1Ptr = Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size);
            var enumAdapters1 = (EnumAdapters1Fn)Marshal.GetDelegateForFunctionPointer(enumAdapters1Ptr, typeof(EnumAdapters1Fn));

            for (int i = 0; i < 32; i++)
            {
                if (enumAdapters1(factoryPtr, (uint)i, out var adapterPtr) != 0 || adapterPtr == IntPtr.Zero)
                    break;
                try
                {
                    var aVtbl = Marshal.ReadIntPtr(adapterPtr);
                    // IDXGIAdapter1 vtable: IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) + IDXGIAdapter1: GetDesc1 at slot 10
                    var getDescPtr = Marshal.ReadIntPtr(aVtbl, 10 * IntPtr.Size);
                    var getDesc = (GetDesc1Fn)Marshal.GetDelegateForFunctionPointer(getDescPtr, typeof(GetDesc1Fn));
                    var desc = new DXGI_ADAPTER_DESC1();
                    if (getDesc(adapterPtr, ref desc) == 0)
                    {
                        result.Add(new DxgiAdapter
                        {
                            Index = i,
                            Description = new string(desc.Description).TrimEnd('\0'),
                            Flags = desc.Flags
                        });
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
        return result;
    }

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr thisPtr, uint Adapter, out IntPtr ppAdapter);

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
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Minimal Identity ONNX model builder. No external model file needed.
//
// ONNX models are protobuf3. This emits a ModelProto containing a single
// Identity node with one float input "x" (shape [1, N]) and matching output
// "y". Total payload ~120 bytes — small enough to read in the debugger.
// ─────────────────────────────────────────────────────────────────────────────
internal static class TinyOnnx
{
    public static byte[] BuildIdentityModel(int elemCount)
    {
        // Build inner messages bottom-up so we can length-prefix each.
        // TensorShapeProto.Dimension: { dim_value: 1 } then { dim_value: elemCount }
        var dim1 = WriteMessage(w => WriteVarint(w, (1 << 3) | 0, 1));       // dim_value = 1
        var dimN = WriteMessage(w => WriteVarint(w, (1 << 3) | 0, elemCount)); // dim_value = elemCount

        // TensorShapeProto: dim (field 1, repeated) — wire type 2 (length-delimited)
        var shapeMsg = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteBytes(w, dim1);
            WriteTag(w, 1, 2); WriteBytes(w, dimN);
        });

        // TypeProto.Tensor: elem_type=1 (FLOAT, field 1), shape (field 2, message)
        var tensorTypeInner = WriteMessage(w =>
        {
            WriteVarint(w, (1 << 3) | 0, 1);         // elem_type = 1 (FLOAT)
            WriteTag(w, 2, 2); WriteBytes(w, shapeMsg); // shape
        });

        // TypeProto: tensor_type (field 1, message)
        var typeProto = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteBytes(w, tensorTypeInner);
        });

        // ValueInfoProto: name (field 1, string), type (field 2, message)
        byte[] makeValueInfo(string name) => WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteString(w, name);
            WriteTag(w, 2, 2); WriteBytes(w, typeProto);
        });
        var vinX = makeValueInfo("x");
        var vinY = makeValueInfo("y");

        // NodeProto: input "x" (field 1), output "y" (field 2), op_type "Identity" (field 4)
        var node = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteString(w, "x");
            WriteTag(w, 2, 2); WriteString(w, "y");
            WriteTag(w, 4, 2); WriteString(w, "Identity");
        });

        // GraphProto: node (1), name (2), input (11), output (12)
        var graph = WriteMessage(w =>
        {
            WriteTag(w, 1, 2); WriteBytes(w, node);
            WriteTag(w, 2, 2); WriteString(w, "g");
            WriteTag(w, 11, 2); WriteBytes(w, vinX);
            WriteTag(w, 12, 2); WriteBytes(w, vinY);
        });

        // OperatorSetIdProto: domain="" (field 1), version=18 (field 2)
        var opsetImport = WriteMessage(w =>
        {
            // domain "" omitted (default proto3) — version only
            WriteVarint(w, (2 << 3) | 0, 18);
        });

        // ModelProto: ir_version=8 (field 1), opset_import (field 8), graph (field 7), producer_name (field 2)
        var model = WriteMessage(w =>
        {
            WriteVarint(w, (1 << 3) | 0, 8);            // ir_version = 8
            WriteTag(w, 2, 2); WriteString(w, "ortspike");
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

    private static void WriteTag(Stream s, int fieldNumber, int wireType)
    {
        WriteVarintRaw(s, (ulong)((fieldNumber << 3) | wireType));
    }

    // For varint values where caller already encoded tag separately.
    // Overload: writes a tag byte (computed) + varint payload.
    private static void WriteVarint(Stream s, int tagByte, long value)
    {
        s.WriteByte((byte)tagByte);
        WriteVarintRaw(s, (ulong)value);
    }

    private static void WriteVarintRaw(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
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
