using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PdfExtractor.Engineering;

namespace PdfExtractor.Engineering
{
    /// <summary>
    /// Orchestrates PDF parsing -> decompression -> text extraction -> analysis.
    /// This is the single public API consumers need.
    /// </summary>
    public class EngineeringPdfExtractor
    {
        private readonly PdfParser _parser;

        public EngineeringPdfExtractor(string filePath)
        {
            _parser = new PdfParser(filePath);
        }

        public EngineeringExtractionResult Extract()
        {
            var allText = new List<TextElement>();
            var images = new List<(byte[], string)>();
            var metadata = new Dictionary<string, string>();
            var fonts = new List<PdfFontInfo>();
            bool hasTextOperators = false;

            // Metadata comes from trailer /Info, not from catalog /Root.
            int infoId = _parser.GetInfoObjectId();
            if (infoId > 0)
            {
                var info = _parser.GetObject(infoId);
                if (info != null)
                    foreach (var kv in info.Dict)
                        metadata[kv.Key] = kv.Value.Trim('(', ')');
            }

            // Page-by-page extraction
            var pageIds = _parser.GetPageObjectIds();

            for (int pageNum = 0; pageNum < pageIds.Count; pageNum++)
            {
                int pageId = pageIds[pageNum];
                var page = _parser.GetObject(pageId);
                if (page == null) continue;

                var fontDecoders = BuildFontDecoders(page, pageId, fonts);

                // Text: resolve /Contents recursively (handles wrapper objects like "6 0 obj [7 0 R] endobj")
                foreach (int contentId in ResolveContentStreamObjectIds(page))
                {
                    var contentObj = _parser.GetObject(contentId);
                    if (contentObj?.RawStream == null) continue;

                    byte[] raw = PdfStream.Decompress(
                        contentObj.RawStream, contentObj.Get("/Filter"));

                    string contentText = Encoding.Latin1.GetString(raw);
                    hasTextOperators |= Regex.IsMatch(contentText, @"\b(Tj|TJ|')\b|""");

                    var elems = PdfStream.ExtractTextElements(raw, pageNum + 1, fontDecoders);
                    allText.AddRange(elems);
                }

                // Images: traverse /Resources -> /XObject
                ExtractImages(page, images, pageId);
            }

            // Engineering analysis
            var analyzer = new EngineeringDrawingAnalyzer();
            var result = analyzer.Analyze(allText, images, metadata, pageIds.Count);
            result.Diagnostics = BuildDiagnostic(pageIds.Count, allText.Count, images.Count, fonts, hasTextOperators);
            return result;
        }

        public PdfDiagnosticReport Diagnose()
        {
            var fonts = new List<PdfFontInfo>();
            int imageCount = 0;
            bool hasTextOperators = false;
            int pageCount = 0;

            var pageIds = _parser.GetPageObjectIds();
            pageCount = pageIds.Count;
            foreach (int pageId in pageIds)
            {
                var page = _parser.GetObject(pageId);
                if (page == null) continue;

                BuildFontDecoders(page, pageId, fonts);

                foreach (int contentId in ResolveContentStreamObjectIds(page))
                {
                    var contentObj = _parser.GetObject(contentId);
                    if (contentObj?.RawStream == null) continue;
                    byte[] raw = PdfStream.Decompress(contentObj.RawStream, contentObj.Get("/Filter"));
                    string contentText = Encoding.Latin1.GetString(raw);
                    hasTextOperators |= Regex.IsMatch(contentText, @"\b(Tj|TJ|')\b|""");
                }

                var pageImages = new List<(byte[], string)>();
                ExtractImages(page, pageImages, pageId);
                imageCount += pageImages.Count;
            }

            return BuildDiagnostic(pageCount, 0, imageCount, fonts, hasTextOperators);
        }

