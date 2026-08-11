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
        // Built-in default when no schema file is found — kept in sync with the committed
        // mbxmoods-schema.json so the bpmHistogram cut still happens without the sidecar file.
        static readonly string[] DefaultExcluded = { "bpmHistogram" };

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

        static HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "mbxmoods-schema.json");
                if (!File.Exists(path))
                {
                    foreach (var f in DefaultExcluded) set.Add(f);
                    return set;
                }
                using var doc = JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                // Schema present is authoritative — even an empty excluded block means "cut
                // nothing" (the operator un-cut a field on purpose). Only a MISSING file falls
                // back to the built-in default.
                if (doc.RootElement.TryGetProperty("excluded", out var exc)
                    && exc.TryGetProperty("fields", out var fields)
                    && fields.ValueKind == JsonValueKind.Object)
                {
                    foreach (var f in fields.EnumerateObject()) set.Add(f.Name);
                }
            }
            catch
            {
                // Unreadable/corrupt schema: fall back to the safe built-in default rather than
                // silently emitting a field the operator meant to cut.
                set.Clear();
                foreach (var f in DefaultExcluded) set.Add(f);
            }
            return set;
        }

        /// <summary>Self-test seam: force a specific excluded set.</summary>
        internal static void OverrideForTest(IEnumerable<string> fields)
        {
            lock (_lock) { _excluded = new HashSet<string>(fields, StringComparer.Ordinal); }
        }
    }
}
