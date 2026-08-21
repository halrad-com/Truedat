using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Truedat
{
    /// <summary>
    /// Persistent defaults for command-line flags: truedat.config.json, beside the exe.
    ///
    /// Precedence is one rule — explicit flag &gt; config &gt; built-in default. A config value
    /// is a default and the command line always wins; nothing here can stop a typed command
    /// from doing what it says.
    ///
    /// Keys ARE flag names, so there is no second vocabulary and no mapping table to drift.
    /// The consequence: a negative flag stays negative, so "always skip SMFM" is
    /// <c>"no-smfm": true</c>.
    ///
    /// Per-machine policy needs nothing extra — each machine has its own install, so it has
    /// its own config.
    ///
    /// Anchored to the exe directory and nowhere else. A discovery ladder that can silently
    /// pick a different file is what IsBareCwdCatalog exists to prevent.
    ///
    /// Fails loud both ways: an unparseable file or an unknown key REFUSES the run and
    /// leaves the file untouched. Running while the operator believes settings are in force
    /// is the silent-wrong failure (mbxmoods-exclude.json's rule), and a typo'd key must not
    /// be indistinguishable from a setting that does nothing (the unrecognized-flag rule).
    ///
    /// Settable is an allowlist and holds knobs only — never a verb, never a target path.
    /// </summary>
    internal static class TruedatConfig
    {
        internal const string FileName = "truedat.config.json";

        internal enum Kind { Bool, Int, Text }

        internal sealed class Setting
        {
            public string Key = "";
            public Kind Kind;
            public int Min;
            public int Max;
            public string Help = "";
        }

        /// <summary>Every settable key. Ranges mirror what the argument parser accepts, so a
        /// value accepted here can never be rejected downstream and left behind as a stray
        /// token (see BuildArgs).</summary>
        internal static readonly Setting[] Settings = new[]
        {
            new Setting { Key = "no-stage",        Kind = Kind.Bool, Help = "read sources directly, never stage a local copy" },
            new Setting { Key = "no-quick-cache",  Kind = Kind.Bool, Help = "disable the head-64k quick cache tier" },
            new Setting { Key = "no-bitusage",     Kind = Kind.Bool, Help = "skip the bitUsage signal (one ffmpeg pass per track)" },
            new Setting { Key = "no-hf-analysis",  Kind = Kind.Bool, Help = "skip the HF signals (one ffmpeg pass per track)" },
            new Setting { Key = "no-smfm",         Kind = Kind.Bool, Help = "never read Sony SMFM tags" },
            new Setting { Key = "file-md5",        Kind = Kind.Bool, Help = "compute and store the whole-file MD5" },
            new Setting { Key = "enableshortfiles",Kind = Kind.Bool, Help = "analyze files under the short-file threshold" },
            new Setting { Key = "allow-sleep",     Kind = Kind.Bool, Help = "do not hold the machine awake during a scan" },
            new Setting { Key = "refresh-features",Kind = Kind.Bool, Help = "re-analyze entries missing a later feature wave" },
            new Setting { Key = "refresh-smfm",    Kind = Kind.Bool, Help = "re-read SMFM tags on cache hits" },
            new Setting { Key = "audit",           Kind = Kind.Bool, Help = "verbose per-track diagnostics to stderr" },
            new Setting { Key = "parallel",        Kind = Kind.Int,  Min = 1, Max = 1024,    Help = "worker count" },
            new Setting { Key = "cpu-limit",       Kind = Kind.Int,  Min = 1, Max = 100,     Help = "percent CPU cap for subprocesses" },
            new Setting { Key = "keep-backups",    Kind = Kind.Int,  Min = 0, Max = 10000,   Help = "catalog backups to retain (0 = keep all)" },
            new Setting { Key = "long-track-mins", Kind = Kind.Int,  Min = 1, Max = 10000,   Help = "duration that tags a track as long" },
            new Setting { Key = "max-duration",    Kind = Kind.Int,  Min = 0, Max = 1000000, Help = "skip tracks longer than this (seconds)" },
            new Setting { Key = "stage-dir",       Kind = Kind.Text, Help = "where staged copies are written" },
            // The one entry that is not purely a knob: it makes a scan run --fixup afterwards
            // (as a child process). It earns its place because "keep the catalog matching what
            // is on disk" is a standing policy, not a per-invocation decision — and it removes
            // nothing a hand-run --fixup would not, with the same guards and the same refusals.
            new Setting { Key = "fixup-after-scan",Kind = Kind.Bool, Help = "reconcile the catalog against disk after a full scan" },
        };

        private static readonly Dictionary<string, Setting> ByKey =
            Settings.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        internal sealed class LoadResult
        {
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string? FileRead;
            /// <summary>Non-empty means REFUSE TO RUN. Every entry is operator-facing.</summary>
            public List<string> Errors = new List<string>();
        }

        /// <summary>Read truedat.config.json beside the exe. An absent file contributes
        /// nothing and is not an error — an install without one behaves exactly as before.</summary>
        internal static LoadResult Load(string exeDir)
        {
            var result = new LoadResult();
            string path = Path.Combine(exeDir, FileName);
            if (!File.Exists(path)) return result;
            result.FileRead = path;
            ReadInto(path, result);
            return result;
        }

        private static void ReadInto(string path, LoadResult result)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { result.Errors.Add($"{path}: cannot read ({ex.Message})"); return; }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (JsonException ex)
            {
                result.Errors.Add($"{path}: not valid JSON ({ex.Message})");
                return;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    result.Errors.Add($"{path}: top level must be a JSON object");
                    return;
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!ByKey.TryGetValue(prop.Name, out var setting))
                    {
                        var near = Settings.Select(s => s.Key).FirstOrDefault(k =>
                            k.Replace("-", "").Equals(prop.Name.Replace("-", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                        result.Errors.Add(near != null
                            ? $"{path}: unknown setting \"{prop.Name}\" — did you mean \"{near}\"?"
                            : $"{path}: unknown setting \"{prop.Name}\" (see: truedat --config)");
                        continue;
                    }
                    if (!TryReadValue(prop, setting, out var value, out var why))
                    {
                        result.Errors.Add($"{path}: {setting.Key} — {why}");
                        continue;
                    }
                    result.Values[setting.Key] = value;
                }
            }
        }

        /// <summary>Validate at LOAD, not at use. The argument loop consumes a value-flag's
        /// value only when it is valid, so an out-of-range number would be left behind as a
        /// bare token and silently become the positional library path.</summary>
        private static bool TryReadValue(JsonProperty prop, Setting setting, out string value, out string why)
        {
            value = ""; why = "";
            switch (setting.Kind)
            {
                case Kind.Bool:
                    if (prop.Value.ValueKind == JsonValueKind.True) { value = "true"; return true; }
                    if (prop.Value.ValueKind == JsonValueKind.False) { value = "false"; return true; }
                    why = "must be true or false";
                    return false;

                case Kind.Int:
                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int n))
                    { why = $"must be a whole number between {setting.Min} and {setting.Max}"; return false; }
                    if (n < setting.Min || n > setting.Max)
                    { why = $"{n} is out of range ({setting.Min}..{setting.Max})"; return false; }
                    value = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;

                default:
                    if (prop.Value.ValueKind != JsonValueKind.String) { why = "must be a string"; return false; }
                    var s = prop.Value.GetString() ?? "";
                    if (s.Trim().Length == 0) { why = "must not be empty"; return false; }
                    value = s;
                    return true;
            }
        }

        /// <summary>Render settings as arguments parsed BEFORE the operator's own. That is what
        /// makes precedence true without touching a use site: the parser assigns as it goes, so
        /// an explicit flag simply overwrites what config seeded. A false boolean emits NOTHING —
        /// false means the built-in default, and most of these flags have no opposite form.</summary>
        internal static string[] BuildArgs(IDictionary<string, string> values)
        {
            var args = new List<string>();
            foreach (var setting in Settings)   // stable order, independent of file order
            {
                if (!values.TryGetValue(setting.Key, out var v)) continue;
                if (setting.Kind == Kind.Bool)
                {
                    if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) args.Add("--" + setting.Key);
                    continue;
                }
                args.Add("--" + setting.Key);
                args.Add(v);
            }
            return args.ToArray();
        }

        /// <summary>Write one setting, preserving the rest. Rewrites from a validated map, so a
        /// file that survives a write is always one this loader accepts — a config that saves but
        /// will not load is the worst outcome available here.</summary>
        internal static bool TrySet(string path, string key, string rawValue, out string error)
        {
            error = "";
            if (!ByKey.TryGetValue(key, out var setting))
            {
                error = $"unknown setting \"{key}\" (see: truedat --config)";
                return false;
            }

            var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                var probe = new LoadResult();
                ReadInto(path, probe);
                if (probe.Errors.Count > 0)
                {
                    // Refuse rather than overwrite — rewriting now would discard whatever else
                    // the operator has in there along with the problem.
                    error = probe.Errors[0];
                    return false;
                }
                existing = probe.Values;
            }

            if (!TryParseRaw(setting, rawValue, out var normalized, out var why))
            {
                error = $"{key} — {why}";
                return false;
            }
            existing[setting.Key] = normalized;

            var lines = new List<string>();
            foreach (var s in Settings)
            {
                if (!existing.TryGetValue(s.Key, out var v)) continue;
                lines.Add($"  \"{s.Key}\": {(s.Kind == Kind.Text ? JsonSerializer.Serialize(v) : v.ToLowerInvariant())}");
            }
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine(string.Join("," + Environment.NewLine, lines));
            sb.AppendLine("}");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = $"cannot write {path}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Parse a value as typed on the command line. Shares every rule with the file
        /// reader so the two cannot disagree on what is legal.</summary>
        internal static bool TryParseRaw(Setting setting, string raw, out string normalized, out string why)
        {
            normalized = ""; why = "";
            raw = (raw ?? "").Trim();
            switch (setting.Kind)
            {
                case Kind.Bool:
                    if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("on", StringComparison.OrdinalIgnoreCase))
                    { normalized = "true"; return true; }
                    if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" || raw.Equals("off", StringComparison.OrdinalIgnoreCase))
                    { normalized = "false"; return true; }
                    why = "must be true/false (on/off accepted)";
                    return false;

                case Kind.Int:
                    if (!int.TryParse(raw, out int n)) { why = $"must be a whole number between {setting.Min} and {setting.Max}"; return false; }
                    if (n < setting.Min || n > setting.Max) { why = $"{n} is out of range ({setting.Min}..{setting.Max})"; return false; }
                    normalized = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;

                default:
                    if (raw.Length == 0) { why = "must not be empty"; return false; }
                    normalized = raw;
                    return true;
            }
        }

        internal static Setting? Find(string key)
            => ByKey.TryGetValue(key ?? "", out var s) ? s : null;
    }
}