        private List<int> ResolveContentStreamObjectIds(PdfObject page)
        {
            var result = new List<int>();
            var visited = new HashSet<int>();
            var queue = new Queue<int>();

            string contentsVal = page.Get("/Contents");
            foreach (int id in ParseRefs(contentsVal))
                queue.Enqueue(id);

            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                if (id <= 0 || !visited.Add(id)) continue;

                var obj = _parser.GetObject(id);
                if (obj == null) continue;

                // Real content stream object
                if (obj.RawStream != null && obj.RawStream.Length > 0)
                {
                    result.Add(id);
                    continue;
                }

                // Wrapper objects can chain references through /Contents or a bare array body.
                foreach (int nested in ParseRefs(obj.Get("/Contents")))
                    queue.Enqueue(nested);
                foreach (int nested in ParseRefs(obj.Content))
                    queue.Enqueue(nested);
            }

            return result;
        }

        private void ExtractImages(PdfObject page, List<(byte[], string)> images, int pageId)
        {
            var resources = ResolvePageResources(page, pageId);
            if (resources == null) return;

            ExtractImagesFromResources(resources, images, new HashSet<int>());
        }

        private PdfObject ResolvePageResources(PdfObject page, int pageId)
        {
            string resVal = page.Get("/Resources");
            var resources = ResolveResourceObject(resVal);
            if (resources != null) return resources;

            // Resource inheritance via /Parent chain
            int current = pageId;
            var visited = new HashSet<int>();
            while (current > 0 && visited.Add(current))
            {
                var node = _parser.GetObject(current);
                if (node == null) break;

                string nodeRes = node.Get("/Resources");
                var resolved = ResolveResourceObject(nodeRes);
                if (resolved != null) return resolved;

                current = ParseRef(node.Get("/Parent"));
            }

            return null;
        }

        private PdfObject ResolveResourceObject(string resVal)
        {
            if (string.IsNullOrEmpty(resVal)) return null;

            if (resVal.TrimStart().StartsWith("<<"))
            {
                var inline = new PdfObject();
                PdfParser.ParseDictionaryInto(inline.Dict, resVal);
                return inline;
            }

            int resId = ParseRef(resVal);
            return resId > 0 ? _parser.GetObject(resId) : null;
        }

        private Dictionary<string, PdfFontDecoder> BuildFontDecoders(
            PdfObject page,
            int pageId,
            List<PdfFontInfo> allFonts)
        {
            var decoders = new Dictionary<string, PdfFontDecoder>();
            var resources = ResolvePageResources(page, pageId);
            if (resources == null) return decoders;

            var fontDict = ResolveDictionary(resources.Get("/Font"));
            if (fontDict.Count == 0) return decoders;

            foreach (var kv in fontDict)
            {
                string resourceName = kv.Key.TrimStart('/');
                int fontId = ParseRef(kv.Value);
                if (fontId <= 0) continue;

                var fontObj = _parser.GetObject(fontId);
                if (fontObj == null) continue;

                var info = AnalyzeFont(resourceName, fontId, fontObj);
                if (!allFonts.Any(f => f.ObjectId == info.ObjectId && f.ResourceName == info.ResourceName))
                    allFonts.Add(info);

                byte[] toUnicode = GetToUnicodeStream(info);
                decoders[resourceName] = PdfFontDecoder.Create(info, toUnicode);
            }

            return decoders;
        }

