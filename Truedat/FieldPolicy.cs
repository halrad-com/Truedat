using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Truedat
{
    /// <summary>
    /// Field-emission policy for mbxmoods.json, driven by <c>mbxmoods-schema.json</c>
    /// (exclude-default: any field NOT listed is emitted). Loaded once from beside the exe.
    /// A field in the schema's <c>excluded</c> block is NOT written by the scan writer and is
    /// stripped from existing catalogs by <c>--fixup</c>. If no schema file is found, a safe
    /// built-in default (matching the committed schema) is used, so the cut is deterministic
    /// even when the file is missing.
    ///
    /// NOTE: reinstating an excluded Essentia field costs a FULL library re-scan — the raw
    /// extractor JSON is deleted at parse, so there is no parse-only backfill. Cutting is cheap;
    /// un-cutting is a multi-day re-analysis. The registry is the single source of truth shared
    /// by the writer and --fixup so scan-time emission and cleanup can never drift.
    /// </summary>
    internal static class FieldPolicy
    {
        // Built-in default when no schema file is found: the committed mbxmoods-schema.json itself, embedded at build, so the
        // fallback cannot drift from the file (X1 — a hand-kept copy carried bpmHistogram only and let trackId back out).
        static readonly string[] DefaultExcluded = new List<string>(ParseExcluded(EmbeddedSchemaJson())).ToArray();

        static HashSet<string>? _excluded;
        static readonly object _lock = new object();

        static HashSet<string> ExcludedSet
        {
            get
            {
                if (_excluded != null) return _excluded;
                lock (_lock)
                {
                    if (_excluded == null) _excluded = Load();
                    return _excluded;
                }
            }
        }

        /// <summary>True if the field must not be emitted (and should be stripped by --fixup).</summary>
        public static bool IsExcluded(string field) => ExcludedSet.Contains(field);

        /// <summary>The excluded field names — the set --fixup strips from an existing entry.</summary>
        public static IReadOnlyCollection<string> ExcludedFieldNames => ExcludedSet;

        static HashSet<string> Load() => LoadFrom(Path.Combine(AppContext.BaseDirectory, "mbxmoods-schema.json"));

        /// <summary>The excluded set from the schema file at <paramref name="path"/>, or the built-in default when the file is
        /// missing, unreadable or corrupt. A present, valid schema is authoritative — even an empty excluded block means "cut
        /// nothing" (the operator un-cut a field on purpose).</summary>
        internal static HashSet<string> LoadFrom(string path)
        {
            try
            {
                if (File.Exists(path)) return ParseExcluded(File.ReadAllText(path));
            }
            catch
            {
                // Unreadable/corrupt schema: fall back to the safe built-in default rather than
                // silently emitting a field the operator meant to cut.
            }
            return new HashSet<string>(DefaultExcluded, StringComparer.Ordinal);
        }

        /// <summary>The names under <c>excluded.fields</c> of a schema document (throws on invalid JSON).</summary>
        internal static HashSet<string> ParseExcluded(string json)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("excluded", out var exc)
                && exc.TryGetProperty("fields", out var fields)
                && fields.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in fields.EnumerateObject()) set.Add(f.Name);
            }
            return set;
        }

        /// <summary>The committed <c>mbxmoods-schema.json</c> as embedded in this exe at build time.</summary>
        internal static string EmbeddedSchemaJson()
        {
            using var s = typeof(FieldPolicy).Assembly.GetManifestResourceStream("Truedat.mbxmoods-schema.json")
                ?? throw new InvalidOperationException("the committed mbxmoods-schema.json is not embedded in this build");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>Self-test seam: force a specific excluded set.</summary>
        internal static void OverrideForTest(IEnumerable<string> fields)
        {
            lock (_lock) { _excluded = new HashSet<string>(fields, StringComparer.Ordinal); }
        }
    }
}
