using System;
using System.Collections.Generic;
using System.Linq;

namespace DriveDeck.Core
{
    public enum ElmStatus { Ok, NoData, Error }

    /// <summary>One reassembled message from one ECU (header is e.g. "7E8", "18DAF110", or "" when headers are off).</summary>
    public sealed class EcuMessage
    {
        public string Header { get; set; }
        public byte[] Payload { get; set; }
        public override string ToString() { return (Header ?? "") + ": " + Hex.ToHex(Payload); }
    }

    public sealed class ElmResult
    {
        public ElmStatus Status { get; set; }
        public string ErrorText { get; set; }
        public List<EcuMessage> Messages { get; set; }
        public List<string> TextLines { get; set; }

        public ElmResult()
        {
            Messages = new List<EcuMessage>();
            TextLines = new List<string>();
        }
    }

    /// <summary>
    /// Parses raw ELM327 output (everything before the '&gt;' prompt). Handles echo, SEARCHING..., error words,
    /// CAN 11-bit and 29-bit headers with ISO-TP (multi-frame) reassembly, and the headers-off "0: .. 1: .." format.
    /// It never throws on malformed adapter output; garbage lines are ignored.
    /// </summary>
    public static class ElmResponseParser
    {
        public const int MaxResponseChars = 16 * 1024;
        public const int MaxIsoTpLength = 4095;

        private static readonly string[] ErrorWords =
        {
            "UNABLE TO CONNECT", "CAN ERROR", "BUS ERROR", "BUS BUSY", "BUFFER FULL", "FB ERROR", "DATA ERROR",
            "<RX ERROR", "RX ERROR", "LV RESET", "ACT ALERT", "STOPPED", "ERROR", "?"
        };

        public static ElmResult Parse(string raw, string sentCommand)
        {
            var result = new ElmResult { Status = ElmStatus.Ok };
            if (raw == null) raw = string.Empty;
            if (raw.Length > MaxResponseChars) raw = raw.Substring(0, MaxResponseChars);

            string echo = (sentCommand ?? string.Empty).Replace(" ", "").Trim().ToUpperInvariant();
            var lines = raw.Replace('>', '\n').Replace("\0", "")
                           .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .Where(l => l.Length > 0)
                           .ToList();

            var dataLines = new List<string>();
            foreach (var line in lines)
            {
                string upper = line.ToUpperInvariant();
                if (echo.Length > 0 && upper.Replace(" ", "") == echo) continue;
                if (upper.StartsWith("SEARCHING")) continue;
                if (upper.StartsWith("BUS INIT")) { if (upper.Contains("ERROR")) { result.Status = ElmStatus.Error; result.ErrorText = line; } continue; }
                if (upper == "OK") { result.TextLines.Add(line); continue; }
                if (upper == "NO DATA") { if (result.Status == ElmStatus.Ok) result.Status = ElmStatus.NoData; continue; }
                string err = ErrorWords.FirstOrDefault(w => upper == w || (w.Length > 1 && upper.Contains(w)));
                if (err != null) { result.Status = ElmStatus.Error; result.ErrorText = line; continue; }
                if (IsDataLine(upper)) dataLines.Add(upper);
                else result.TextLines.Add(line);
            }

            if (dataLines.Count > 0)
            {
                result.Messages = Reassemble(dataLines);
                if (result.Messages.Count > 0 && result.Status == ElmStatus.NoData) result.Status = ElmStatus.Ok;
            }
            else if (result.Status == ElmStatus.Ok && result.TextLines.Count == 0)
            {
                result.Status = ElmStatus.NoData;
            }
            return result;
        }

        private static bool IsDataLine(string upper)
        {
            // Data lines are hex tokens, optionally starting with a "N:" frame index (headers-off multi-frame).
            string s = upper;
            int colon = s.IndexOf(':');
            if (colon > 0 && colon <= 2) s = s.Substring(colon + 1);
            bool any = false;
            foreach (char c in s)
            {
                if (c == ' ') continue;
                if (!Hex.IsHexChar(c)) return false;
                any = true;
            }
            return any;
        }