        private Dictionary<string, string> ResolveDictionary(string value)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(value)) return dict;

            if (value.TrimStart().StartsWith("<<"))
            {
                PdfParser.ParseDictionaryInto(dict, value);
                return dict;
            }

            int id = ParseRef(value);
            if (id > 0)
            {
                var obj = _parser.GetObject(id);
                if (obj != null) return obj.Dict;
            }

            return dict;
        }

        private PdfFontInfo AnalyzeFont(string resourceName, int fontId, PdfObject fontObj)
        {
            string subtype = fontObj.Get("/Subtype") ?? "";
            string encoding = fontObj.Get("/Encoding") ?? "";
            string baseFont = (fontObj.Get("/BaseFont") ?? "").TrimStart('/');
            int toUnicodeId = ParseRef(fontObj.Get("/ToUnicode"));

            var descendantIds = ParseRefs(fontObj.Get("/DescendantFonts"));
            bool isCid = subtype == "/Type0" || encoding.Contains("Identity-H");
            bool embedded = HasEmbeddedFont(fontObj);

            foreach (int descendantId in descendantIds)
            {
                var descendant = _parser.GetObject(descendantId);
                if (descendant == null) continue;
                string descSubtype = descendant.Get("/Subtype") ?? "";
                isCid |= descSubtype.StartsWith("/CIDFont", StringComparison.Ordinal);
                embedded |= HasEmbeddedFont(descendant);
            }

            return new PdfFontInfo
            {
                ResourceName = resourceName,
                ObjectId = fontId,
                BaseFont = baseFont,
                Subtype = subtype,
                Encoding = encoding,
                IsCidFont = isCid,
                IsEmbedded = embedded,
                HasToUnicode = toUnicodeId > 0,
                ToUnicodeObjectId = toUnicodeId
            };
        }

        private bool HasEmbeddedFont(PdfObject fontObj)
        {
            int descriptorId = ParseRef(fontObj.Get("/FontDescriptor"));
            if (descriptorId <= 0) return false;

            var descriptor = _parser.GetObject(descriptorId);
            if (descriptor == null) return false;

            return !string.IsNullOrEmpty(descriptor.Get("/FontFile")) ||
                   !string.IsNullOrEmpty(descriptor.Get("/FontFile2")) ||
                   !string.IsNullOrEmpty(descriptor.Get("/FontFile3"));
        }

        private byte[] GetToUnicodeStream(PdfFontInfo info)
        {
            if (info.ToUnicodeObjectId <= 0) return Array.Empty<byte>();
            var cmapObj = _parser.GetObject(info.ToUnicodeObjectId);
            if (cmapObj?.RawStream == null) return Array.Empty<byte>();
            return PdfStream.Decompress(cmapObj.RawStream, cmapObj.Get("/Filter"));
        }

        private PdfDiagnosticReport BuildDiagnostic(
            int pageCount,
            int textElementCount,
            int imageCount,
            List<PdfFontInfo> fonts,
            bool hasTextOperators)
        {
            bool hasImages = imageCount > 0;
            var report = new PdfDiagnosticReport
            {
                PageCount = pageCount,
                TextElementCount = textElementCount,
                ImageCount = imageCount,
                HasTextOperators = hasTextOperators,
                HasImageXObjects = hasImages,
                HasObjectStreams = _parser.ContainsAscii("/ObjStm"),
                HasXrefStream = _parser.ContainsAscii("/Type /XRef"),
                IsEncrypted = _parser.ContainsAscii("/Encrypt"),
                Fonts = fonts
                    .GroupBy(f => new { f.ObjectId, f.ResourceName })
                    .Select(g => g.First())
                    .OrderBy(f => f.ObjectId)
                    .ToList()
            };

            report.TextExtractable = hasTextOperators;
            report.PdfType = hasTextOperators && hasImages ? "Hybrid native PDF"
                : hasTextOperators ? "Native text/vector PDF"
                : hasImages ? "Image-based PDF"
                : "Unknown or empty PDF";

            if (report.IsEncrypted)
            {
                report.Issues.Add("PDF is encrypted or protected.");
                report.Recommendations.Add("Provide an unprotected engineering PDF before extraction.");
            }

            if (!hasTextOperators && hasImages)
            {
                report.Issues.Add("No PDF text-showing operators were found; page content appears image-based.");
                report.Recommendations.Add("Use CATIA Save As PDF/PDF-A for native extraction. OCR is a separate phase.");
            }
            else if (!hasTextOperators)
            {
                report.Issues.Add("No PDF text-showing operators were found.");
            }

            foreach (var font in report.Fonts.Where(f => f.IsCidFont && !f.HasToUnicode))
            {
                report.Issues.Add($"CID font {font.BaseFont} ({font.ResourceName}) has no /ToUnicode map.");
                report.Recommendations.Add("Export as PDF/A or provide PDFs with embedded ToUnicode maps.");
            }

            foreach (var font in report.Fonts.Where(f => f.DuplicateToUnicodeSourceCodes > 0))
            {
                report.Issues.Add(
                    $"Font {font.BaseFont} ({font.ResourceName}) has {font.DuplicateToUnicodeSourceCodes} duplicate /ToUnicode source code mappings.");
                report.Recommendations.Add("Regenerate/export the PDF with a font/export setting that writes unique ToUnicode mappings for every engineering symbol.");
            }

            if (report.Fonts.Any(f => f.IsCidFont && f.HasToUnicode))
                report.Recommendations.Add("CID /Identity-H fonts are present; extraction must apply /ToUnicode CMap.");

            if (report.HasObjectStreams)
                report.Recommendations.Add("Object streams detected; full /ObjStm support is recommended for production coverage.");

            if (report.Issues.Count == 0)
                report.Issues.Add("No blocking structural issue detected by diagnostic mode.");

            report.Recommendations = report.Recommendations.Distinct().ToList();
            return report;
        }

        private void ExtractImagesFromResources(PdfObject resources, List<(byte[], string)> images, HashSet<int> visitedXObjects)
        {
            string xobjVal = resources.Get("/XObject");
            if (string.IsNullOrEmpty(xobjVal)) return;

            var xobjDict = new Dictionary<string, string>();
            if (xobjVal.TrimStart().StartsWith("<<"))
            {
                PdfParser.ParseDictionaryInto(xobjDict, xobjVal);
            }
            else
            {
                int xobjId = ParseRef(xobjVal);
                if (xobjId > 0)
                {
                    var xobj = _parser.GetObject(xobjId);
                    if (xobj != null) xobjDict = xobj.Dict;
                }
            }

            foreach (var kv in xobjDict)
            {
                int imgId = ParseRef(kv.Value);
                if (imgId <= 0) continue;
                if (!visitedXObjects.Add(imgId)) continue;

                var imgObj = _parser.GetObject(imgId);
                if (imgObj == null) continue;

                string subtype = imgObj.Get("/Subtype") ?? "";
                if (subtype == "/Image")
                {
                    if (imgObj.RawStream == null) continue;

                    string filter = imgObj.Get("/Filter") ?? "";
                    string format = DetectImageFormat(filter, imgObj.RawStream);

                    // For image formats that are already natively encoded (JPEG/JP2),
                    // return raw stream. For others, try decompression.
                    byte[] imgData = filter.Contains("/DCTDecode") || filter.Contains("/JPXDecode")
                        ? imgObj.RawStream
                        : PdfStream.Decompress(imgObj.RawStream, filter);

                    images.Add((imgData, format));
                }
                else if (subtype == "/Form")
                {
                    var formResources = ResolveResourceObject(imgObj.Get("/Resources"));
                    if (formResources != null)
                        ExtractImagesFromResources(formResources, images, visitedXObjects);
                }

            }
        }

        private static string DetectImageFormat(string filter, byte[] data)
        {
            if (filter.Contains("/DCTDecode")) return "jpg";
            if (filter.Contains("/JPXDecode")) return "jp2";
            if (filter.Contains("/JBIG2Decode")) return "jbig2";

            // Magic bytes
            if (data.Length >= 4 &&
                data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
                return "png";
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
                return "jpg";

            return "bin";
        }

        private static int ParseRef(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = Regex.Match(s, @"(\d+)\s+\d+\s+R");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static List<int> ParseRefs(string s)
        {
            var ids = new List<int>();
            if (string.IsNullOrEmpty(s)) return ids;

            if (s.TrimStart().StartsWith("["))
            {
                foreach (Match m in Regex.Matches(s, @"(\d+)\s+\d+\s+R"))
                    ids.Add(int.Parse(m.Groups[1].Value));
            }
            else
            {
                int id = ParseRef(s);
                if (id > 0) ids.Add(id);
            }
            return ids;
        }
    }
}

