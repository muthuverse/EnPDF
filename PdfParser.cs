using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfExtractor.Engineering
{
    public class PdfObject
    {
        public int Id                             { get; set; }
        public int Generation                     { get; set; }
        public string Content                     { get; set; } = "";
        public byte[] RawStream                   { get; set; }
        public Dictionary<string, string> Dict    { get; set; } = new();

        public string Get(string key) =>
            Dict.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// Parses PDF structure: xref table, object lookup, page tree.
    ///
    /// Bug fixes over original Manus code:
    ///   1. Uses /Length to skip stream body before searching for endobj.
    ///   2. Object header read uses 200 bytes instead of 100.
    ///   3. xref section is not artificially capped at 1 MB.
    ///   4. Multiple xref subsections in one table are supported.
    ///
    /// PoC known gap: cross-reference streams (PDF 1.5+, xref offset points
    /// to a stream object instead of 'xref'). Detected and reported but not parsed.
    /// </summary>
    public class PdfParser
    {
        private readonly byte[] _data;
        private readonly Dictionary<int, long> _xref = new();
        public int RootObjectId { get; private set; }

        public PdfParser(string filePath)
        {
            _data = File.ReadAllBytes(filePath);
            ParseXref();
        }

        // ─── Cross-reference table ────────────────────────────────────────────

        private void ParseXref()
        {
            // Read the last 2 KB to find 'startxref'
            int searchLen = Math.Min(_data.Length, 2048);
            string tail = Encoding.ASCII.GetString(_data, _data.Length - searchLen, searchLen);

            var xrefMatches = Regex.Matches(tail, @"startxref\s+(\d+)");
            if (xrefMatches.Count == 0)
            {
                Console.WriteLine("[WARNING] startxref not found. PDF may be malformed.");
                return;
            }

            // Incremental PDFs can contain multiple startxref markers.
            // The last one points to the most recent xref section.
            var mXref = xrefMatches[^1];
            long xrefOffset = long.Parse(mXref.Groups[1].Value);
            if (xrefOffset < 0 || xrefOffset >= _data.Length)
            {
                Console.WriteLine("[WARNING] startxref offset out of range.");
                return;
            }

            // Detect xref type
            if (_data[xrefOffset] == 'x')
            {
                ParseTraditionalXref(xrefOffset);
            }
            else
            {
                // PDF 1.5+ compressed xref stream
                Console.WriteLine("[PoC NOTICE] Compressed xref stream detected (PDF 1.5+)." +
                                  " Falling back to full-file object scan.");
                ScanObjectsFromFullFile();
            }

            // Find /Root from trailer (last occurrence — handles incremental updates)
            FindRoot();
        }

        private void ParseTraditionalXref(long xrefOffset)
        {
            // Read from xrefOffset to end — no artificial size cap
            int len = (int)(_data.Length - xrefOffset);
            string xrefSection = Encoding.ASCII.GetString(_data, (int)xrefOffset, len);

            var lines = xrefSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int currentId = 0;

            for (int i = 1; i < lines.Length; i++) // skip 'xref' on line 0
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("trailer")) break;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2) // subsection header: startId count
                {
                    int.TryParse(parts[0], out currentId);
                }
                else if (parts.Length == 3) // entry: offset gen f/n
                {
                    if (parts[2] == "n" && long.TryParse(parts[0], out long offset))
                        _xref[currentId] = offset;
                    currentId++;
                }
            }

            Console.WriteLine($"  xref table: {_xref.Count} objects mapped.");

            // Handle incremental updates: follow /Prev chain
            var prevMatch = Regex.Match(xrefSection, @"/Prev\s+(\d+)");
            if (prevMatch.Success && long.TryParse(prevMatch.Groups[1].Value, out long prevOffset)
                && prevOffset > 0 && prevOffset < _data.Length && _data[prevOffset] == 'x')
            {
                Console.WriteLine($"  Following /Prev xref at offset {prevOffset}...");
                ParseTraditionalXref(prevOffset);
            }
        }

        /// <summary>
        /// Fallback for PDFs with compressed xref streams:
        /// scan the entire file for "N 0 obj" patterns.
        /// Less precise but covers most real-world cases at PoC level.
        /// </summary>
        private void ScanObjectsFromFullFile()
        {
            string full = Encoding.ASCII.GetString(_data);
            var matches = Regex.Matches(full, @"\b(\d+)\s+(\d+)\s+obj\b");
            var bestById = new Dictionary<int, (int Generation, int Index)>();

            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
                if (!int.TryParse(m.Groups[2].Value, out int generation)) continue;

                if (!bestById.TryGetValue(id, out var existing) ||
                    generation > existing.Generation ||
                    (generation == existing.Generation && m.Index > existing.Index))
                {
                    bestById[id] = (generation, m.Index);
                }
            }

            foreach (var kv in bestById)
                _xref[kv.Key] = kv.Value.Index;

            Console.WriteLine($"  Full-file scan: {_xref.Count} objects found.");
        }

        private void FindRoot()
        {
            // Search last 8 KB for the most-recent /Root entry
            int searchLen = Math.Min(_data.Length, 8192);
            string tail = Encoding.ASCII.GetString(_data, _data.Length - searchLen, searchLen);

            var m = Regex.Match(tail, @"/Root\s+(\d+)\s+\d+\s+R");
            if (!m.Success)
            {
                // Rare: /Root is very early in file — search whole thing
                string full = Encoding.ASCII.GetString(_data);
                m = Regex.Match(full, @"/Root\s+(\d+)\s+\d+\s+R", RegexOptions.RightToLeft);
            }

            if (m.Success)
            {
                RootObjectId = int.Parse(m.Groups[1].Value);
                Console.WriteLine($"  Root object ID: {RootObjectId}");
            }
            else
            {
                Console.WriteLine("[WARNING] /Root not found in trailer.");
            }
        }

        public int GetInfoObjectId()
        {
            // Prefer tail search (most recent trailer in incremental PDFs),
            // then fall back to full-file right-to-left search.
            int searchLen = Math.Min(_data.Length, 8192);
            string tail = Encoding.ASCII.GetString(_data, _data.Length - searchLen, searchLen);

            var m = Regex.Match(tail, @"/Info\s+(\d+)\s+\d+\s+R");
            if (!m.Success)
            {
                string full = Encoding.ASCII.GetString(_data);
                m = Regex.Match(full, @"/Info\s+(\d+)\s+\d+\s+R", RegexOptions.RightToLeft);
            }

            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        // ─── Object access ────────────────────────────────────────────────────

        public PdfObject GetObject(int id)
        {
            if (!_xref.TryGetValue(id, out long offset)) return null;
            if (offset < 0 || offset >= _data.Length) return null;

            // Read up to 200 bytes for the object header (was 100 — too small)
            int headerLen = (int)Math.Min(200, _data.Length - offset);
            string header = Encoding.ASCII.GetString(_data, (int)offset, headerLen);

            var mHead = Regex.Match(header, @"^(\d+)\s+(\d+)\s+obj");
            if (!mHead.Success) return null;

            var obj = new PdfObject
            {
                Id         = int.Parse(mHead.Groups[1].Value),
                Generation = int.Parse(mHead.Groups[2].Value)
            };

            int objectEndMarker = FindMarker((int)offset, "endobj", _data.Length);
            int objectLimit = objectEndMarker >= 0 ? objectEndMarker : _data.Length;

            // Parse dictionary first to get /Length for stream boundary
            int dictStart = -1, dictEnd = -1;
            FindDictionaryBounds((int)offset, objectLimit, out dictStart, out dictEnd);

            string dictStr = "";
            if (dictStart >= 0 && dictEnd > dictStart)
            {
                dictStr = Encoding.ASCII.GetString(_data, dictStart, dictEnd - dictStart);
                ParseDictionaryInto(obj.Dict, dictStr);
            }

            // Use /Length to find stream boundaries (fixes the endobj-in-stream bug)
            int streamStart = FindStreamStart((int)offset, dictEnd, objectLimit);
            if (streamStart >= 0)
            {
                int streamLength = GetStreamLength(obj, streamStart, objectLimit);
                if (streamLength > 0 && streamStart + streamLength <= _data.Length)
                {
                    obj.RawStream = new byte[streamLength];
                    Array.Copy(_data, streamStart, obj.RawStream, 0, streamLength);
                }
            }

            // Build content string from object start to endstream (or endobj)
            int objEnd = FindEndObj((int)offset, streamStart, obj.RawStream?.Length ?? 0);
            obj.Content = objEnd > 0
                ? Encoding.ASCII.GetString(_data, (int)offset, objEnd - (int)offset)
                : dictStr;

            return obj;
        }

        public List<int> GetKnownObjectIds() =>
            _xref.Keys.OrderBy(id => id).ToList();

        public bool ContainsAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            byte[] needle = Encoding.ASCII.GetBytes(value);
            for (int i = 0; i <= _data.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (_data[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        private void FindDictionaryBounds(int from, int limitExclusive, out int start, out int end)
        {
            start = -1; end = -1;
            // Find first <<
            for (int i = from; i < limitExclusive - 1; i++)
            {
                if (_data[i] == '<' && _data[i+1] == '<') { start = i + 2; break; }
            }
            if (start < 0) return;

            // Find matching >> by counting nesting depth
            int depth = 1;
            for (int i = start; i < limitExclusive - 1; i++)
            {
                if (_data[i] == '<' && _data[i+1] == '<') depth++;
                else if (_data[i] == '>' && _data[i+1] == '>') { depth--; if (depth == 0) { end = i; return; } }
            }
        }

        private int FindStreamStart(int objStart, int dictEnd, int objectLimit)
        {
            if (dictEnd < 0) return -1;
            // Look for 'stream' keyword after the dictionary
            int searchFrom = dictEnd;
            int searchTo   = Math.Min(objectLimit, dictEnd + 20);
            if (searchTo <= searchFrom) return -1;
            string near    = Encoding.ASCII.GetString(_data, searchFrom, searchTo - searchFrom);
            int idx        = near.IndexOf("stream", StringComparison.Ordinal);
            if (idx < 0) return -1;

            int pos = searchFrom + idx + 6; // after 'stream'
            // Skip CRLF or LF
            if (pos < _data.Length && _data[pos] == '\r') pos++;
            if (pos < _data.Length && _data[pos] == '\n') pos++;
            return pos;
        }

        private int GetStreamLength(PdfObject obj, int streamStart, int objectLimit)
        {
            string lenVal = obj.Get("/Length");
            if (!string.IsNullOrEmpty(lenVal))
            {
                // /Length may be a direct integer or an indirect reference
                var mRef = Regex.Match(lenVal, @"(\d+)\s+\d+\s+R");
                if (mRef.Success)
                {
                    var lenObj = GetObject(int.Parse(mRef.Groups[1].Value));
                    lenVal = lenObj?.Content;
                    // Extract the integer from the object content
                    var mInt = Regex.Match(lenVal ?? "", @"\d+\s+\d+\s+obj\s+(\d+)");
                    if (mInt.Success) lenVal = mInt.Groups[1].Value;
                }
                if (int.TryParse(lenVal?.Trim(), out int len) && len > 0) return len;
            }

            // Fallback: scan for 'endstream'
            int endStreamIdx = FindMarker(streamStart, "endstream", objectLimit);
            if (endStreamIdx >= 0)
            {
                int len = endStreamIdx - streamStart;
                if (len > 0 && _data[endStreamIdx - 1] == '\n') len--;
                if (len > 0 && _data[endStreamIdx - 1] == '\r') len--;
                return len;
            }
            return 0;
        }

        private int FindEndObj(int objStart, int streamStart, int streamLen)
        {
            // Skip past stream body to avoid false matches of 'endobj' inside binary
            int searchFrom = streamStart > 0 ? streamStart + streamLen : objStart;
            int idx = FindMarker(searchFrom, "endobj", _data.Length);
            if (idx >= 0) return idx + "endobj".Length;
            return -1;
        }

        private int FindMarker(int searchFrom, string markerText, int limitExclusive)
        {
            byte[] marker = Encoding.ASCII.GetBytes(markerText);
            int max = Math.Min(limitExclusive, _data.Length);
            for (int i = searchFrom; i <= max - marker.Length; i++)
            {
                bool match = true;
                for (int k = 0; k < marker.Length && match; k++)
                    if (_data[i + k] != marker[k]) match = false;
                if (match) return i;
            }
            return -1;
        }

        // ─── Page tree ────────────────────────────────────────────────────────

        public List<int> GetPageObjectIds()
        {
            var ids = new List<int>();
            if (RootObjectId == 0) return ids;

            var root = GetObject(RootObjectId);
            if (root == null) return ids;

            string pagesRef = root.Get("/Pages");
            var m = Regex.Match(pagesRef ?? "", @"(\d+)\s+\d+\s+R");
            if (m.Success)
                WalkPageTree(int.Parse(m.Groups[1].Value), ids);

            return ids;
        }

        private void WalkPageTree(int nodeId, List<int> ids)
        {
            var node = GetObject(nodeId);
            if (node == null) return;

            string type = node.Get("/Type");
            if (type == "/Page")
            {
                ids.Add(nodeId);
            }
            else if (type == "/Pages")
            {
                string kids = node.Get("/Kids") ?? "";
                foreach (Match m in Regex.Matches(kids, @"(\d+)\s+\d+\s+R"))
                    WalkPageTree(int.Parse(m.Groups[1].Value), ids);
            }
        }

        // ─── Dictionary parser ────────────────────────────────────────────────

        private static readonly Regex DictEntry = new(
            @"/(\w+)\s*((?:\d+\s+\d+\s+R)|(?:\[(?:[^\[\]]|\[[^\[\]]*\])*\])" +
            @"|(?:<<(?:[^<>]|<<[^<>]*>>)*>>)|(?:/[^\s/<>\[\]]+)" +
            @"|(?:\([^)\\]*(?:\\.[^)\\]*)*\))|(?:[^\s/<>\[\]]+))" +
            @"|/(\w+)(?=[/<>\[\]])",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static void ParseDictionaryInto(Dictionary<string, string> dict, string dictStr)
        {
            foreach (Match m in DictEntry.Matches(dictStr))
            {
                if (m.Groups[1].Success)
                    dict["/" + m.Groups[1].Value] = m.Groups[2].Value;
                else if (m.Groups[3].Success)
                    dict["/" + m.Groups[3].Value] = "";
            }
        }
    }
}
