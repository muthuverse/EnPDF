using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfExtractor.Engineering
{
    /// <summary>
    /// Analyzes positioned TextElement data to extract engineering-drawing-specific
    /// content: title block, dimensions, tolerances, notes, and tables.
    ///
    /// All spatial reasoning is coordinate-based — no whitespace heuristics.
    /// </summary>
    public class EngineeringDrawingAnalyzer
    {
        // ─── Keyword lists ─────────────────────────────────────────────────────

        private static readonly string[] TitleBlockLabels =
        {
            "PART NO", "PART NUMBER", "DRG NO", "DWG NO",
            "DRAWING NO", "DRAWING NUMBER", "DRAWING TITLE",
            "REVISION", "REV",
            "TITLE", "NAME",
            "DRAWN", "DRAWN BY", "DRN BY",
            "CHECKED", "CHECKED BY", "CHK BY",
            "APPROVED", "APPROVED BY", "APP BY",
            "MATERIAL", "MAT",
            "SCALE",
            "DATE",
            "SHEET", "SHEET NO",
            "WEIGHT", "MASS",
            "FINISH", "SURFACE FINISH",
            "TOLERANCE", "GENERAL TOLERANCE",
            "COMPANY", "ORGANISATION", "ORGANIZATION",
            "PROJECT", "PROJECT NO", "CONTRACT",
            "SPECIFICATION", "SPEC"
        };

        private static readonly string[] NoteKeywords =
            { "NOTE", "NOTES", "GENERAL NOTES", "UNLESS OTHERWISE",
              "ALL DIMENSIONS", "ALL RADII", "BREAK ALL", "DEBURR" };

        // Dimension: optional minus, digits, optional decimal, optional unit, optional tolerance
        private static readonly Regex DimRegex = new(
            @"^-?(\d+(?:\.\d+)?)\s*(mm|cm|m|in|inch|ft|""|°|deg)?(?:\s*[±\+\-]\s*\d+(?:\.\d+)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TolRegex = new(
            @"([±\+]\s*\d+(?:\.\d+)?)\s*(?:/\s*(-\s*\d+(?:\.\d+)?))?",
            RegexOptions.Compiled);

        // ISO fit tolerance symbols
        private static readonly Regex FitRegex = new(
            @"\b([Hh][5-9]|[fghjk][5-9]|[Ee][6-9])\b",
            RegexOptions.Compiled);

        // ─── Public entry point ────────────────────────────────────────────────

        public EngineeringExtractionResult Analyze(
            List<TextElement> allElements,
            List<(byte[] Data, string Format)> images,
            Dictionary<string, string> metadata,
            int pageCount)
        {
            var result = new EngineeringExtractionResult
            {
                AllText   = allElements,
                Images    = images,
                Metadata  = metadata,
                PageCount = pageCount
            };

            if (allElements == null || allElements.Count == 0) return result;

            // Process page by page
            foreach (var grp in allElements.GroupBy(e => e.PageNumber))
            {
                var elems = grp.ToList();

                var titleElems = TitleBlockRegion(elems);
                ParseTitleBlock(titleElems, result.TitleBlock);

                result.Dimensions.AddRange(ExtractDimensions(elems));
                result.Notes.AddRange(ExtractNotes(elems));
                result.Tables.AddRange(DetectTables(elems));
                result.Tolerances.AddRange(ExtractTolerances(elems));
            }

            // Deduplicate
            result.Tolerances = result.Tolerances.Distinct().ToList();

            return result;
        }

        // ─── Title block ───────────────────────────────────────────────────────

        /// <summary>
        /// Title block occupies the bottom 25 % of the page height.
        /// For portrait A4/A3 drawings this maps to the standard title block area.
        /// </summary>
        private List<TextElement> TitleBlockRegion(List<TextElement> elems)
        {
            if (elems.Count == 0) return new();
            float minY = elems.Min(e => e.Y);
            float maxY = elems.Max(e => e.Y);
            float threshold = minY + (maxY - minY) * 0.25f;
            return elems.Where(e => e.Y <= threshold).ToList();
        }

        private void ParseTitleBlock(List<TextElement> elems, TitleBlock tb)
        {
            if (elems.Count == 0) return;

            var rows = GroupRows(elems, yTol: 5f);

            // Two-pass: first collect label rows, then value rows immediately below
            string pendingLabel = "";
            foreach (var row in rows)
            {
                string upper = RowText(row).ToUpperInvariant();
                string raw   = RowText(row);

                string matchedLabel = TitleBlockLabels.FirstOrDefault(
                    l => upper.Contains(l));

                if (matchedLabel != null)
                {
                    pendingLabel = matchedLabel;
                    // Value might follow the label on the same row
                    string after = After(raw, matchedLabel);
                    if (!string.IsNullOrWhiteSpace(after))
                    {
                        Assign(tb, matchedLabel, after);
                        pendingLabel = "";
                    }
                }
                else if (!string.IsNullOrEmpty(pendingLabel) &&
                         !string.IsNullOrWhiteSpace(raw))
                {
                    Assign(tb, pendingLabel, raw.Trim());
                    pendingLabel = "";
                }
            }
        }

        private static string After(string text, string label)
        {
            int idx = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            string rest = text[(idx + label.Length)..].TrimStart(':', ' ');
            return rest;
        }

        private static void Assign(TitleBlock tb, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            switch (label.ToUpperInvariant())
            {
                case "PART NO":
                case "PART NUMBER":
                case "DRG NO":
                case "DWG NO":
                case "DRAWING NO":
                case "DRAWING NUMBER":
                    if (string.IsNullOrEmpty(tb.PartNumber)) tb.PartNumber = value; break;
                case "REVISION":
                case "REV":
                    if (string.IsNullOrEmpty(tb.Revision))   tb.Revision  = value; break;
                case "TITLE":
                case "NAME":
                case "DRAWING TITLE":
                    if (string.IsNullOrEmpty(tb.Title))      tb.Title     = value; break;
                case "DRAWN":
                case "DRAWN BY":
                case "DRN BY":
                    if (string.IsNullOrEmpty(tb.DrawnBy))    tb.DrawnBy   = value; break;
                case "CHECKED":
                case "CHECKED BY":
                case "CHK BY":
                    if (string.IsNullOrEmpty(tb.CheckedBy))  tb.CheckedBy = value; break;
                case "APPROVED":
                case "APPROVED BY":
                case "APP BY":
                    if (string.IsNullOrEmpty(tb.ApprovedBy)) tb.ApprovedBy = value; break;
                case "MATERIAL":
                case "MAT":
                    if (string.IsNullOrEmpty(tb.Material))   tb.Material  = value; break;
                case "SCALE":
                    if (string.IsNullOrEmpty(tb.Scale))      tb.Scale     = value; break;
                case "DATE":
                    if (string.IsNullOrEmpty(tb.Date))       tb.Date      = value; break;
                case "SHEET":
                case "SHEET NO":
                    if (string.IsNullOrEmpty(tb.Sheet))      tb.Sheet     = value; break;
                case "COMPANY":
                case "ORGANISATION":
                case "ORGANIZATION":
                    if (string.IsNullOrEmpty(tb.Company))    tb.Company   = value; break;
                default:
                    tb.OtherFields.TryAdd(label, value); break;
            }
        }

        // ─── Dimensions ────────────────────────────────────────────────────────

        private List<Dimension> ExtractDimensions(List<TextElement> elems)
        {
            var dims = new List<Dimension>();
            foreach (var e in elems)
            {
                string text = e.Text.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                // Skip pure title-block labels
                if (TitleBlockLabels.Any(l => text.Equals(l, StringComparison.OrdinalIgnoreCase))) continue;

                var m = DimRegex.Match(text);
                if (!m.Success) continue;
                if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out double val)) continue;
                if (val == 0 || val > 100_000) continue; // sanity bounds

                var dim = new Dimension
                {
                    RawText      = text,
                    NominalValue = val,
                    Unit         = m.Groups[2].Success ? m.Groups[2].Value.ToLower() : "",
                    X = e.X, Y = e.Y, PageNumber = e.PageNumber
                };

                var tol = TolRegex.Match(text);
                if (tol.Success)
                {
                    dim.UpperTolerance = tol.Groups[1].Value.Trim();
                    dim.LowerTolerance = tol.Groups[2].Success
                        ? tol.Groups[2].Value.Trim()
                        : NegateSign(dim.UpperTolerance);
                }

                dims.Add(dim);
            }
            return dims;
        }

        private static string NegateSign(string s)
        {
            if (s.StartsWith("+")) return "-" + s[1..];
            if (s.StartsWith("±")) return s;
            return s;
        }

        // ─── Notes ─────────────────────────────────────────────────────────────

        private List<string> ExtractNotes(List<TextElement> elems)
        {
            var notes   = new List<string>();
            var rows    = GroupRows(elems, yTol: 4f);
            bool active = false;
            var numberedNote = new Regex(@"^\d+[\.\)]\s+", RegexOptions.Compiled);

            foreach (var row in rows)
            {
                string text = RowText(row).Trim();
                if (string.IsNullOrEmpty(text)) continue;

                bool isNoteHeader = NoteKeywords.Any(
                    k => text.StartsWith(k, StringComparison.OrdinalIgnoreCase));

                if (isNoteHeader)
                {
                    active = true;
                    // Add text if there is content after the keyword header
                    string matchedKw = NoteKeywords.First(
                        kw => text.StartsWith(kw, StringComparison.OrdinalIgnoreCase));
                    if (text.Length > matchedKw.Length)
                        notes.Add(text);
                    continue;
                }
                if (active && (numberedNote.IsMatch(text) ||
                               text.StartsWith("•") || text.StartsWith("-")))
                {
                    notes.Add(text);
                }
            }
            return notes;
        }

        // ─── Tables ────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects tables by finding text elements that share consistent X column positions
        /// across multiple rows. Requires ≥ 2 columns and ≥ 2 data rows.
        /// </summary>
        private List<DrawingTable> DetectTables(List<TextElement> elems)
        {
            var tables = new List<DrawingTable>();
            if (elems.Count < 6) return tables;

            var rows = GroupRows(elems, yTol: 4f);
            if (rows.Count < 3) return tables;

            // Collect all X positions and find those that appear in many rows
            var xCounts = new Dictionary<int, int>(); // rounded X → row count
            foreach (var row in rows)
                {
                    // Count each rounded column at most once per row.
                    var rowXs = new HashSet<int>();
                    foreach (var e in row)
                        rowXs.Add((int)(Math.Round(e.X / 8.0) * 8)); // 8-pt grid

                    foreach (int rx in rowXs)
                        xCounts[rx] = xCounts.GetValueOrDefault(rx) + 1;
                }

            int minRows = Math.Max(2, rows.Count / 3);
            var colXs = xCounts
                .Where(kvp => kvp.Value >= minRows)
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => (float)kvp.Key)
                .ToList();

            if (colXs.Count < 2) return tables;

            // Build candidate table rows
            var tableRows  = new List<List<string>>();
            var currentRun = new List<List<string>>();

            foreach (var row in rows)
            {
                var cells = AssignCells(row, colXs, xTol: 16f);
                int filled = cells.Count(c => !string.IsNullOrWhiteSpace(c));

                if (filled >= 2)
                {
                    currentRun.Add(cells);
                }
                else
                {
                    if (currentRun.Count >= 2)
                        tables.Add(BuildTable(currentRun));
                    currentRun.Clear();
                }
            }
            if (currentRun.Count >= 2)
                tables.Add(BuildTable(currentRun));

            return tables;
        }

        private List<string> AssignCells(
            List<TextElement> row, List<float> colXs, float xTol)
        {
            var cells = new string[colXs.Count];
            for (int i = 0; i < cells.Length; i++) cells[i] = "";
            foreach (var e in row)
            {
                int col = NearestCol(e.X, colXs, xTol);
                if (col >= 0)
                    cells[col] = (cells[col] + " " + e.Text).Trim();
            }
            return cells.ToList();
        }

        private static int NearestCol(float x, List<float> cols, float tol)
        {
            for (int i = 0; i < cols.Count; i++)
                if (Math.Abs(x - cols[i]) <= tol) return i;
            return -1;
        }

        private static DrawingTable BuildTable(List<List<string>> rows)
        {
            var t = new DrawingTable { Headers = rows[0], Rows = rows.Skip(1).ToList() };
            string h = string.Join(" ", t.Headers).ToUpperInvariant();

            if (h.Contains("ITEM") || h.Contains("QTY") || h.Contains("PART"))
                t.TableType = "Bill of Materials";
            else if (h.Contains("REV") && (h.Contains("DATE") || h.Contains("DESCRIPTION")))
                t.TableType = "Revision History";
            else if (h.Contains("SYMBOL") || h.Contains("TOL"))
                t.TableType = "Tolerance Table";
            else
                t.TableType = "Data Table";

            return t;
        }

        // ─── Tolerances ────────────────────────────────────────────────────────

        private List<string> ExtractTolerances(List<TextElement> elems)
        {
            var result = new List<string>();
            foreach (var e in elems)
            {
                string t = e.Text.Trim();
                if (t.Contains('±') || t.Contains("+/-") || FitRegex.IsMatch(t) ||
                    t.ToUpperInvariant().Contains("TOL"))
                    result.Add(t);
            }
            return result;
        }

        // ─── Spatial helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Group TextElements into horizontal rows using Y-coordinate proximity.
        /// Returns rows ordered top → bottom (descending Y in PDF coordinates).
        /// </summary>
        private static List<List<TextElement>> GroupRows(
            List<TextElement> elems, float yTol)
        {
            if (elems.Count == 0) return new();
            var sorted = elems.OrderByDescending(e => e.Y).ThenBy(e => e.X).ToList();
            var rows   = new List<List<TextElement>>();
            var cur    = new List<TextElement> { sorted[0] };
            float curY = sorted[0].Y;

            for (int i = 1; i < sorted.Count; i++)
            {
                if (Math.Abs(sorted[i].Y - curY) <= yTol)
                    cur.Add(sorted[i]);
                else
                {
                    rows.Add(cur.OrderBy(e => e.X).ToList());
                    cur  = new List<TextElement> { sorted[i] };
                    curY = sorted[i].Y;
                }
            }
            if (cur.Count > 0) rows.Add(cur.OrderBy(e => e.X).ToList());
            return rows;
        }

        private static string RowText(List<TextElement> row) =>
            string.Join(" ", row.Select(e => e.Text.Trim()));

        // ─── Result formatter ──────────────────────────────────────────────────

        public static string Format(EngineeringExtractionResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════╗");
            sb.AppendLine("║      ENGINEERING DRAWING — EXTRACTION RESULT         ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════╝");
            sb.AppendLine($"  Pages        : {r.PageCount}");
            sb.AppendLine($"  Text elements: {r.AllText.Count}");
            sb.AppendLine($"  Images       : {r.Images.Count}");
            sb.AppendLine();

            // Metadata
            if (r.Metadata.Count > 0)
            {
                sb.AppendLine("── DOCUMENT METADATA ───────────────────────────────────");
                foreach (var kv in r.Metadata)
                    sb.AppendLine($"  {kv.Key,-20} {kv.Value}");
                sb.AppendLine();
            }

            // Diagnostic mode
            if (r.Diagnostics != null)
            {
                var d = r.Diagnostics;
                sb.AppendLine("── ENPDF DIAGNOSTIC REPORT ─────────────────────────────");
                sb.AppendLine($"  PDF Type          : {d.PdfType}");
                sb.AppendLine($"  Text Extractable  : {(d.TextExtractable ? "YES" : "NO")}");
                sb.AppendLine($"  Text Operators    : {(d.HasTextOperators ? "YES" : "NO")}");
                sb.AppendLine($"  Image XObjects    : {(d.HasImageXObjects ? "YES" : "NO")}");
                sb.AppendLine($"  Object Streams    : {(d.HasObjectStreams ? "YES" : "NO")}");
                sb.AppendLine($"  XRef Stream       : {(d.HasXrefStream ? "YES" : "NO")}");
                sb.AppendLine($"  Encrypted         : {(d.IsEncrypted ? "YES" : "NO")}");

                if (d.Fonts.Count > 0)
                {
                    sb.AppendLine("  Fonts:");
                    foreach (var f in d.Fonts)
                    {
                        sb.AppendLine(
                            $"    /{f.ResourceName,-6} obj {f.ObjectId,-4} {f.Subtype,-7} {f.Encoding,-14} " +
                            $"base={f.BaseFont} embedded={(f.IsEmbedded ? "YES" : "NO")} " +
                            $"ToUnicode={(f.HasToUnicode ? $"YES ({f.ToUnicodeEntries})" : "NO")} " +
                            $"dup={(f.DuplicateToUnicodeSourceCodes > 0 ? f.DuplicateToUnicodeSourceCodes : 0)}");
                    }
                }

                if (d.Issues.Count > 0)
                {
                    sb.AppendLine("  Issues:");
                    foreach (string issue in d.Issues)
                        sb.AppendLine($"    - {issue}");
                }

                if (d.Recommendations.Count > 0)
                {
                    sb.AppendLine("  Recommendations:");
                    foreach (string recommendation in d.Recommendations)
                        sb.AppendLine($"    - {recommendation}");
                }

                sb.AppendLine();
            }

            // Title block
            sb.AppendLine("── TITLE BLOCK ─────────────────────────────────────────");
            var tb = r.TitleBlock;
            void Row(string label, string val) {
                if (!string.IsNullOrEmpty(val)) sb.AppendLine($"  {label,-20} {val}");
            }
            Row("Part Number",  tb.PartNumber);
            Row("Revision",     tb.Revision);
            Row("Title",        tb.Title);
            Row("Drawn By",     tb.DrawnBy);
            Row("Checked By",   tb.CheckedBy);
            Row("Approved By",  tb.ApprovedBy);
            Row("Material",     tb.Material);
            Row("Scale",        tb.Scale);
            Row("Date",         tb.Date);
            Row("Sheet",        tb.Sheet);
            Row("Company",      tb.Company);
            foreach (var kv in tb.OtherFields) Row(kv.Key, kv.Value);
            sb.AppendLine();

            // Dimensions
            sb.AppendLine($"── DIMENSIONS  ({r.Dimensions.Count} found) ────────────────────────");
            foreach (var d in r.Dimensions.Take(50))
            {
                string tol = string.IsNullOrEmpty(d.UpperTolerance) ? ""
                    : $"  tol: {d.UpperTolerance} / {d.LowerTolerance}";
                sb.AppendLine($"  {d.NominalValue,10}{d.Unit,-4}{tol}   pg{d.PageNumber} [{d.X:F0},{d.Y:F0}]");
            }
            if (r.Dimensions.Count > 50) sb.AppendLine($"  ... +{r.Dimensions.Count - 50} more");
            sb.AppendLine();

            // Tables
            sb.AppendLine($"── TABLES  ({r.Tables.Count} found) ────────────────────────────────");
            foreach (var t in r.Tables)
            {
                sb.AppendLine($"  [{t.TableType}]");
                if (t.Headers.Count > 0)
                    sb.AppendLine("    " + string.Join("  |  ", t.Headers));
                foreach (var row in t.Rows.Take(15))
                    sb.AppendLine("    " + string.Join("  |  ", row));
                if (t.Rows.Count > 15) sb.AppendLine($"    ... +{t.Rows.Count - 15} more rows");
                sb.AppendLine();
            }

            // Notes
            if (r.Notes.Count > 0)
            {
                sb.AppendLine($"── NOTES  ({r.Notes.Count} found) ──────────────────────────────");
                foreach (var n in r.Notes) sb.AppendLine($"  {n}");
                sb.AppendLine();
            }

            // Tolerances
            if (r.Tolerances.Count > 0)
            {
                sb.AppendLine($"── TOLERANCES  ({r.Tolerances.Count} found) ────────────────────");
                foreach (var t in r.Tolerances) sb.AppendLine($"  {t}");
                sb.AppendLine();
            }

            // Images
            sb.AppendLine($"── IMAGES  ({r.Images.Count} found) ─────────────────────────────");
            for (int i = 0; i < r.Images.Count; i++)
                sb.AppendLine($"  image_{i}.{r.Images[i].Format}  ({r.Images[i].Data.Length:N0} bytes)");
            sb.AppendLine();

            // Full text dump
            sb.AppendLine("── ALL TEXT  (top → bottom, left → right) ──────────────");
            var sorted = r.AllText
                .OrderByDescending(e => e.Y).ThenBy(e => e.X)
                .ToList();
            foreach (var e in sorted)
                sb.AppendLine($"  [{e.X,6:F0},{e.Y,6:F0}]  sz{e.FontSize:F1}  \"{e.Text}\"");

            return sb.ToString();
        }
    }
}
