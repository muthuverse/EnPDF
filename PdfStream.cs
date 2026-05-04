using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfExtractor.Engineering
{
    /// <summary>
    /// Handles PDF content stream decompression and text extraction with full
    /// coordinate tracking (Tm, Td, TD, T*, Tj, TJ, ', " operators).
    ///
    /// PoC scope: FlateDecode, ASCIIHex, ASCII85 filters only.
    /// Not yet handled: LZW, CCITTFax, JBIG2, JPX.
    /// </summary>
    public static class PdfStream
    {
        // ─── Decompression ────────────────────────────────────────────────────

        public static byte[] Decompress(byte[] data, string filter)
        {
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();
            if (string.IsNullOrEmpty(filter)) return data;

            // /Filter can be a single name (/FlateDecode) or an array
            // (e.g. [/ASCII85Decode /FlateDecode]). Apply decoders in order.
            var filters = Regex.Matches(filter, @"/[A-Za-z0-9]+")
                .Select(m => m.Value)
                .ToList();

            if (filters.Count == 0) return data;

            byte[] current = data;
            foreach (var f in filters)
            {
                current = ApplySingleFilter(current, f);
            }
            return current;
        }

        private static byte[] ApplySingleFilter(byte[] data, string filterName)
        {
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();

            if (filterName == "/FlateDecode")
                return DecompressFlateDecode(data);

            if (filterName == "/ASCIIHexDecode")
                return DecodeAsciiHex(data);

            if (filterName == "/ASCII85Decode")
                return DecodeAscii85(data);

            // Unsupported filter — return current bytes so caller can continue
            Console.WriteLine($"  [Unsupported filter: {filterName} — keeping current bytes]");
            return data;
        }

        private static byte[] DecompressFlateDecode(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                // Skip zlib header (2 bytes: 0x78 0x9C / 0x78 0x01 / 0x78 0xDA)
                if (data.Length > 2 && data[0] == 0x78)
                {
                    ms.ReadByte();
                    ms.ReadByte();
                }
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FlateDecode error: {ex.Message}]");
                return data;
            }
        }

        private static byte[] DecodeAsciiHex(byte[] data)
        {
            var hex = Encoding.ASCII.GetString(data)
                              .Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var eod = hex.IndexOf('>');
            if (eod >= 0) hex = hex[..eod];
            if (hex.Length % 2 != 0) hex += "0";
            var bytes = new List<byte>();
            for (int i = 0; i + 2 <= hex.Length; i += 2)
                bytes.Add(Convert.ToByte(hex.Substring(i, 2), 16));
            return bytes.ToArray();
        }

        private static byte[] DecodeAscii85(byte[] data)
        {
            var result = new List<byte>();
            var str = Encoding.ASCII.GetString(data);
            int i = 0;
            while (i < str.Length)
            {
                char c = str[i];
                if (c == '~') break;                        // ~> end marker
                if (c == 'z') { result.AddRange(new byte[4]); i++; continue; }
                if (c < '!' || c > 'u') { i++; continue; } // skip whitespace

                var group = new int[5];
                int count = 0;
                while (count < 5 && i < str.Length && str[i] >= '!' && str[i] <= 'u')
                    group[count++] = str[i++] - '!';
                for (int k = count; k < 5; k++) group[k] = 84; // padding

                uint val = (uint)(group[0] * 52200625u + group[1] * 614125u
                                + group[2] * 7225u      + group[3] * 85u + group[4]);
                byte[] b = { (byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val };
                for (int k = 0; k < count - 1; k++) result.Add(b[k]);
            }
            return result.ToArray();
        }

        // ─── Text extraction with full coordinate tracking ────────────────────

        /// <summary>
        /// Extract positioned TextElement objects from a decompressed content stream.
        /// Implements the PDF text state machine as defined in ISO 32000 §9.
        /// </summary>
        public static List<TextElement> ExtractTextElements(
            byte[] decompressedData,
            int pageNumber,
            IReadOnlyDictionary<string, PdfFontDecoder> fontDecoders = null)
        {
            var elements = new List<TextElement>();
            if (decompressedData == null || decompressedData.Length == 0) return elements;

            string content = Encoding.Latin1.GetString(decompressedData);
            var tokens = Tokenize(content);

            // ── Text state variables ───────────────────────────────────────────
            // Text matrix Tm and text line matrix Tlm (6 components: a b c d e f)
            float tm_a=1, tm_b=0, tm_c=0, tm_d=1, tm_e=0, tm_f=0;
            float tl_a=1, tl_b=0, tl_c=0, tl_d=1, tl_e=0, tl_f=0;
            float fontSize   = 12f;
            float leading    = 0f;
            float charSpace  = 0f;
            float wordSpace  = 0f;
            string fontName  = "";
            PdfFontDecoder currentFont = null;
            bool inText      = false;

            var stack = new List<string>(); // operand stack

            for (int i = 0; i < tokens.Count; i++)
            {
                string tok = tokens[i];

                switch (tok)
                {
                    // ── Text block delimiters ──────────────────────────────────
                    case "BT":
                        inText = true;
                        ResetTextMatrix(ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                        ResetTextMatrix(ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f);
                        stack.Clear();
                        break;

                    case "ET":
                        inText = false;
                        stack.Clear();
                        break;

                    // ── Font selection: /FontName size Tf ─────────────────────
                    case "Tf":
                        if (stack.Count >= 2)
                        {
                            float.TryParse(stack[^1], NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out fontSize);
                            fontName = stack[^2].TrimStart('/');
                            if (fontDecoders == null ||
                                !fontDecoders.TryGetValue(fontName, out currentFont))
                            {
                                currentFont = null;
                            }
                        }
                        stack.Clear();
                        break;

                    // ── Text matrix: a b c d e f Tm ───────────────────────────
                    case "Tm":
                        if (stack.Count >= 6)
                        {
                            ParseFloat(stack[^6], out tm_a);
                            ParseFloat(stack[^5], out tm_b);
                            ParseFloat(stack[^4], out tm_c);
                            ParseFloat(stack[^3], out tm_d);
                            ParseFloat(stack[^2], out tm_e);
                            ParseFloat(stack[^1], out tm_f);
                            // Tlm = Tm
                            tl_a=tm_a; tl_b=tm_b; tl_c=tm_c; tl_d=tm_d; tl_e=tm_e; tl_f=tm_f;
                        }
                        stack.Clear();
                        break;

                    // ── Move text position: tx ty Td ──────────────────────────
                    case "Td":
                        if (stack.Count >= 2)
                        {
                            ParseFloat(stack[^2], out float tx);
                            ParseFloat(stack[^1], out float ty);
                            ApplyTd(tx, ty,
                                    ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f,
                                    ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                        }
                        stack.Clear();
                        break;

                    // ── Move and set leading: tx ty TD ────────────────────────
                    case "TD":
                        if (stack.Count >= 2)
                        {
                            ParseFloat(stack[^2], out float tx);
                            ParseFloat(stack[^1], out float ty);
                            leading = -ty;
                            ApplyTd(tx, ty,
                                    ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f,
                                    ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                        }
                        stack.Clear();
                        break;

                    // ── Move to next line: T* ─────────────────────────────────
                    case "T*":
                        ApplyTd(0, -leading,
                                ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f,
                                ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                        break;

                    // ── Set leading: val TL ───────────────────────────────────
                    case "TL":
                        if (stack.Count >= 1) ParseFloat(stack[^1], out leading);
                        stack.Clear();
                        break;

                    // ── Character/word spacing ────────────────────────────────
                    case "Tc":
                        if (stack.Count >= 1) ParseFloat(stack[^1], out charSpace);
                        stack.Clear();
                        break;

                    case "Tw":
                        if (stack.Count >= 1) ParseFloat(stack[^1], out wordSpace);
                        stack.Clear();
                        break;

                    // ── Show text: (string) Tj ────────────────────────────────
                    case "Tj":
                        if (inText && stack.Count >= 1)
                        {
                            string text = DecodeTextToken(stack[^1], currentFont);
                            AddElement(elements, text, tm_e, tm_f,
                                       fontSize, tm_d, fontName, pageNumber);
                            AdvanceTextPosition(ref tm_e, ref tm_f, tm_a, tm_b,
                                                text, fontSize, charSpace, wordSpace);
                        }
                        stack.Clear();
                        break;

                    // ── Next line + show: (string) ' ──────────────────────────
                    case "'":
                        ApplyTd(0, -leading,
                                ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f,
                                ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                        if (inText && stack.Count >= 1)
                        {
                            string text = DecodeTextToken(stack[^1], currentFont);
                            AddElement(elements, text, tm_e, tm_f,
                                       fontSize, tm_d, fontName, pageNumber);
                            AdvanceTextPosition(ref tm_e, ref tm_f, tm_a, tm_b,
                                                text, fontSize, charSpace, wordSpace);
                        }
                        stack.Clear();
                        break;

                    // ── Word/char spacing + next line + show: aw ac string " ──
                    case "\"":
                        if (stack.Count >= 3)
                        {
                            ParseFloat(stack[^3], out wordSpace);
                            ParseFloat(stack[^2], out charSpace);
                            ApplyTd(0, -leading,
                                    ref tl_a, ref tl_b, ref tl_c, ref tl_d, ref tl_e, ref tl_f,
                                    ref tm_a, ref tm_b, ref tm_c, ref tm_d, ref tm_e, ref tm_f);
                            if (inText)
                            {
                                string text = DecodeTextToken(stack[^1], currentFont);
                                AddElement(elements, text, tm_e, tm_f,
                                           fontSize, tm_d, fontName, pageNumber);
                                AdvanceTextPosition(ref tm_e, ref tm_f, tm_a, tm_b,
                                                    text, fontSize, charSpace, wordSpace);
                            }
                        }
                        stack.Clear();
                        break;

                    // ── Show text with kerning: [...] TJ ─────────────────────
                    case "TJ":
                        if (inText && stack.Count >= 1)
                        {
                            string arrayToken = stack[^1];
                            if (arrayToken.StartsWith("["))
                            {
                                string text = ExtractTJText(arrayToken, currentFont);
                                AddElement(elements, text, tm_e, tm_f,
                                           fontSize, tm_d, fontName, pageNumber);
                                AdvanceTextPosition(ref tm_e, ref tm_f, tm_a, tm_b,
                                                    text, fontSize, charSpace, wordSpace);
                            }
                        }
                        stack.Clear();
                        break;

                    // ── Push everything else onto the operand stack ───────────
                    default:
                        stack.Add(tok);
                        break;
                }
            }

            return elements;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static void ResetTextMatrix(
            ref float a, ref float b, ref float c,
            ref float d, ref float e, ref float f)
        { a=1; b=0; c=0; d=1; e=0; f=0; }

        private static void ApplyTd(
            float tx, float ty,
            ref float tl_a, ref float tl_b, ref float tl_c,
            ref float tl_d, ref float tl_e, ref float tl_f,
            ref float tm_a, ref float tm_b, ref float tm_c,
            ref float tm_d, ref float tm_e, ref float tm_f)
        {
            // Tlm = [1 0 0; 0 1 0; tx ty 1] × Tlm  (row-vector PDF convention)
            tl_e = tl_a * tx + tl_c * ty + tl_e;
            tl_f = tl_b * tx + tl_d * ty + tl_f;
            tm_a = tl_a; tm_b = tl_b; tm_c = tl_c;
            tm_d = tl_d; tm_e = tl_e; tm_f = tl_f;
        }

        private static void ParseFloat(string s, out float val) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);

        private static void AddElement(
            List<TextElement> list, string text,
            float x, float y, float fontSize, float scaleD,
            string fontName, int page)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            list.Add(new TextElement
            {
                X        = x,
                Y        = y,
                FontSize = MathF.Abs(fontSize * scaleD),
                FontName = fontName,
                Text     = text,
                PageNumber = page
            });
        }

        private static void AdvanceTextPosition(
            ref float tm_e, ref float tm_f,
            float tm_a, float tm_b,
            string text, float fontSize,
            float charSpace, float wordSpace)
        {
            if (string.IsNullOrEmpty(text)) return;
            float advance = EstimateAdvance(text, fontSize, charSpace, wordSpace);
            tm_e += tm_a * advance;
            tm_f += tm_b * advance;
        }

        private static float EstimateAdvance(
            string text, float fontSize, float charSpace, float wordSpace)
        {
            // PoC approximation when font metrics are unavailable:
            // average glyph width ~= 0.5em.
            int glyphs = text.Length;
            int spaces = text.Count(c => c == ' ');
            float baseAdvance = glyphs * fontSize * 0.5f;
            float charAdvance = Math.Max(0, glyphs - 1) * charSpace;
            float wordAdvance = spaces * wordSpace;
            return Math.Max(0, baseAdvance + charAdvance + wordAdvance);
        }

        // ─── Tokenizer ────────────────────────────────────────────────────────

        /// <summary>
        /// Tokenize a PDF content stream.
        /// Handles: (literal strings), &lt;hex strings&gt;, [arrays], /names, numbers, operators.
        /// </summary>
        public static List<string> Tokenize(string content)
        {
            var tokens = new List<string>();
            int i = 0;
            int n = content.Length;

            while (i < n)
            {
                // Skip whitespace
                while (i < n && content[i] is ' ' or '\t' or '\r' or '\n') i++;
                if (i >= n) break;

                char c = content[i];

                // Comment
                if (c == '%') { while (i < n && content[i] != '\n' && content[i] != '\r') i++; continue; }

                // Literal string (...)
                if (c == '(')
                {
                    i++; int depth = 1;
                    var sb = new StringBuilder();
                    while (i < n && depth > 0)
                    {
                        char ch = content[i];
                        if (ch == '\\' && i + 1 < n) { sb.Append(ch); sb.Append(content[i+1]); i += 2; continue; }
                        if (ch == '(') depth++;
                        else if (ch == ')') { depth--; if (depth == 0) { i++; break; } }
                        if (depth > 0) sb.Append(ch);
                        i++;
                    }
                    tokens.Add("(" + sb + ")");
                    continue;
                }

                // Hex string <...> (not <<)
                if (c == '<' && i + 1 < n && content[i+1] != '<')
                {
                    i++; var sb = new StringBuilder();
                    while (i < n && content[i] != '>') { sb.Append(content[i]); i++; }
                    if (i < n) i++; // skip >
                    tokens.Add("<" + sb + ">");
                    continue;
                }

                // Dictionary << >>  — pass through as single token (we don't parse inline dicts)
                if (c == '<' && i + 1 < n && content[i+1] == '<')
                {
                    int start = i; int depth = 1; i += 2;
                    while (i < n - 1 && depth > 0)
                    {
                        if (content[i]=='<' && content[i+1]=='<') { depth++; i += 2; }
                        else if (content[i]=='>' && content[i+1]=='>') { depth--; i += 2; }
                        else i++;
                    }
                    tokens.Add(content[start..i]);
                    continue;
                }

                // Array [...]
                if (c == '[')
                {
                    int start = i; int depth = 1; i++;
                    while (i < n && depth > 0)
                    {
                        char ch = content[i];
                        if (ch == '\\' && i + 1 < n) { i += 2; continue; }
                        if (ch == '(') // nested string inside array
                        {
                            i++; int pd = 1;
                            while (i < n && pd > 0)
                            {
                                if (content[i] == '\\' && i + 1 < n) { i += 2; continue; }
                                if (content[i] == '(') pd++;
                                else if (content[i] == ')') pd--;
                                i++;
                            }
                            continue;
                        }
                        if (ch == '[') depth++;
                        else if (ch == ']') depth--;
                        i++;
                    }
                    tokens.Add(content[start..i]);
                    continue;
                }

                // Name /...
                if (c == '/')
                {
                    int start = i++; 
                    while (i < n && content[i] is not (' ' or '\t' or '\r' or '\n'
                                                        or '/' or '[' or ']'
                                                        or '(' or ')' or '<' or '>')) i++;
                    tokens.Add(content[start..i]);
                    continue;
                }

                // Numbers and operators
                {
                    int start = i;
                    while (i < n && content[i] is not (' ' or '\t' or '\r' or '\n'
                                                        or '/' or '[' or ']'
                                                        or '(' or ')' or '<' or '>')) i++;
                    string tok = content[start..i];
                    if (!string.IsNullOrEmpty(tok)) tokens.Add(tok);
                }
            }

            return tokens;
        }

        // ─── String helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Convert a hex string (without angle brackets) to a text string.
        /// Detects UTF-16BE by checking for leading 00 bytes or FEFF BOM.
        /// </summary>
        public static string HexStringToText(string hex)
        {
            hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "").ToUpper();
            if (hex.Length % 2 != 0) hex += "0";

            bool isUnicode = hex.Length >= 4 &&
                             (hex.StartsWith("FEFF") || hex.StartsWith("FFFE") ||
                              (hex.StartsWith("00") && hex.Length > 4));

            var sb = new StringBuilder();
            if (isUnicode)
            {
                int start = hex.StartsWith("FEFF") || hex.StartsWith("FFFE") ? 4 : 0;
                for (int i = start; i + 4 <= hex.Length; i += 4)
                {
                    int code = Convert.ToInt32(hex.Substring(i, 4), 16);
                    if (code > 0) sb.Append((char)code);
                }
            }
            else
            {
                for (int i = 0; i + 2 <= hex.Length; i += 2)
                    sb.Append((char)Convert.ToInt32(hex.Substring(i, 2), 16));
            }
            return sb.ToString();
        }

        private static string DecodeTextToken(string token, PdfFontDecoder fontDecoder)
        {
            if (string.IsNullOrEmpty(token)) return "";

            if (token.StartsWith("<") && token.EndsWith(">"))
                return fontDecoder != null
                    ? fontDecoder.DecodeHex(token)
                    : HexStringToText(token[1..^1]);

            if (token.StartsWith("(") && token.EndsWith(")"))
                return fontDecoder != null
                    ? fontDecoder.DecodeLiteral(token)
                    : UnescapePdfString(token[1..^1]);

            return fontDecoder != null
                ? fontDecoder.DecodeLiteral("(" + token + ")")
                : UnescapePdfString(token);
        }

        /// <summary>
        /// Extract text from a TJ array token like [(Hello) -200 (World) 50 (!)].
        /// Numeric kerning values are ignored for PoC — they affect only glyph spacing.
        /// </summary>
        private static string ExtractTJText(string arrayToken, PdfFontDecoder fontDecoder)
        {
            var sb = new StringBuilder();
            int i = 1; // skip opening [
            int n = arrayToken.Length;

            while (i < n && arrayToken[i] != ']')
            {
                char c = arrayToken[i];

                if (c == '(') // literal string
                {
                    i++; int depth = 1;
                    var str = new StringBuilder();
                    while (i < n && depth > 0)
                    {
                        char ch = arrayToken[i];
                        if (ch == '\\' && i + 1 < n) { str.Append(ch); str.Append(arrayToken[i+1]); i += 2; continue; }
                        if (ch == '(') depth++;
                        else if (ch == ')') { depth--; if (depth == 0) { i++; break; } }
                        if (depth > 0) str.Append(ch);
                        i++;
                    }
                    string token = "(" + str + ")";
                    sb.Append(fontDecoder != null
                        ? fontDecoder.DecodeLiteral(token)
                        : UnescapePdfString(str.ToString()));
                }
                else if (c == '<') // hex string
                {
                    i++; var hex = new StringBuilder();
                    while (i < n && arrayToken[i] != '>') { hex.Append(arrayToken[i]); i++; }
                    if (i < n) i++;
                    string token = "<" + hex + ">";
                    sb.Append(fontDecoder != null
                        ? fontDecoder.DecodeHex(token)
                        : HexStringToText(hex.ToString()));
                }
                else i++; // number or whitespace — skip
            }
            return sb.ToString();
        }

        /// <summary>
        /// Unescape a PDF literal string.
        /// Handles: \\n \\r \\t \\b \\f \\( \\) \\\\ and octal \\ddd.
        /// </summary>
        public static string UnescapePdfString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var sb = new StringBuilder(input.Length);
            int i = 0;
            while (i < input.Length)
            {
                if (input[i] != '\\' || i + 1 >= input.Length) { sb.Append(input[i++]); continue; }

                char next = input[i + 1];
                switch (next)
                {
                    case 'n':  sb.Append('\n'); i += 2; break;
                    case 'r':  sb.Append('\r'); i += 2; break;
                    case 't':  sb.Append('\t'); i += 2; break;
                    case 'b':  sb.Append('\b'); i += 2; break;
                    case 'f':  sb.Append('\f'); i += 2; break;
                    case '(':  sb.Append('(');  i += 2; break;
                    case ')':  sb.Append(')');  i += 2; break;
                    case '\\': sb.Append('\\'); i += 2; break;
                    default:
                        if (char.IsAsciiDigit(next)) // octal \ddd
                        {
                            int end = i + 2;
                            while (end < input.Length && end < i + 4 && char.IsAsciiDigit(input[end])) end++;
                            try { sb.Append((char)Convert.ToInt32(input.Substring(i + 1, end - i - 1), 8)); }
                            catch { sb.Append(next); }
                            i = end;
                        }
                        else { sb.Append(next); i += 2; }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