class Program
{
    [System.STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            RunCommandLine(args);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new ExtractorForm());
    }

    private static void RunCommandLine(string[] args)
    {
        if (args.Any(a => a.Equals("--create-character-test", StringComparison.OrdinalIgnoreCase)))
        {
            string testPdfPath = args.FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.OrdinalIgnoreCase)) ?? "";

            if (string.IsNullOrWhiteSpace(testPdfPath))
            {
                testPdfPath = Path.Combine(
                    AppContext.BaseDirectory,
                    PdfExtractor.Engineering.EngineeringCharacterTestPdfGenerator.DefaultFileName);
            }

            string createdPath = PdfExtractor.Engineering.EngineeringCharacterTestPdfGenerator.Create(testPdfPath);
            Console.WriteLine(createdPath);
            return;
        }

        string filePath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine("Usage: EngineeringPdfExtractor <pdf-path> [--diagnose]");
            Console.Error.WriteLine("       EngineeringPdfExtractor --create-character-test [output-pdf-path]");
            return;
        }

        var extractor = new PdfExtractor.Engineering.EngineeringPdfExtractor(filePath);
        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            var diagnostic = extractor.Diagnose();
            Console.WriteLine($"PDF Type: {diagnostic.PdfType}");
            Console.WriteLine($"Text Extractable: {(diagnostic.TextExtractable ? "YES" : "NO")}");
            foreach (var issue in diagnostic.Issues)
                Console.WriteLine("Issue: " + issue);
            foreach (var recommendation in diagnostic.Recommendations)
                Console.WriteLine("Recommendation: " + recommendation);
            return;
        }

        var result = extractor.Extract();
        string report = PdfExtractor.Engineering.EngineeringDrawingAnalyzer.Format(result);
        string outPath = Path.ChangeExtension(filePath, ".extraction.txt");
        File.WriteAllText(outPath, report, Encoding.UTF8);
        Console.WriteLine(outPath);
    }
}