        private static List<EcuMessage> Reassemble(List<string> lines)
        {
            var messages = new List<EcuMessage>();

            // Headers-off multi-frame form: "00A", "0: 43 04 ...", "1: ..."
            if (lines.Any(l => l.IndexOf(':') > 0))
            {
                int declared = -1;
                var buf = new List<byte>();
                foreach (var l in lines)
                {
                    int colon = l.IndexOf(':');
                    if (colon < 0)
                    {
                        string t = l.Replace(" ", "");
                        if (t.Length == 3 && declared < 0)
                        {
                            int len;
                            if (int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out len)) declared = len;
                        }
                        else
                        {
                            var b = Hex.TryParseBytes(l);
                            if (b != null) messages.Add(new EcuMessage { Header = "", Payload = b });
                        }
                        continue;
                    }
                    var bytes = Hex.TryParseBytes(l.Substring(colon + 1));
                    if (bytes != null) buf.AddRange(bytes);
                }
                if (buf.Count > 0)
                {
                    if (declared > 0 && declared <= buf.Count) buf = buf.Take(declared).ToList();
                    messages.Add(new EcuMessage { Header = "", Payload = buf.ToArray() });
                }
                return messages;
            }

            // Tokenise and detect headers.
            var frames = new List<KeyValuePair<string, byte[]>>();
            bool sawHeader = false;
            foreach (var l in lines)
            {
                var tokens = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (tokens[0].Length == 3 && Hex.IsHexToken(tokens[0]) && tokens.Length >= 2)
                {
                    var b = Hex.TryParseBytes(string.Join("", tokens.Skip(1)));
                    if (b == null) continue;
                    frames.Add(new KeyValuePair<string, byte[]>(tokens[0], b));
                    sawHeader = true;
                }
                else if (tokens.Length >= 6 && tokens[0] == "18" && (tokens[1] == "DA" || tokens[1] == "DB"))
                {
                    var b = Hex.TryParseBytes(string.Join("", tokens.Skip(4)));
                    if (b == null) continue;
                    frames.Add(new KeyValuePair<string, byte[]>(string.Join("", tokens.Take(4)), b));
                    sawHeader = true;
                }
                else
                {
                    var b = Hex.TryParseBytes(l);
                    if (b != null) frames.Add(new KeyValuePair<string, byte[]>("", b));
                }
            }

            if (!sawHeader)
            {
                foreach (var f in frames) messages.Add(new EcuMessage { Header = "", Payload = f.Value });
                return messages;
            }

            // ISO-TP reassembly per header (PCI byte is present when headers are on).
            var pending = new Dictionary<string, List<byte>>();
            var expected = new Dictionary<string, int>();
            foreach (var f in frames)
            {
                string h = f.Key;
                byte[] d = f.Value;
                if (h.Length == 0 || d.Length == 0) continue;
                int type = d[0] >> 4;
                if (type == 0)
                {
                    int len = d[0] & 0x0F;
                    if (len == 0 || len > d.Length - 1) continue;
                    var payload = new byte[len];
                    Array.Copy(d, 1, payload, 0, len);
                    messages.Add(new EcuMessage { Header = h, Payload = payload });
                }
                else if (type == 1 && d.Length >= 2)
                {
                    int len = ((d[0] & 0x0F) << 8) | d[1];
                    if (len == 0 || len > MaxIsoTpLength) continue;
                    var list = new List<byte>(len);
                    for (int i = 2; i < d.Length && list.Count < len; i++) list.Add(d[i]);
                    pending[h] = list;
                    expected[h] = len;
                }
                else if (type == 2)
                {
                    List<byte> list;
                    int len;
                    if (!pending.TryGetValue(h, out list) || !expected.TryGetValue(h, out len)) continue;
                    for (int i = 1; i < d.Length && list.Count < len; i++) list.Add(d[i]);
                    if (list.Count >= len)
                    {
                        messages.Add(new EcuMessage { Header = h, Payload = list.ToArray() });
                        pending.Remove(h);
                        expected.Remove(h);
                    }
                }
                // type 3 = flow control, ignored.
            }
            // Incomplete multi-frame messages are dropped: a partial DTC list would be misleading.
            return messages;
        }
    }
}
