using System;
using System.Collections.Generic;
using System.IO;

namespace Truedat.Store
{
    internal sealed class ErrorRow { public string Path = ""; public string Error = ""; public string? FailedComponents; public string? Artist; public string? Title; }
    internal sealed class SkippedRow { public string Path = ""; public string Reason = ""; public string? Timestamp; }
    internal sealed class VerifyRow { public string Status = ""; public string Path = ""; public string? Detail; public string? BackfilledFields; }

    /// <summary>Pure readers for the ledgers beside a catalog. Keyed by header (tolerant-reader
    /// convention): an errors CSV written before 2026-07-30 lacks FailedComponents and still reads.
    /// A missing file is an empty ledger, not an error.</summary>
    internal static class LedgerImport
    {
        internal static List<ErrorRow> ReadErrors(string csvPath)
        {
            var rows = new List<ErrorRow>();
            foreach (var (header, cells) in CsvRows(csvPath, Program.ParseCsvLineForStore))
            {
                var path = Cell(header, cells, "FilePath");
                if (string.IsNullOrEmpty(path)) continue;
                rows.Add(new ErrorRow
                {
                    Path = path!, Error = Cell(header, cells, "Error") ?? "",
                    FailedComponents = NullIfEmpty(Cell(header, cells, "FailedComponents")),
                    Artist = NullIfEmpty(Cell(header, cells, "Artist")), Title = NullIfEmpty(Cell(header, cells, "Title")),
                });
            }
            return rows;
        }

        internal static List<SkippedRow> ReadSkipped(string csvPath)
        {
            var rows = new List<SkippedRow>();
            foreach (var (header, cells) in CsvRows(csvPath, Program.ParseCsvLineForStore))
            {
                var path = Cell(header, cells, "path");
                if (string.IsNullOrEmpty(path)) continue;
                rows.Add(new SkippedRow { Path = path!, Reason = Cell(header, cells, "reason") ?? "", Timestamp = NullIfEmpty(Cell(header, cells, "timestamp")) });
            }
            return rows;
        }

        internal static List<VerifyRow> ReadVerify(string csvPath)
        {
            var rows = new List<VerifyRow>();
            foreach (var (header, cells) in CsvRows(csvPath, line => line.Split('\t')))
            {
                var path = Cell(header, cells, "path");
                if (string.IsNullOrEmpty(path)) continue;
                rows.Add(new VerifyRow
                {
                    Status = Cell(header, cells, "status") ?? "", Path = path!,
                    Detail = NullIfEmpty(Cell(header, cells, "detail")), BackfilledFields = NullIfEmpty(Cell(header, cells, "backfilledFields")),
                });
            }
            return rows;
        }

        static IEnumerable<(Dictionary<string, int> Header, string[] Cells)> CsvRows(string path, Func<string, string[]> split)
        {
            if (!File.Exists(path)) yield break;
            Dictionary<string, int>? header = null;
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length == 0) continue;
                var line = raw.Length > 0 && raw[0] == '﻿' ? raw.Substring(1) : raw;
                var cells = split(line);
                if (header == null)
                {
                    header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < cells.Length; i++) header[cells[i].Trim()] = i;
                    continue;
                }
                yield return (header, cells);
            }
        }

        static string? Cell(Dictionary<string, int> header, string[] cells, string name)
            => header.TryGetValue(name, out var i) && i < cells.Length ? cells[i] : null;

        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
