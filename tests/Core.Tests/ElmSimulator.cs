using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;

namespace DriveDeck.Tests
{
    /// <summary>
    /// Behaves like a clone ELM327 v1.5 plugged into a SEAT Leon Mk4 (ISO 15765-4 CAN 11-bit 500k):
    /// engine (7E8) and gearbox (7E9) answer broadcasts, headers on, ISO-TP multi-frame for fault codes.
    /// </summary>
    public sealed class ElmSimulator : IObdTransport
    {
        public readonly List<string> Sent = new List<string>();
        public bool Headers = false;
        public string Header = "7DF";
        public int FailAfter = int.MaxValue;   // throw IOException after N commands (simulates BT drop)
        public bool MilOn = true;
        public string Dtc03 = "7E8 10 08 43 03 01 33 03 01\r7E8 21 06 04 55 55 55 55 55\r";
        public string Name { get { return "Simulator"; } }

        public Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(command);
            if (Sent.Count > FailAfter) throw new IOException("link lost");
            return Task.FromResult(Reply(command.Trim().ToUpperInvariant()) + "\r\r>");
        }

        private string Reply(string c)
        {
            if (c == "ATZ") { Headers = false; Header = "7DF"; return "\r\rELM327 v1.5"; }
            if (c == "ATH1") { Headers = true; return "OK"; }
            if (c == "ATH0") { Headers = false; return "OK"; }
            if (c == "ATRV") return "12.6V";
            if (c == "ATDPN") return "A6";
            if (c.StartsWith("ATSH")) { Header = c.Substring(4); return "OK"; }
            if (c.StartsWith("AT")) return "OK";

            if (Header == "7E0")
            {
                if (c == "2211BD") return F("7E8", "62 11 BD 00 8C");          // VAG-style DID read
                if (c == "2E11BD00") return "7E8 03 7F 2E 33";
                return "NO DATA";
            }

            switch (c)
            {
                case "0100": return F("7E8", "41 00 9E 3F 80 03") + "\r" + F("7E9", "41 00 98 18 80 11");
                case "0120": return F("7E8", "41 20 00 02 A0 01");
                case "0140": return F("7E8", "41 40 44 80 00 14");
                case "0101": return F("7E8", MilOn ? "41 01 83 07 E5 00" : "41 01 00 07 E5 00");
                case "010C": return F("7E8", "41 0C 1A F8");
                case "010D": return F("7E8", "41 0D 32") + "\r" + F("7E9", "41 0D 32");
                case "0105": return F("7E8", "41 05 7B");
                case "010B": return F("7E8", "41 0B 96");
                case "0133": return F("7E8", "41 33 64");
                case "0110": return F("7E8", "41 10 05 DC");
                case "015C": return "NO DATA";
                case "03": return MilOn ? Dtc03 + "7E9 02 43 00" : "7E8 02 43 00\r7E9 02 43 00";
                case "07": return "7E8 04 47 01 01 71";
                case "0A": return "7E8 02 4A 00";
                default: return "NO DATA";
            }
        }

        private string F(string hdr, string data)
        {
            var bytes = Hex.TryParseBytes(data);
            if (!Headers) return data;
            return hdr + " " + bytes.Length.ToString("X2") + " " + data;
        }

        public void Dispose() { }
    }
}