internal sealed class ExtractorForm : System.Windows.Forms.Form
{
    private readonly System.Windows.Forms.TextBox _pathBox;
    private readonly System.Windows.Forms.CheckBox _saveImagesBox;
    private readonly System.Windows.Forms.Button _browseButton;
    private readonly System.Windows.Forms.Button _extractButton;
    private readonly System.Windows.Forms.TextBox _outputBox;
    private readonly System.Windows.Forms.Label _statusLabel;

    public ExtractorForm()
    {
        Text = "Engineering PDF Extractor";
        Width = 400;
        Height = 220;
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;

        var topPanel = new System.Windows.Forms.Panel
        {
            Dock = System.Windows.Forms.DockStyle.Top,
            Height = 84
        };

        _pathBox = new System.Windows.Forms.TextBox
        {
            Left = 12,
            Top = 12,
            Width = 80,
            Anchor = System.Windows.Forms.AnchorStyles.Top |
                     System.Windows.Forms.AnchorStyles.Left |
                     System.Windows.Forms.AnchorStyles.Right
        };

        _browseButton = new System.Windows.Forms.Button
        {
            Text = "Browse PDF",
            Left = 100,
            Top = 10,
            Width = 94,
            Height = 28,
            Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right
        };
        _browseButton.Click += (_, _) => BrowsePdf();

        _extractButton = new System.Windows.Forms.Button
        {
            Text = "Extract",
            Left = 25,
            Top = 45,
            Width = 94,
            Height = 28,
            Anchor = System.Windows.Forms.AnchorStyles.None | System.Windows.Forms.AnchorStyles.Right
        };
        _extractButton.Click += (_, _) => RunExtraction();

        _saveImagesBox = new System.Windows.Forms.CheckBox
        {
            Text = "Save extracted images",
            Left = 12,
            Top = 46,
            Width = 180,
            Checked = true
        };

        _statusLabel = new System.Windows.Forms.Label
        {
            Left = 210,
            Top = 48,
            Width = 760,
            Height = 24,
            Anchor = System.Windows.Forms.AnchorStyles.Top |
                     System.Windows.Forms.AnchorStyles.Left |
                     System.Windows.Forms.AnchorStyles.Right,
            Text = "Select a PDF and click Extract."
        };

        _outputBox = new System.Windows.Forms.TextBox
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            Multiline = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Both,
            ReadOnly = true,
            Font = new System.Drawing.Font("Consolas", 9f),
            WordWrap = false
        };

