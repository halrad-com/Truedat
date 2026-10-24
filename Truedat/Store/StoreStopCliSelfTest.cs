using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace Truedat.Store
{
    /// <summary>Real Main regressions, isolated from the global suite's scan statics and console handlers.
    /// The child host is Windows' built-in .NET Framework PowerShell, never an audio extractor.</summary>
    internal static class StoreStopCliSelfTest
    {
        const int GuardMs = 5000;

        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-stop CLI self-test (unarmed scan, throwing final write, persistent failure handoff)");
            foreach (var mode in new[] { "unarmed", "exception", "persistent" })
            {
                try { RunCase(assert, mode); }
                catch (Exception ex) { assert(false, $"StoreStop.Cli.{mode}: fixture threw {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        static void RunCase(Action<bool, string> assert, string mode)
        {
            string dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "stop-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool keep = false;
            try
            {
                string missing = Path.Combine(dir, "absent.flac"), list = Path.Combine(dir, "files.txt");
                File.WriteAllText(list, missing + Environment.NewLine);
                // FindTool checks existence. Every input is absent, so this sentinel is NEVER executed.
                File.WriteAllText(Path.Combine(dir, "essentia_streaming_extractor_music.exe"), "self-test sentinel; must never execute");
                Directory.CreateDirectory(Path.Combine(dir, "temp"));
                var start = new ProcessStartInfo
                {
                    FileName = PowerShell, WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.EnvironmentVariables["TEMP"] = Path.Combine(dir, "temp");
                start.EnvironmentVariables["TMP"] = Path.Combine(dir, "temp");
                string Literal(string s) => "'" + s.Replace("'", "''") + "'";
                string script = "$ErrorActionPreference='Stop'\n"
                    + "$a=[Reflection.Assembly]::LoadFrom(" + Literal(typeof(Program).Assembly.Location) + ")\n"
                    + "$m=$a.GetType('Truedat.Store.StoreStopCliSelfTest').GetMethod('RunChildProbe',[Reflection.BindingFlags]'Static,NonPublic')\n"
                    + "$m.Invoke($null,[object[]]@(" + Literal(mode) + "," + Literal(dir) + "))\nexit 0";
                start.Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start stop CLI test child."))
                {
                    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(30000))
                    {
                        keep = true;
                        assert(false, $"StoreStop.Cli.{mode}: child did not exit in 30 seconds; fixture retained at {dir}");
                        return; // Never kill a process or delete files it may still be using.
                    }
                    string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                    assert(child.ExitCode == 0, $"StoreStop.Cli.{mode}: child exits 0 (got {child.ExitCode})"
                        + (child.ExitCode == 0 ? "" : ": " + output.Substring(0, Math.Min(800, output.Length))));
                    if (child.ExitCode != 0) return;
                    var report = File.ReadAllLines(Path.Combine(dir, "probe.txt"))
                        .Select(line => line.Split(new[] { '=' }, 2)).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : "");
                    bool Flag(string key) => report.TryGetValue(key, out var value) && value == "True";
                    string Detail(string key) => report.TryGetValue(key, out var value) ? value : "missing";
                    assert(!report.ContainsKey("probeError"), $"StoreStop.Cli.{mode}: actual Main fixture ran ({Detail("probeError")})");
                    if (report.ContainsKey("probeError")) return;
                    if (mode == "unarmed")
                    {
                        assert(Flag("exit") && output.Contains("[skipped missing]") && File.Exists(Path.Combine(dir, "mbxmoods-skipped.csv")),
                            "StoreStop.Cli.Unarmed: real Main without moods reaches the missing-file skip and exits 0");
                        assert(!output.Contains("saved by the stop"),
                            "StoreStop.Cli.Unarmed: a scan with no armed session and no stop never claims a stop saved completed work");
                    }
                    else if (mode == "exception")
                    {
                        assert(Flag("fault") && Flag("joined") && Flag("caught"),
                            "StoreStop.Cli.Exception: a stop joins the real final catalog write before its sharing violation escapes Main (" + Detail("error") + ")");
                        assert(Flag("returned") && !Flag("claimedSaved"),
                            "StoreStop.Cli.Exception: the throwing production final-write path releases the stop without claiming a successful save (" + Detail("stop") + ")");
                        assert(Flag("unchanged"), "StoreStop.Cli.Exception: failed replacement preserves the original catalog bytes");
                    }
                    else
                    {
                        assert(Flag("injected") && Flag("joined") && Flag("snapshot"),
                            "StoreStop.Cli.Persistent: actual Main checkpoint rolls back, final publication runs, and the waiting stop reads its record before Main cleanup (" + Detail("setup") + ")");
                        assert(Flag("failed") && Flag("cause") && Flag("exit"),
                            "StoreStop.Cli.Persistent: final-owned stop preserves failed outcome, real checkpoint cause, and exit 130 (" + Detail("result") + ")");
                        assert(Flag("pending") && Flag("oneAttempt") && Flag("committed") && Flag("original"),
                            "StoreStop.Cli.Persistent: finalization does not retry the failed store stage; pending work and original committed data remain (" + Detail("durability") + ")");
                        assert(Flag("publishedFacts"),
                            "StoreStop.Cli.Persistent: the stop record carries the actual committed generation and final publication hash (" + Detail("publicationDetails") + ")");
                    }
                }
            }
            finally { if (!keep) try { Directory.Delete(dir, true); } catch { } }
        }

        static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        static TrackEntry Entry(string title) => new TrackEntry
        {
            Features = new TrackFeatures { Artist = "Stop CLI", Title = title, Bpm = 120, Mfcc = new[] { 1.5, -2.25 } },
            AudioStreamSha256 = new string('a', 64), AudioStreamSha256Source = "flac-frames",
            LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        // Invoked only by the private child host. Production Main owns Arm/BeginFinal/final writes/EndFinal throughout.
        internal static void RunChildProbe(string mode, string dir)
        {
            // PowerShell does not use truedat.exe.config's binding redirects. Resolve only this build's sibling DLLs,
            // in managed code: the scan's worker threads have no PowerShell runspace for a script delegate.
            ResolveEventHandler resolve = (_, args) =>
            {
                string dependency = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try { RunChildProbeCore(mode, dir); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }

        static void RunChildProbeCore(string mode, string dir)
        {
            var report = new Dictionary<string, string>();
            // CleanupOrphanedFiles checks same-name processes. This idle, stdin-held host guarantees its sibling guard
            // even when a focused test was launched from a non-truedat process; no shared orphan directory is swept.
            var guardStart = new ProcessStartInfo
            {
                FileName = PowerShell, Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::In.ReadLine() | Out-Null\"",
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var sibling = Process.Start(guardStart) ?? throw new InvalidOperationException("Could not start the orphan-sweep guard."))
            {
                try
                {
                    if (!SpinWait.SpinUntil(() => Program.CountSiblingTruedatProcesses() > 0, GuardMs))
                        throw new InvalidOperationException("The child cannot prove its orphan-sweep sibling guard; refusing to run Main.");
                    if (mode == "unarmed")
                    {
                        Console.SetIn(new OnePathReader(Path.Combine(dir, "absent.flac")));
                        InvokeMain(dir, null);
                        report["exit"] = (Environment.ExitCode == 0).ToString();
                    }
                    else if (mode == "exception") ExceptionProbe(dir, report);
                    else if (mode == "persistent") PersistentProbe(dir, report);
                    else throw new ArgumentException("Unknown stop CLI probe: " + mode);
                }
                catch (Exception ex) { report["probeError"] = ex.ToString(); }
                finally
                {
                    sibling.StandardInput.Close(); // EOF releases only this test's idle child, without process termination.
                    if (!sibling.WaitForExit(GuardMs)) report["probeError"] = "The orphan-sweep guard did not exit after its stdin was closed.";
                }
            }
            File.WriteAllLines(Path.Combine(dir, "probe.txt"), report.Select(p => p.Key + "=" + p.Value.Replace('\r', ' ').Replace('\n', ' ')));
        }

        static void InvokeMain(string dir, string? catalog, string? store = null)
        {
            var args = new List<string> { "--no-config", "--no-edges", "--allow-sleep", "--file-list", "-", "--stage-dir", Path.Combine(dir, "stage") };
            if (catalog != null) { args.Add("--moods"); args.Add(catalog); }
            if (store == null) args.Add("--no-store"); else { args.Add("--store"); args.Add(store); }
            typeof(Program).Assembly.EntryPoint!.Invoke(null, new object[] { args.ToArray() });
        }

        sealed class OnePathReader : TextReader
        {
            readonly string _path; readonly Action? _atRead; bool _read;
            internal OnePathReader(string path, Action? atRead = null) { _path = path; _atRead = atRead; }
            public override string? ReadLine() { if (_read) return null; _read = true; _atRead?.Invoke(); return _path; }
        }

        sealed class LineHook : TextWriter
        {
            readonly TextWriter _inner; readonly Action<string> _line;
            internal LineHook(TextWriter inner, Action<string> line) { _inner = inner; _line = line; }
            public override Encoding Encoding => _inner.Encoding;
            public override void Write(char value) => _inner.Write(value);
            public override void Write(string? value) => _inner.Write(value);
            public override void WriteLine(string? value) { _line(value ?? ""); _inner.WriteLine(value); }
            public override void WriteLine() => WriteLine("");
        }

        sealed class StopWaiter : IDisposable
        {
            readonly ManualResetEventSlim _returned = new ManualResetEventSlim(false);
            Thread? _thread;
            internal bool Joined;
            internal string? Result, Error;
            internal void Start(Action? afterReturn = null)
            {
                _thread = new Thread(() =>
                {
                    try { Result = ScanStop.Fire(130); afterReturn?.Invoke(); }
                    catch (Exception ex) { Error = ex.Message; }
                    finally { _returned.Set(); }
                }) { IsBackground = true };
                _thread.Start();
                bool reached = SpinWait.SpinUntil(() => _returned.IsSet || (_thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, GuardMs);
                Joined = reached && !_returned.IsSet;
            }
            internal bool Wait() => _returned.Wait(GuardMs);
            public void Dispose()
            {
                ScanStop.EndFinal(); // Cleanup only, AFTER observations; cannot turn a failed assertion green.
                if (_thread == null || _thread.Join(GuardMs)) _returned.Dispose();
            }
        }

        static void ExceptionProbe(string dir, Dictionary<string, string> report)
        {
            string catalog = Path.Combine(dir, "mbxmoods.json"), path = Path.Combine(dir, "absent.flac");
            Program.SaveResultsForStore(catalog, new ConcurrentDictionary<string, TrackEntry>(new[] { new KeyValuePair<string, TrackEntry>(path, Entry("Original")) }));
            string original = File.ReadAllText(catalog);
            using (var stop = new StopWaiter())
            using (var held = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int fault = 0;
                EventHandler<FirstChanceExceptionEventArgs> onException = (_, e) =>
                {
                    if (!(e.Exception is IOException) || (e.Exception.HResult & 0xffff) != 32
                        || !new StackTrace().ToString().Contains("RunFinalWrites") || Interlocked.Exchange(ref fault, 1) != 0) return;
                    stop.Start(); // Join while the REAL final owner remains on the stack, before it unwinds.
                };
                AppDomain.CurrentDomain.FirstChanceException += onException;
                Console.SetIn(new OnePathReader(path));
                bool caught = false;
                try { InvokeMain(dir, catalog); }
                catch (TargetInvocationException ex)
                {
                    var cause = ex.InnerException;
                    caught = cause is IOException && (cause.HResult & 0xffff) == 32 && (cause.StackTrace ?? "").Contains("RunFinalWrites");
                    report["error"] = cause?.GetType().Name + ": " + cause?.Message;
                }
                finally { AppDomain.CurrentDomain.FirstChanceException -= onException; }
                report["fault"] = (fault == 1).ToString(); report["joined"] = stop.Joined.ToString(); report["caught"] = caught.ToString();
                report["returned"] = (stop.Wait() && stop.Error == null).ToString();
                string stopReport = (stop.Result ?? "").ToLowerInvariant();
                report["claimedSaved"] = (stopReport.Contains("saved") && !stopReport.Contains("not saved") && !stopReport.Contains("failed")).ToString();
                report["stop"] = stop.Result ?? stop.Error ?? "still waiting before cleanup";
                report["unchanged"] = (File.ReadAllText(catalog) == original).ToString();
            }
        }

        static void PersistentProbe(string dir, Dictionary<string, string> report)
        {
            string catalog = Path.Combine(dir, "mbxmoods.json"), store = Path.Combine(dir, "truedat.db"), path = Path.Combine(dir, "absent.flac");
            using (StoreLog.Quiet())
            {
                ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [path] = Entry("Original") },
                    dir, "primary", true, "essentia", "stop-cli", "fixture", null, null);
                var publishError = StoreScan.Publish(store, catalog, out _);
                if (publishError != null) throw publishError;
            }
            long before;
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) before = os.Dal.Generation();
            PeriodicSave? periodic = null;
            StoreResultFile? snapshot = null;
            bool injected = false, startedStop = false, snapshotBeforeCleanup = false;
            using (var stop = new StopWaiter())
            {
                void Observe(string line)
                {
                    if (!injected && line.StartsWith("[skipped missing]", StringComparison.Ordinal))
                    {
                        injected = true;
                        // Read the callback's real closure; never manufacture a PeriodicSave or mutate its private fields.
                        var save = (Delegate?)typeof(ScanStop).GetField("_save", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
                        var target = save?.Target ?? throw new InvalidOperationException("The actual Main scan did not arm its stop callback.");
                        var values = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.GetValue(target)).ToArray();
                        periodic = values.OfType<PeriodicSave>().SingleOrDefault()
                            ?? throw new InvalidOperationException("The armed Main callback no longer exposes one PeriodicSave; update read-only fixture discovery.");
                        var checkpoint = values.OfType<Func<CheckpointOutcome>>().SingleOrDefault()
                            ?? throw new InvalidOperationException("The armed Main callback no longer exposes one checkpoint; update read-only fixture discovery.");
                        ScanSink.Record(path, Entry("Pending update"));
                        bool previousFault = ImportCommand.FailRunOutcomeForTest;
                        try { ImportCommand.FailRunOutcomeForTest = true; periodic.SaveNow(checkpoint, force: true); }
                        finally { ImportCommand.FailRunOutcomeForTest = previousFault; }
                        report["injected"] = (periodic.PersistentFailure != null && ScanSink.Total.CheckpointFailures == 1 && ScanSink.Pending == 1).ToString();
                    }
                    else if (injected && !startedStop && line.Contains("[published from the store:"))
                    {
                        startedStop = true;
                        stop.Start(() => { if (StoreResult.TryRead(StoreResult.PathFor(store), out var record)) snapshot = record; });
                    }
                    else if (startedStop)
                    {
                        // First post-publication output is after EndFinal, before Main's outcome/report cleanup.
                        snapshotBeforeCleanup = stop.Wait();
                    }
                }
                // Main sets OutputEncoding before stdin, which can reset Console.Out/Error. Install hooks AFTER that.
                Console.SetIn(new OnePathReader(path, () =>
                {
                    Console.SetOut(new LineHook(Console.Out, Observe));
                    Console.SetError(new LineHook(Console.Error, Observe));
                }));
                InvokeMain(dir, catalog, store);
                report["joined"] = stop.Joined.ToString();
                report["snapshot"] = (snapshotBeforeCleanup && snapshot != null && stop.Error == null).ToString();
                report["failed"] = (snapshot?.Outcome == StoreResult.OutcomeFailed).ToString();
                report["cause"] = (snapshot?.Reason?.Contains("fail-run-outcome") == true).ToString();
                report["exit"] = (snapshot?.ExitCode == 130).ToString();
                report["result"] = $"{snapshot?.Outcome}; exit {snapshot?.ExitCode}; {snapshot?.Reason}";
                report["pending"] = (ScanSink.Pending == 1).ToString();
                report["oneAttempt"] = (periodic?.Attempts == 1 && ScanSink.Total.CheckpointFailures == 1).ToString();
                long committed;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) committed = os.Dal.Generation();
                report["committed"] = (committed == before).ToString();
                string publishedHash = Publication.Sha256Hex(catalog);
                report["publishedFacts"] = (snapshot?.Store.Generation == committed && snapshot?.Export.Generation == committed
                    && snapshot?.Export.Performed == true && snapshot?.Export.Catalog?.Sha256 == publishedHash).ToString();
                report["publicationDetails"] = $"store={snapshot?.Store.Generation}, exported={snapshot?.Export.Generation}, committed={committed}, performed={snapshot?.Export.Performed}, catalog hash={snapshot?.Export.Catalog?.Sha256}, actual hash={publishedHash}";
                var entries = StoreTransfer.ReadEntries(store);
                report["original"] = (entries.TryGetValue(path, out var entry) && entry.Features?.Title == "Original").ToString();
                report["durability"] = $"pending={ScanSink.Pending}, attempts={periodic?.Attempts}, checkpoint failures={ScanSink.Total.CheckpointFailures}, generation={before}->{committed}, title={entry?.Features?.Title}";
                report["setup"] = $"injected={injected}, final publication={startedStop}, joined={stop.Joined}, snapshot={snapshotBeforeCleanup}, stop error={stop.Error}";
            }
        }
    }
}

