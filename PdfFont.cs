using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfExtractor.Engineering
{
    public sealed class PdfFontInfo
    {
        public string ResourceName { get; set; } = "";
        public int ObjectId { get; set; }
        public string BaseFont { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string Encoding { get; set; } = "";
        public bool IsEmbedded { get; set; }
        public bool IsCidFont { get; set; }
        public bool HasToUnicode { get; set; }
        public int ToUnicodeObjectId { get; set; }
        public int ToUnicodeEntries { get; set; }
        public int DuplicateToUnicodeSourceCodes { get; set; }
    }

    public sealed class PdfFontDecoder
    {
        private readonly Dictionary<int, string> _toUnicode;
        private readonly int _codeByteLength;

        public PdfFontInfo Info { get; }

        public PdfFontDecoder(PdfFontInfo info, Dictionary<int, string> toUnicode, int codeByteLength)
        {
            Info = info;
            _toUnicode = toUnicode;
            _codeByteLength = Math.Clamp(codeByteLength, 1, 4);
        }

        public string DecodeLiteral(string token)
        {
            string raw = StripDelimiters(token, '(', ')');
            string unescaped = PdfStream.UnescapePdfString(raw);
            return DecodeBytes(Encoding.Latin1.GetBytes(unescaped));
        }

        public string DecodeHex(string token)
        {
            string hex = StripDelimiters(token, '<', '>');
            return DecodeBytes(HexToBytes(hex));
        }

        public string DecodeBytes(byte[] bytes)
        {
            if (bytes.Length == 0) return "";

            if (_toUnicode.Count > 0)
                return DecodeWithToUnicode(bytes);

            return DecodeWinAnsi(bytes);
        }

        private string DecodeWithToUnicode(byte[] bytes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length;)
            {
                bool matched = false;
                int maxLen = Math.Min(_codeByteLength, bytes.Length - i);

                for (int len = maxLen; len >= 1; len--)
                {
                    int code = 0;
                    for (int j = 0; j < len; j++)
                        code = (code << 8) | bytes[i + j];

                    if (_toUnicode.TryGetValue(code, out string mapped))
                    {
                        sb.Append(mapped);
                        i += len;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    sb.Append(DecodeWinAnsiByte(bytes[i]));
                    i++;
                }
            }
            return sb.ToString();
        }

        private static string DecodeWinAnsi(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
                sb.Append(DecodeWinAnsiByte(b));
            return sb.ToString();
        }

        private static char DecodeWinAnsiByte(byte b)
        {
            return b switch
            {
                0x80 => '\u20AC',
                0x82 => '\u201A',
                0x83 => '\u0192',
                0x84 => '\u201E',
                0x85 => '\u2026',
                0x86 => '\u2020',
                0x87 => '\u2021',
                0x88 => '\u02C6',
                0x89 => '\u2030',
                0x8A => '\u0160',
                0x8B => '\u2039',
                0x8C => '\u0152',
                0x8E => '\u017D',
                0x91 => '\u2018',
                0x92 => '\u2019',
                0x93 => '\u201C',
                0x94 => '\u201D',
                0x95 => '\u2022',
                0x96 => '\u2013',
                0x97 => '\u2014',
                0x98 => '\u02DC',
                0x99 => '\u2122',
                0x9A => '\u0161',
                0x9B => '\u203A',
                0x9C => '\u0153',
                0x9E => '\u017E',
                0x9F => '\u0178',
                _ => (char)b
            };
        }

        private static string StripDelimiters(string token, char start, char end)
        {
            if (token.Length >= 2 && token[0] == start && token[^1] == end)
                return token[1..^1];
            return token;
        }

        private static byte[] HexToBytes(string hex)
        {
            hex = Regex.Replace(hex, @"\s+", "");
            if (hex.Length % 2 != 0) hex += "0";

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public static PdfFontDecoder Create(PdfFontInfo info, byte[] toUnicodeStream)
        {
            var map = new Dictionary<int, string>();
            int codeByteLength = 1;

            if (toUnicodeStream.Length > 0)
            {
                string cmap = Encoding.ASCII.GetString(toUnicodeStream);
                int duplicates = 0;
                ParseBfChar(cmap, map, ref codeByteLength, ref duplicates);
                ParseBfRange(cmap, map, ref codeByteLength, ref duplicates);
                info.DuplicateToUnicodeSourceCodes = duplicates;
            }

            info.ToUnicodeEntries = map.Count;
            return new PdfFontDecoder(info, map, codeByteLength);
        }

        private static void ParseBfChar(
            string cmap,
            Dictionary<int, string> map,
            ref int codeByteLength,
            ref int duplicates)
        {
            foreach (Match block in Regex.Matches(cmap, @"beginbfchar(?<body>.*?)endbfchar",
                         RegexOptions.Singleline))
            {
                foreach (Match m in Regex.Matches(block.Groups["body"].Value,
                             @"<(?<src>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
                {
                    AddMapping(map, m.Groups["src"].Value, m.Groups["dst"].Value, ref codeByteLength, ref duplicates);
                }
            }
        }

        private static void ParseBfRange(
            string cmap,
            Dictionary<int, string> map,
            ref int codeByteLength,
            ref int duplicates)
        {
            foreach (Match block in Regex.Matches(cmap, @"beginbfrange(?<body>.*?)endbfrange",
                         RegexOptions.Singleline))
            {
                string body = block.Groups["body"].Value;

                foreach (Match m in Regex.Matches(body,
                             @"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
                {
                    int start = HexToInt(m.Groups["start"].Value);
                    int end = HexToInt(m.Groups["end"].Value);
                    int dst = HexToInt(m.Groups["dst"].Value);
                    codeByteLength = Math.Max(codeByteLength, m.Groups["start"].Value.Length / 2);

                    for (int code = start; code <= end && code - start < 2048; code++)
                    {
                        if (map.ContainsKey(code)) duplicates++;
                        map[code] = char.ConvertFromUtf32(dst + (code - start));
                    }
                }

                foreach (Match m in Regex.Matches(body,
                             @"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*\[(?<items>.*?)\]",
                             RegexOptions.Singleline))
                {
                    int start = HexToInt(m.Groups["start"].Value);
                    codeByteLength = Math.Max(codeByteLength, m.Groups["start"].Value.Length / 2);
                    int offset = 0;
                    foreach (Match item in Regex.Matches(m.Groups["items"].Value, @"<(?<dst>[0-9A-Fa-f]+)>"))
                    {
                        if (map.ContainsKey(start + offset)) duplicates++;
                        map[start + offset] = HexToUnicode(item.Groups["dst"].Value);
                        offset++;
                    }
                }
            }
        }

        private static void AddMapping(
            Dictionary<int, string> map,
            string srcHex,
            string dstHex,
            ref int codeByteLength,
            ref int duplicates)
        {
            int code = HexToInt(srcHex);
            codeByteLength = Math.Max(codeByteLength, srcHex.Length / 2);
            if (map.ContainsKey(code)) duplicates++;
            map[code] = HexToUnicode(dstHex);
        }

        private static int HexToInt(string hex) =>
            Convert.ToInt32(hex, 16);

        private static string HexToUnicode(string hex)
        {
            hex = Regex.Replace(hex, @"\s+", "");
            if (hex.Length == 0) return "";

            var sb = new StringBuilder();
            if (hex.Length % 4 == 0)
            {
                for (int i = 0; i + 4 <= hex.Length; i += 4)
                {
                    int codePoint = Convert.ToInt32(hex.Substring(i, 4), 16);
                    if (codePoint > 0)
                        sb.Append(char.ConvertFromUtf32(codePoint));
                }
                return sb.ToString();
            }

            foreach (byte b in HexToBytes(hex))
                sb.Append((char)b);
            return sb.ToString();
        }
    }
}
