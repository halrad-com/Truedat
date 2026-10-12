using System;

namespace Truedat.Store
{
    internal enum StoreLogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

    /// <summary>Levelled logging for the v2 store, to stderr (truedat's stdout is a data channel in
    /// <c>--json-output</c> mode). Debug and Trace are gated by the existing <c>--audit</c> flag via
    /// <see cref="SetVerbose"/>; the sink is swappable so the self-test can capture lines.</summary>
    internal static class StoreLog
    {
        internal static StoreLogLevel Threshold = StoreLogLevel.Info;
        internal static Action<string> Sink = s => Console.Error.WriteLine(s);

        internal static void SetVerbose(bool audit) => Threshold = audit ? StoreLogLevel.Trace : StoreLogLevel.Info;

        /// <summary>Scope for a read-only summary (<c>--stats</c>) that opens the store incidentally: lifts the
        /// threshold to Warn so the "store open" line does not print inside someone else's report, and restores
        /// it on dispose. Under <c>--audit</c> it suppresses nothing: the operator asked for detail.</summary>
        internal static IDisposable Quiet()
        {
            var scope = new Restore(Threshold);
            if (Threshold == StoreLogLevel.Info) Threshold = StoreLogLevel.Warn;
            return scope;
        }

        sealed class Restore : IDisposable
        {
            readonly StoreLogLevel _saved;
            public Restore(StoreLogLevel saved) { _saved = saved; }
            public void Dispose() => Threshold = _saved;
        }

        internal static void Trace(string message) => Write(StoreLogLevel.Trace, message);
        internal static void Debug(string message) => Write(StoreLogLevel.Debug, message);
        internal static void Info(string message) => Write(StoreLogLevel.Info, message);
        internal static void Warn(string message) => Write(StoreLogLevel.Warn, message);
        internal static void Error(string message) => Write(StoreLogLevel.Error, message);

        static void Write(StoreLogLevel level, string message)
        {
            if (level < Threshold) return;
            Sink($"[store] {Tag(level)} {message}");
        }

        static string Tag(StoreLogLevel level) => level switch
        {
            StoreLogLevel.Trace => "TRACE",
            StoreLogLevel.Debug => "DEBUG",
            StoreLogLevel.Info => "INFO",
            StoreLogLevel.Warn => "WARN",
            _ => "ERROR",
        };
    }
}
