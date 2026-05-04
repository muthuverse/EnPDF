using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfExtractor.Engineering
{
    public static class EngineeringCharacterTestPdfGenerator
    {
        public const string DefaultFileName = "Engineering_All_Characters_Test.pdf";

        public static string Create(string outputPath)
        {
            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var noteLines = new[]
            {
                "ENGINEERING / NON-ENGINEERING CHARACTER TEST NOTE",
                "Purpose: native PDF text test for EnPDF extraction; no OCR and no third-party PDF generator.",
                "Standard dimensions: 120.00 mm, 50.00 mm, R2.5, R17.5, Ø38, ⌀11, 2× M8.",
                "Tolerance symbols: ±0.05, +0.10/-0.05, ≤3.2 μm, ≥10 N·m, 90°, 45°.",
                "GD&T symbols: ⌀ diameter, ⌖ position, ⟂ perpendicularity, ∥ parallelism, ⌭ cylindricity.",
                "GD&T continued: ○ circularity, ⌓ profile, ⌒ arc/profile, ⌯ symmetry, ↗ runout, Ⓜ MMC, Ⓛ LMC, Ⓢ RFS.",
                "Datums and callouts: datum A|B|C, target ⊕, triangle ▽, square □, leader arrow →, angle ∠.",
                "Surface and process: Ra 3.2 μm, roughness √, hardness 45 HRC, temperature 20 ℃.",
                "Math symbols: = ≠ ≈ ∑ ∆ Δ δ π λ α β γ Ω ∞ √ ∫ × ÷ · •.",
                "Units: mm, cm, m, in, ft, kg, g, N, kN, MPa, GPa, rpm, s, ms, µm, μm.",
                "Punctuation: \"double quotes\", 'single quotes', en dash – em dash — ellipsis … slash / backslash \\.",
                "Currency and legal: ₹ $ € £ ¥ © ® ™ § ¶.",
                "Latin accents: café, naïve, façade, résumé, Ångström, São Paulo, München.",
                "Indian language sample: தமிழ், हिंदी, ಕನ್ನಡ, తెలుగు, മലയാളം.",
                "Greek sample: Α Β Γ Δ Ω α β γ δ μ π σ φ.",
                "Checklist: selectable text YES; vector text YES; embedded font NO; ToUnicode YES.",
                "Expected extraction: all characters above should appear as Unicode text in the report."
            };

            CreatePdf(outputPath, noteLines);
            return outputPath;
        }

        private static void CreatePdf(string path, IReadOnlyList<string> lines)
        {
            var uniqueChars = lines.SelectMany(l => l).Distinct().OrderBy(c => c).ToList();
            string content = BuildContent(lines);
            string cmap = BuildToUnicodeCMap(uniqueChars);

            var objects = new List<byte[]>
            {
                Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
                StreamObj(Encoding.ASCII.GetBytes(content)),
                Obj("<< /Type /Font /Subtype /Type0 /BaseFont /SegoeUISymbol /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>"),
                Obj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SegoeUISymbol /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /DW 600 >>"),
                StreamObj(Encoding.ASCII.GetBytes(cmap)),
                Obj("<< /Type /FontDescriptor /FontName /SegoeUISymbol /Flags 4 /FontBBox [-1000 -300 2000 1100] /ItalicAngle 0 /Ascent 900 /Descent -250 /CapHeight 700 /StemV 80 >>")
            };

            using var ms = new MemoryStream();
            WriteAscii(ms, "%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

            var offsets = new List<long> { 0 };
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(ms.Position);
                WriteAscii(ms, $"{i + 1} 0 obj\n");
                ms.Write(objects[i]);
                WriteAscii(ms, "\nendobj\n");
            }

            long xref = ms.Position;
            WriteAscii(ms, $"xref\n0 {objects.Count + 1}\n");
            WriteAscii(ms, "0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1))
                WriteAscii(ms, $"{offset:0000000000} 00000 n \n");
            WriteAscii(ms, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

            File.WriteAllBytes(path, ms.ToArray());
        }

        private static string BuildContent(IReadOnlyList<string> lines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("BT");
            sb.AppendLine("/F1 13 Tf");
            sb.AppendLine("16 TL");
            sb.AppendLine("36 558 Td");

            foreach (string line in lines)
            {
                sb.Append('<');
                sb.Append(ToUtf16BeHex(line));
                sb.AppendLine("> Tj");
                sb.AppendLine("T*");
            }

            sb.AppendLine("ET");
            sb.AppendLine("0.5 w");
            sb.AppendLine("30 30 782 535 re S");
            sb.AppendLine("30 510 782 1 re f");
            return sb.ToString();
        }

        private static string BuildToUnicodeCMap(IReadOnlyList<char> chars)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/CIDInit /ProcSet findresource begin");
            sb.AppendLine("12 dict begin");
            sb.AppendLine("begincmap");
            sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
            sb.AppendLine("/CMapName /EngineeringAllCharacters def");
            sb.AppendLine("/CMapType 2 def");
            sb.AppendLine("1 begincodespacerange");
            sb.AppendLine("<0000> <FFFF>");
            sb.AppendLine("endcodespacerange");

            foreach (var chunk in chars.Chunk(90))
            {
                sb.AppendLine($"{chunk.Length} beginbfchar");
                foreach (char ch in chunk)
                {
                    string hex = ((int)ch).ToString("X4", CultureInfo.InvariantCulture);
                    sb.AppendLine($"<{hex}> <{hex}>");
                }
                sb.AppendLine("endbfchar");
            }

            sb.AppendLine("endcmap");
            sb.AppendLine("CMapName currentdict /CMap defineresource pop");
            sb.AppendLine("end");
            sb.AppendLine("end");
            return sb.ToString();
        }

        private static string ToUtf16BeHex(string text)
        {
            var sb = new StringBuilder(text.Length * 4);
            foreach (char ch in text)
                sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static byte[] Obj(string body) =>
            Encoding.ASCII.GetBytes(body);

        private static byte[] StreamObj(byte[] data)
        {
            using var ms = new MemoryStream();
            WriteAscii(ms, $"<< /Length {data.Length} >>\nstream\n");
            ms.Write(data);
            WriteAscii(ms, "\nendstream");
            return ms.ToArray();
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes);
        }
    }
}
