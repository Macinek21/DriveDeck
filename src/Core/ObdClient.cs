using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DriveDeck.Core
{
    /// <summary>A byte stream to an ELM327: send one command line, get everything up to the '&gt;' prompt.</summary>
    public interface IObdTransport : IDisposable
    {
        /// <summary>Sends <paramref name="command"/> + CR and returns the raw reply up to the prompt.
        /// Throws TimeoutException when no prompt arrives in time and IOException when the link drops.</summary>
        Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct);
        string Name { get; }
    }

    public sealed class ObdReading
    {
        public bool Ok { get; set; }
        public bool Unsupported { get; set; }
        public double Value { get; set; }
        public string Error { get; set; }
        public static ObdReading Fail(string err, bool unsupported = false) { return new ObdReading { Ok = false, Error = err, Unsupported = unsupported, Value = double.NaN }; }
    }

    /// <summary>ELM327 protocol logic. Read-only by design: every request passes <see cref="CommandGuard"/>.</summary>
    public sealed class ObdClient
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2.5);
        public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(12);
        private const string DefaultHeader = "7DF";

        private readonly IObdTransport _t;
        private string _currentHeader = DefaultHeader;
        private readonly Dictionary<string, Formula> _formulaCache = new Dictionary<string, Formula>();

        public string AdapterVersion { get; private set; }
        public string ProtocolNumber { get; private set; }
        public HashSet<byte> SupportedPids { get; private set; }

        public ObdClient(IObdTransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _t = transport;
            SupportedPids = new HashSet<byte>();
        }

        private async Task<string> AtAsync(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            if (!CommandGuard.IsAllowedAtCommand(cmd)) throw new InvalidOperationException("AT command refused: " + cmd);
            return await _t.SendAsync(cmd, timeout, ct).ConfigureAwait(false);
        }

        /// <param name="protocol">ELM protocol digit: "0" automatic, "6" = ISO 15765-4 CAN 11-bit 500 kbaud (Leon Mk4).</param>
        public async Task InitializeAsync(string protocol, CancellationToken ct)
        {
            if (protocol != "0" && protocol != "6" && protocol != "7" && protocol != "8" && protocol != "9") protocol = "0";
            string ver = await AtAsync("ATZ", TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            AdapterVersion = ExtractVersion(ver);
            await AtAsync("ATE0", DefaultTimeout, ct).ConfigureAwait(false);
            await AtAsync("ATL0", DefaultTimeout, ct).ConfigureAwait(false);
            await AtAsync("ATS1", DefaultTimeout, ct).ConfigureAwait(false);
            await AtAsync("ATH1", DefaultTimeout, ct).ConfigureAwait(false);
            await AtAsync("ATAT1", DefaultTimeout, ct).ConfigureAwait(false);
            await AtAsync("ATSP" + protocol, DefaultTimeout, ct).ConfigureAwait(false);
            _currentHeader = DefaultHeader;

            // First real request triggers the protocol search; give it time.
            string raw = await _t.SendAsync("0100", SearchTimeout, ct).ConfigureAwait(false);
            var res = ElmResponseParser.Parse(raw, "0100");
            if (res.Status != ElmStatus.Ok)
                throw new ObdException(res.Status == ElmStatus.NoData
                    ? "The car did not answer. Is the ignition on?"
                    : "Adapter error: " + (res.ErrorText ?? "unknown"));
            AddSupported(res, 0x00);

            string dpn = await AtAsync("ATDPN", DefaultTimeout, ct).ConfigureAwait(false);
            ProtocolNumber = (dpn ?? "").Replace(">", "").Trim();

            // Walk the "supported PIDs" chain 0x20, 0x40 ... while advertised.
            for (byte basePid = 0x20; basePid <= 0xA0; basePid += 0x20)
            {
                if (!SupportedPids.Contains(basePid)) break;
                string cmd = "01" + basePid.ToString("X2");
                var r = ElmResponseParser.Parse(await _t.SendAsync(cmd, DefaultTimeout, ct).ConfigureAwait(false), cmd);
                if (r.Status != ElmStatus.Ok) break;
                AddSupported(r, basePid);
            }
        }

        private static string ExtractVersion(string raw)
        {
            if (raw == null) return "";
            var line = raw.Split(new[] { '\r', '\n', '>' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim()).FirstOrDefault(l => l.ToUpperInvariant().Contains("ELM"));
            if (line == null) return "";
            return line.Length > 40 ? line.Substring(0, 40) : line;
        }

        private void AddSupported(ElmResult res, byte basePid)
        {
            foreach (var m in res.Messages)
            {
                var p = m.Payload;
                if (p.Length < 6 || p[0] != 0x41 || p[1] != basePid) continue;
                for (int i = 0; i < 32; i++)
                {
                    int byteIdx = 2 + i / 8;
                    int bit = 7 - (i % 8);
                    if ((p[byteIdx] >> bit & 1) == 1) SupportedPids.Add((byte)(basePid + i + 1));
                }
            }
        }

        public bool IsStandardPidSupported(ValueDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.Request)) return false;
            var b = Hex.TryParseBytes(def.Request);
            if (b == null || b.Length != 2 || b[0] != 0x01) return true; // custom requests: unknown, try them
            return SupportedPids.Contains(b[1]);
        }

        private async Task EnsureHeaderAsync(string header, CancellationToken ct)
        {
            string want = string.IsNullOrEmpty(header) ? DefaultHeader : header.ToUpperInvariant();
            if (want == _currentHeader) return;
            await AtAsync("ATSH" + want, DefaultTimeout, ct).ConfigureAwait(false);
            _currentHeader = want;
        }

        private Formula GetFormula(string text)
        {
            Formula f;
            if (_formulaCache.TryGetValue(text, out f)) return f;
            string err;
            if (!Formula.TryParse(text, out f, out err)) f = null;
            _formulaCache[text] = f;
            return f;
        }

        public async Task<ObdReading> ReadAsync(ValueDefinition def, CancellationToken ct)
        {
            byte[] req = null; string reason = null;
            if (def == null || !CommandGuard.IsAllowedRequest(def.Request, out req, out reason)) return ObdReading.Fail(reason ?? "No request");
            if (!CommandGuard.IsAllowedHeader(def.Header, out reason)) return ObdReading.Fail(reason);
            var formula = GetFormula(def.Formula ?? "");
            if (formula == null) return ObdReading.Fail("Invalid formula");

            await EnsureHeaderAsync(def.Header, ct).ConfigureAwait(false);
            string cmd = Hex.ToHex(req, false);
            string raw = await _t.SendAsync(cmd, DefaultTimeout, ct).ConfigureAwait(false);
            var res = ElmResponseParser.Parse(raw, cmd);
            if (res.Status == ElmStatus.NoData) return ObdReading.Fail("No data", true);
            if (res.Status == ElmStatus.Error) return ObdReading.Fail(res.ErrorText);
            return Evaluate(res, req, formula);
        }

        /// <summary>Picks the matching positive response (engine ECU first) and applies the formula to the bytes after the echoed request.</summary>
        public static ObdReading Evaluate(ElmResult res, byte[] req, Formula formula)
        {
            byte positive = (byte)(req[0] + 0x40);
            var candidates = res.Messages
                .Where(m => m.Payload.Length >= req.Length && m.Payload[0] == positive && StartsWithParams(m.Payload, req))
                .OrderBy(m => m.Header == "7E8" ? 0 : 1)
                .ToList();
            if (candidates.Count == 0)
            {
                bool negative = res.Messages.Any(m => m.Payload.Length >= 3 && m.Payload[0] == 0x7F && m.Payload[1] == req[0]);
                return ObdReading.Fail(negative ? "ECU rejected the request" : "Unexpected response", negative);
            }
            var payload = candidates[0].Payload;
            var data = new byte[payload.Length - req.Length];
            Array.Copy(payload, req.Length, data, 0, data.Length);
            if (formula.HighestVariable >= data.Length) return ObdReading.Fail("Response too short");
            double v = formula.Evaluate(data);
            if (double.IsNaN(v) || double.IsInfinity(v)) return ObdReading.Fail("Formula gave no number");
            return new ObdReading { Ok = true, Value = v };
        }

        private static bool StartsWithParams(byte[] payload, byte[] req)
        {
            for (int i = 1; i < req.Length; i++) if (payload[i] != req[i]) return false;
            return true;
        }

        public async Task<List<Dtc>> ReadDtcsAsync(bool includePermanent, CancellationToken ct)
        {
            await EnsureHeaderAsync(DefaultHeader, ct).ConfigureAwait(false);
            var all = new List<Dtc>();
            var modes = new List<KeyValuePair<string, DtcKind>>
            {
                new KeyValuePair<string, DtcKind>("03", DtcKind.Stored),
                new KeyValuePair<string, DtcKind>("07", DtcKind.Pending),
            };
            if (includePermanent) modes.Add(new KeyValuePair<string, DtcKind>("0A", DtcKind.Permanent));
            foreach (var mode in modes)
            {
                byte expect = (byte)(Convert.ToByte(mode.Key, 16) + 0x40);
                var res = ElmResponseParser.Parse(await _t.SendAsync(mode.Key, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false), mode.Key);
                if (res.Status != ElmStatus.Ok) continue;
                foreach (var m in res.Messages.Where(x => x.Payload.Length > 0 && x.Payload[0] == expect))
                    all.AddRange(DtcDecoder.DecodeResponse(m.Payload, mode.Value, EcuName(m.Header)));
            }
            return all.Distinct().ToList();
        }

        public static string EcuName(string header)
        {
            switch ((header ?? "").ToUpperInvariant())
            {
                case "7E8": return "Engine";
                case "7E9": return "Gearbox";
                case "7EA": return "ECU 3";
                case "": return "";
                default: return header;
            }
        }

        /// <summary>Returns MIL state from mode 01 PID 01, or null when unavailable.</summary>
        public async Task<bool?> ReadMilAsync(CancellationToken ct)
        {
            await EnsureHeaderAsync(DefaultHeader, ct).ConfigureAwait(false);
            var res = ElmResponseParser.Parse(await _t.SendAsync("0101", DefaultTimeout, ct).ConfigureAwait(false), "0101");
            var m = res.Messages.FirstOrDefault(x => x.Payload.Length >= 3 && x.Payload[0] == 0x41 && x.Payload[1] == 0x01);
            if (m == null) return null;
            bool mil; int count;
            DtcDecoder.DecodeMonitorStatus(m.Payload[2], out mil, out count);
            return mil;
        }

        public async Task<ObdReading> ReadAdapterVoltageAsync(CancellationToken ct)
        {
            string raw = await AtAsync("ATRV", DefaultTimeout, ct).ConfigureAwait(false);
            return ParseVoltage(raw);
        }

        public static ObdReading ParseVoltage(string raw)
        {
            if (raw == null) return ObdReading.Fail("No reply");
            string s = raw.Replace(">", "").Trim().TrimEnd('V', 'v').Trim();
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v < 40)
                return new ObdReading { Ok = true, Value = v };
            return ObdReading.Fail("Unreadable voltage");
        }
    }

    public sealed class ObdException : Exception
    {
        public ObdException(string message) : base(message) { }
    }
}
