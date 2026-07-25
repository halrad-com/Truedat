namespace Truedat
{
    /// <summary>
    /// Single source of truth for the truedat version string.
    /// <para>
    /// Format: <c>MAJOR.MINOR.BUILD.REV-[branch-]commit[+dirty]</c>. Branch is
    /// omitted when on main/master/HEAD/unknown — release builds look like
    /// <c>1.0.0.0-2f99d72</c>; feature builds look like
    /// <c>1.0.0.0-myfeature-2f99d72</c>.
    /// </para>
    /// <para>
    /// The stamp is the COMMIT, not a build clock. It used to be a build epoch,
    /// which answered the wrong question and answered it unreliably — the dist
    /// binary once reported the same epoch across two builds containing
    /// different code, so the only trustworthy way to identify what an exe
    /// held was counting self-test assertions. A commit cannot go stale.
    /// </para>
    /// <para>
    /// <c>+dirty</c> means the working tree had uncommitted tracked changes at
    /// build time, so the commit does NOT fully describe this binary. That
    /// suffix is the honest half of the stamp: without it, a build from a
    /// modified tree would masquerade as the reviewed commit.
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
        public static string Commit => BuildInfo.Commit;
        public static string Describe => BuildInfo.Describe;
        public static bool Dirty => BuildInfo.Dirty;

        private static string ComputeDisplay()
        {
            var branch = BuildInfo.Branch?.Trim() ?? "";
            var lower = branch.ToLowerInvariant();
            bool omit = lower.Length == 0
                        || lower == "main"
                        || lower == "master"
                        || lower == "head"
                        || lower == "unknown";

            // git describe carries the release line - nearest tag, distance past it, and the
            // commit - so the stamp says which RELEASE a binary belongs to, not just which
            // commit. Falls back to the bare SHA when describe is unavailable.
            var commit = (BuildInfo.Describe?.Trim() ?? "");
            if (commit.Length == 0) commit = (BuildInfo.Commit?.Trim() ?? "");
            if (commit.Length == 0) commit = "unknown";
            if (BuildInfo.Dirty) commit += "+dirty";

            if (omit) return Version + "-" + commit;

            var sanitized = branch.Replace('/', '-').Replace('\\', '-');
            return Version + "-" + sanitized + "-" + commit;
        }
    }
}
