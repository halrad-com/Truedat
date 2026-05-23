namespace Truedat
{
    /// <summary>
    /// Single source of truth for the truedat version string.
    /// <para>
    /// Format: <c>MAJOR.MINOR.BUILD.REV-[branch-]epoch</c>. Branch is omitted
    /// when on main/master/HEAD/unknown — release builds look like
    /// <c>1.0.0.0-1748000000</c>; feature builds look like
    /// <c>1.0.0.0-myfeature-1748000123</c>.
    /// </para>
    /// <para>
    /// <see cref="BuildInfo"/> is generated at compile time by the
    /// GenerateBuildInfo target in Truedat.csproj (writes BuildInfo.g.cs into
    /// the intermediate output). Missing git / detached HEAD falls back to
    /// "unknown", which is treated as main for display purposes.
    /// </para>
    /// </summary>
    internal static class VersionInfo
    {
        public const string Version = "1.0.0.0";

        public static string Display { get; } = ComputeDisplay();

        public static string Branch => BuildInfo.Branch;
        public static long Epoch => BuildInfo.Epoch;

        private static string ComputeDisplay()
        {
            var branch = BuildInfo.Branch?.Trim() ?? "";
            var lower = branch.ToLowerInvariant();
            bool omit = lower.Length == 0
                        || lower == "main"
                        || lower == "master"
                        || lower == "head"
                        || lower == "unknown";

            if (omit) return Version + "-" + BuildInfo.Epoch;

            var sanitized = branch.Replace('/', '-').Replace('\\', '-');
            return Version + "-" + sanitized + "-" + BuildInfo.Epoch;
        }
    }
}
