using System;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// The self-test fixture folder is cleaned up by the next run: whatever an earlier run left (a fixture a failed test
    /// kept, a folder a crashed or killed run never got to delete) goes when the next self-test starts — unless another
    /// truedat is running at that moment, because a self-test running now has its fixtures in the same folder.
    /// </summary>
    internal static class SelfTestTempSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Self-test temp folder: the next run removes what earlier runs left, never while another truedat runs");
            TestLeftovers(assert);
            TestOtherRunning(assert);
            TestFileInUse(assert);
        }

        static string NewRoot() => Path.Combine(SelfTestTemp.Root, "selftesttemp-" + Guid.NewGuid().ToString("N"));

        static void Remove(string root) { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }

        static void TestLeftovers(Action<bool, string> assert)
        {
            var root = NewRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "old-1"));
                File.WriteAllText(Path.Combine(root, "old-1", "mbxmoods.json"), "{}");
                File.WriteAllText(Path.Combine(root, "old.bin"), "x");
                using (var run = SelfTestTemp.Begin(root, () => false))
                {
                    assert(!run.OtherRunning, "selftest-temp: with no other truedat running the cleanup goes ahead");
                    assert(!Directory.Exists(Path.Combine(root, "old-1")) && !File.Exists(Path.Combine(root, "old.bin")),
                        "selftest-temp: the folder and the file an earlier run left are removed when the next run starts");
                    assert(run.Removed == 2 && run.StillThere.Count == 0, $"selftest-temp: and counted ({run.Removed} removed, {run.StillThere.Count} left)");
                    assert(Directory.Exists(root), "selftest-temp: the folder itself stays while the run makes its fixtures in it");
                }
                assert(!Directory.Exists(root), "selftest-temp: at the end the run removes the folder once it is empty");
            }
            finally { Remove(root); }
        }

        static void TestOtherRunning(Action<bool, string> assert)
        {
            var root = NewRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "theirs"));
                File.WriteAllText(Path.Combine(root, "theirs", "mbxmoods.json"), "{}");
                using (var run = SelfTestTemp.Begin(root, () => true))
                {
                    assert(run.OtherRunning && run.Removed == 0, $"selftest-temp: with another truedat running nothing is removed ({run.Removed})");
                    assert(File.Exists(Path.Combine(root, "theirs", "mbxmoods.json")), "selftest-temp: a running self-test's fixture is left exactly as it was");
                }
                assert(Directory.Exists(Path.Combine(root, "theirs")), "selftest-temp: and the end of this run leaves it alone too");
            }
            finally { Remove(root); }
        }

        static void TestFileInUse(Action<bool, string> assert)
        {
            var root = NewRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "held"));
                Directory.CreateDirectory(Path.Combine(root, "free"));
                using (new FileStream(Path.Combine(root, "held", "truedat.db"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var run = SelfTestTemp.Begin(root, () => false))
                {
                    assert(!Directory.Exists(Path.Combine(root, "free")), "selftest-temp: a leftover nothing holds is removed");
                    assert(run.StillThere.Count == 1 && run.StillThere[0] == Path.Combine(root, "held"),
                        $"selftest-temp: one a program still holds a file in stays, and is named ({string.Join(", ", run.StillThere)})");
                }
            }
            finally { Remove(root); }
        }
    }
}
