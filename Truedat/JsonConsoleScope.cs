using System;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// Reserves stdout for a mode's explicit JSON result. Helpers and workers
    /// can keep writing diagnostics through Console.Out; those go to stderr
    /// until the mode returns. The console writers are borrowed, never closed.
    /// </summary>
    internal sealed class JsonConsoleScope : IDisposable
    {
        private readonly bool _enabled;
        private bool _disposed;

        public TextWriter ResultWriter { get; }

        public JsonConsoleScope(bool enabled)
        {
            ResultWriter = Console.Out;
            _enabled = enabled;
            if (enabled) Console.SetOut(Console.Error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_enabled) Console.SetOut(ResultWriter);
        }
    }
}
