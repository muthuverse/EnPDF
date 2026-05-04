using System.Collections.Generic;

namespace PdfExtractor.Engineering
{
    /// <summary>
    /// A single piece of text with its position on the page.
    /// The X/Y coordinates are in PDF user space (points from bottom-left).
    /// </summary>
    public class TextElement
    {
        public float X          { get; set; }
        public float Y          { get; set; }
        public float FontSize   { get; set; }
        public string FontName  { get; set; } = "";
        public string Text      { get; set; } = "";
        public int PageNumber   { get; set; }

        public override string ToString() =>
            $"[{X:F1},{Y:F1}] sz:{FontSize:F1} \"{Text}\"";
    }

    /// <summary>
    /// Standard engineering drawing title block fields.
    /// Populated by keyword-matching in the bottom region of the drawing.
    /// </summary>
    public class TitleBlock
    {
        public string PartNumber  { get; set; } = "";
        public string Revision    { get; set; } = "";
        public string Title       { get; set; } = "";
        public string DrawnBy     { get; set; } = "";
        public string CheckedBy   { get; set; } = "";
        public string ApprovedBy  { get; set; } = "";
        public string Material    { get; set; } = "";
        public string Scale       { get; set; } = "";
        public string Date        { get; set; } = "";
        public string Sheet       { get; set; } = "";
        public string Company     { get; set; } = "";

        /// <summary>Any field that did not match a known keyword.</summary>
        public Dictionary<string, string> OtherFields { get; set; } = new();
    }

    /// <summary>
    /// A measurement value extracted from the drawing, with optional tolerance.
    /// </summary>
    public class Dimension
    {
        public string RawText          { get; set; } = "";
        public double NominalValue     { get; set; }
        public string Unit             { get; set; } = "";
        public string UpperTolerance   { get; set; } = "";
        public string LowerTolerance   { get; set; } = "";
        public float X                 { get; set; }
        public float Y                 { get; set; }
        public int PageNumber          { get; set; }

        public override string ToString() =>
            $"{NominalValue}{Unit}  tol:{UpperTolerance}/{LowerTolerance}  pg{PageNumber} [{X:F0},{Y:F0}]";
    }

    /// <summary>
    /// A table detected by coordinate-based spatial grouping.
    /// TableType is inferred from header keywords (BOM, Revision, Tolerance, etc.)
    /// </summary>
    public class DrawingTable
    {
        public string TableType             { get; set; } = "Data Table";
        public List<string> Headers         { get; set; } = new();
        public List<List<string>> Rows      { get; set; } = new();

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{TableType}]");
            if (Headers.Count > 0)
                sb.AppendLine("  " + string.Join(" | ", Headers));
            foreach (var row in Rows)
                sb.AppendLine("  " + string.Join(" | ", row));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Complete output of the engineering drawing extractor.
    /// </summary>
    public class EngineeringExtractionResult
    {
        public TitleBlock TitleBlock                    { get; set; } = new();
        public List<Dimension> Dimensions               { get; set; } = new();
        public List<DrawingTable> Tables                { get; set; } = new();
        public List<string> Notes                       { get; set; } = new();
        public List<string> Tolerances                  { get; set; } = new();
        public List<TextElement> AllText                { get; set; } = new();
        public List<(byte[] Data, string Format)> Images { get; set; } = new();
        public Dictionary<string, string> Metadata     { get; set; } = new();
        public PdfDiagnosticReport Diagnostics          { get; set; } = new();
        public int PageCount                            { get; set; }
    }

    public class PdfDiagnosticReport
    {
        public string PdfType                    { get; set; } = "Unknown";
        public bool TextExtractable              { get; set; }
        public bool HasTextOperators             { get; set; }
        public bool HasImageXObjects             { get; set; }
        public bool HasObjectStreams             { get; set; }
        public bool HasXrefStream                { get; set; }
        public bool IsEncrypted                  { get; set; }
        public int PageCount                     { get; set; }
        public int TextElementCount              { get; set; }
        public int ImageCount                    { get; set; }
        public List<PdfFontInfo> Fonts           { get; set; } = new();
        public List<string> Issues               { get; set; } = new();
        public List<string> Recommendations      { get; set; } = new();
    }
}