        topPanel.Controls.Add(_pathBox);
        topPanel.Controls.Add(_browseButton);
        topPanel.Controls.Add(_extractButton);
        topPanel.Controls.Add(_saveImagesBox);
        topPanel.Controls.Add(_statusLabel);

        Controls.Add(_outputBox);
        Controls.Add(topPanel);
    }

    private void BrowsePdf()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Select Engineering Drawing PDF",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            FilterIndex = 1,
            Multiselect = false,
            CheckFileExists = true,
            CheckPathExists = true,
            RestoreDirectory = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _pathBox.Text = dialog.FileName;
    }

    private void RunExtraction()
    {
        string filePath = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            System.Windows.Forms.MessageBox.Show(
                "Please select a PDF first.",
                "Missing file",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            return;
        }

        if (!File.Exists(filePath))
        {
            System.Windows.Forms.MessageBox.Show(
                $"File not found:\n{filePath}",
                "Invalid file path",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return;
        }

        try
        {
            ToggleBusy(true, "Extracting...");
            _outputBox.Text = "";

            var extractor = new PdfExtractor.Engineering.EngineeringPdfExtractor(filePath);
            var result = extractor.Extract();

            string report = EngineeringDrawingAnalyzer.Format(result);
            _outputBox.Text = report;

            string outPath = Path.ChangeExtension(filePath, ".extraction.txt");
            File.WriteAllText(outPath, report, Encoding.UTF8);

            string imageMessage = "";
            if (_saveImagesBox.Checked && result.Images.Count > 0)
            {
                string imageDir = Path.Combine(
                    Path.GetDirectoryName(filePath) ?? ".",
                    Path.GetFileNameWithoutExtension(filePath) + "_images");

                Directory.CreateDirectory(imageDir);
                for (int i = 0; i < result.Images.Count; i++)
                {
                    string imgPath = Path.Combine(
                        imageDir,
                        $"image_{i:D3}.{result.Images[i].Format}");
                    File.WriteAllBytes(imgPath, result.Images[i].Data);
                }

                imageMessage = $"\nImages saved to:\n{imageDir}";
            }

            ToggleBusy(false, "Extraction complete.");
            System.Windows.Forms.MessageBox.Show(
                $"Extraction complete.\n\nReport saved to:\n{outPath}{imageMessage}",
                "Done",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ToggleBusy(false, "Extraction failed.");
            System.Windows.Forms.MessageBox.Show(
                $"Error:\n{ex.Message}",
                "Extraction error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            _outputBox.Text = ex.ToString();
        }
    }

    private void ToggleBusy(bool busy, string status)
    {
        _browseButton.Enabled = !busy;
        _extractButton.Enabled = !busy;
        _pathBox.Enabled = !busy;
        _saveImagesBox.Enabled = !busy;
        _statusLabel.Text = status;
        Cursor = busy ? System.Windows.Forms.Cursors.WaitCursor : System.Windows.Forms.Cursors.Default;
        System.Windows.Forms.Application.DoEvents();
    }
}
