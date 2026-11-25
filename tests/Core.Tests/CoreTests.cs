using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;
using Xunit;

namespace DriveDeck.Tests
{
    public class FormulaTests
    {
        [Theory]
        [InlineData("(A*256+B)/4", new byte[] { 0x1A, 0xF8 }, 1726)]
        [InlineData("A-40", new byte[] { 0x7B }, 83)]
        [InlineData("A*100/128-100", new byte[] { 0x80 }, 0)]
        [InlineData("signed8(A)", new byte[] { 0xFF }, -1)]
        [InlineData("signed16(A*256+B)/10", new byte[] { 0xFF, 0x9C }, -10)]
        [InlineData("bit(A,7)", new byte[] { 0x80 }, 1)]
        [InlineData("max(A,10)-min(B,3)", new byte[] { 5, 9 }, 7)]
        [InlineData("-A+2*3", new byte[] { 1 }, 5)]
        [InlineData("2*(3+4)%5", new byte[] { }, 4)]
        public void Evaluates(string f, byte[] data, double expected)
        {
            Assert.Equal(expected, Formula.Parse(f).Evaluate(data), 6);
        }

        [Theory]
        [InlineData("")]
        [InlineData("A+")]
        [InlineData("(A")]
        [InlineData("Z*2")]
        [InlineData("System.IO.File(A)")]
        [InlineData("A;B")]
        [InlineData("abs(A,B)")]
        [InlineData("1..2")]
        public void RejectsInvalid(string f)
        {
            Formula x; string err;
            Assert.False(Formula.TryParse(f, out x, out err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void RejectsTooLongAndTooDeep()
        {
            Formula x; string err;
            Assert.False(Formula.TryParse(new string('1', 300), out x, out err));
            Assert.False(Formula.TryParse(new string('(', 60) + "1" + new string(')', 60), out x, out err));
            Assert.False(Formula.TryParse(new string('-', 60) + "1", out x, out err));
        }

        [Fact]
        public void MissingBytesAndDivisionByZeroGiveNaN()
        {
            Assert.True(double.IsNaN(Formula.Parse("A*256+B").Evaluate(new byte[] { 1 })));
            Assert.True(double.IsNaN(Formula.Parse("A/B").Evaluate(new byte[] { 1, 0 })));
        }
    }

    public class ParserTests
    {
        [Fact]
        public void ParsesMultiEcuSingleFrames()
        {
            var r = ElmResponseParser.Parse("7E8 03 41 0D 32\r7E9 03 41 0D 32\r\r>", "010D");
            Assert.Equal(ElmStatus.Ok, r.Status);
            Assert.Equal(2, r.Messages.Count);
            Assert.Equal("7E8", r.Messages[0].Header);
            Assert.Equal(new byte[] { 0x41, 0x0D, 0x32 }, r.Messages[0].Payload);
        }

        [Fact]
        public void ReassemblesIsoTp()
        {
            var r = ElmResponseParser.Parse("7E8 10 08 43 03 01 33 03 01\r7E8 21 06 04 55 55 55 55 55\r>", "03");
            Assert.Single(r.Messages);
            Assert.Equal(new byte[] { 0x43, 0x03, 0x01, 0x33, 0x03, 0x01, 0x06, 0x04 }, r.Messages[0].Payload);
        }

        [Fact]
        public void DropsIncompleteIsoTp()
        {
            var r = ElmResponseParser.Parse("7E8 10 08 43 03 01 33 03 01\r>", "03");
            Assert.Empty(r.Messages);
        }

        [Fact]
        public void ParsesHeadersOffMultiLine()
        {
            var r = ElmResponseParser.Parse("008\r0: 43 03 01 33 03 01\r1: 06 04 00 00 00 00 00\r>", "03");
            Assert.Single(r.Messages);
            Assert.Equal(8, r.Messages[0].Payload.Length);
        }

        [Fact]
        public void ParsesTwentyNineBitHeaders()
        {
            var r = ElmResponseParser.Parse("18 DA F1 10 03 41 0D 32\r>", "010D");
            Assert.Equal("18DAF110", r.Messages[0].Header);
            Assert.Equal(new byte[] { 0x41, 0x0D, 0x32 }, r.Messages[0].Payload);
        }

        [Theory]
        [InlineData("SEARCHING...\rUNABLE TO CONNECT\r>", ElmStatus.Error)]
        [InlineData("NO DATA\r>", ElmStatus.NoData)]
        [InlineData("CAN ERROR\r>", ElmStatus.Error)]
        [InlineData("?\r>", ElmStatus.Error)]
        [InlineData("STOPPED\r>", ElmStatus.Error)]
        [InlineData("", ElmStatus.NoData)]
        public void DetectsStatus(string raw, ElmStatus expected)
        {
            Assert.Equal(expected, ElmResponseParser.Parse(raw, "010C").Status);
        }

        [Fact]
        public void IgnoresEchoAndGarbage()
        {
            var r = ElmResponseParser.Parse("010C\rSEARCHING...\r\u0001ÿ%%\r7E8 04 41 0C 1A F8\r>", "010C");
            Assert.Single(r.Messages);
        }

        [Fact]
        public void SurvivesHugeInput()
        {
            var raw = string.Concat(Enumerable.Repeat("7E8 10 FF 43 01 02 03 04 05\r", 5000));
            var r = ElmResponseParser.Parse(raw, "03");
            Assert.NotNull(r);
        }
    }

    public class GuardTests
    {
        [Theory]
        [InlineData("01 0C", true)]
        [InlineData("22 11 BD", true)]
        [InlineData("03", true)]
        [InlineData("19 02 08", true)]
        [InlineData("04", false)]        // clear codes
        [InlineData("14 FF FF FF", false)]
        [InlineData("2E 11 BD 00", false)] // write data
        [InlineData("27 01", false)]     // security access
        [InlineData("31 01 02 03", false)]
        [InlineData("10 03", false)]
        [InlineData("11 01", false)]
        [InlineData("19 04 00", false)]
        [InlineData("01 0C\rATMA", false)]
        [InlineData("01 0C;04", false)]
        [InlineData("010", false)]
        [InlineData("01 02 03 04 05 06 07 08", false)]
        public void Requests(string req, bool allowed)
        {
            byte[] b; string why;
            Assert.Equal(allowed, CommandGuard.IsAllowedRequest(req, out b, out why));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("7E0", true)]
        [InlineData("714", true)]
        [InlineData("7E0\r", false)]
        [InlineData("123", false)]
        [InlineData("7E0ATMA", false)]
        [InlineData("18DA10F1", false)]
        public void Headers(string h, bool allowed)
        {
            string why;
            Assert.Equal(allowed, CommandGuard.IsAllowedHeader(h, out why));
        }

        [Theory]
        [InlineData("ATZ", true)]
        [InlineData("ATSH7E0", true)]
        [InlineData("ATSH100", false)]
        [InlineData("ATMA", false)]
        [InlineData("ATPP 0C SV 00", false)]
        [InlineData("ATSH7E0\rATMA", false)]
        public void AtCommands(string c, bool allowed)
        {
            Assert.Equal(allowed, CommandGuard.IsAllowedAtCommand(c));
        }
    }

    public class DtcTests
    {
        [Theory]
        [InlineData(0x01, 0x33, "P0133")]
        [InlineData(0xC1, 0x00, "U0100")]
        [InlineData(0x42, 0x34, "C0234")]
        [InlineData(0x91, 0x01, "B1101")]
        [InlineData(0x24, 0x2F, "P242F")]
        public void Decodes(byte hi, byte lo, string code)
        {
            Assert.Equal(code, DtcDecoder.Decode(hi, lo));
        }

        [Fact]
        public void DecodesCanResponseWithCount()
        {
            var list = DtcDecoder.DecodeResponse(new byte[] { 0x43, 0x03, 0x01, 0x33, 0x03, 0x01, 0x06, 0x04 }, DtcKind.Stored, "Engine");
            Assert.Equal(new[] { "P0133", "P0301", "P0604" }, list.Select(d => d.Code));
        }

        [Fact]
        public void DescribesUnknownByGroup()
        {
            Assert.Equal("Cylinder 1 misfire detected", DtcDescriptions.Describe("P0301"));
            Assert.Contains("Manufacturer-specific", DtcDescriptions.Describe("P1234"));
            Assert.Contains("Network", DtcDescriptions.Describe("U3000"));
        }
    }

    public class ObdClientTests
    {
        private static async Task<ObdClient> Init(ElmSimulator sim)
        {
            var c = new ObdClient(sim);
            await c.InitializeAsync("6", CancellationToken.None);
            return c;
        }

        [Fact]
        public async Task InitialisesAndReadsSupportedPids()
        {
            var sim = new ElmSimulator();
            var c = await Init(sim);
            Assert.Equal("ELM327 v1.5", c.AdapterVersion);
            Assert.Equal("A6", c.ProtocolNumber);
            foreach (byte pid in new byte[] { 0x04, 0x05, 0x0C, 0x0D, 0x11, 0x2F, 0x33, 0x42, 0x46, 0x49, 0x5C, 0x5E })
                Assert.Contains(pid, c.SupportedPids);
            Assert.DoesNotContain((byte)0x0A, c.SupportedPids);
            Assert.Contains("ATSP6", sim.Sent);
        }

        [Fact]
        public async Task ReadsStandardValues()
        {
            var c = await Init(new ElmSimulator());
            var rpm = await c.ReadAsync(PidCatalog.Find("obd:0C", null), CancellationToken.None);
            Assert.True(rpm.Ok); Assert.Equal(1726, rpm.Value);
            var cool = await c.ReadAsync(PidCatalog.Find("obd:05", null), CancellationToken.None);
            Assert.Equal(83, cool.Value);
            var oil = await c.ReadAsync(PidCatalog.Find("obd:5C", null), CancellationToken.None);
            Assert.False(oil.Ok); Assert.True(oil.Unsupported);
            var v = await c.ReadAdapterVoltageAsync(CancellationToken.None);
            Assert.Equal(12.6, v.Value, 3);
        }

        [Fact]
        public async Task CustomDidSwitchesHeaderAndBack()
        {
            var sim = new ElmSimulator();
            var c = await Init(sim);
            var def = new ValueDefinition { Key = "custom:x", Name = "Oil", Request = "22 11 BD", Header = "7E0", Formula = "(A*256+B)/10" };
            var r = await c.ReadAsync(def, CancellationToken.None);
            Assert.True(r.Ok); Assert.Equal(14, r.Value, 6);
            Assert.Contains("ATSH7E0", sim.Sent);
            await c.ReadAsync(PidCatalog.Find("obd:0C", null), CancellationToken.None);
            Assert.Equal("ATSH7DF", sim.Sent[sim.Sent.Count - 2]);
        }

        [Fact]
        public async Task BlockedRequestNeverReachesAdapter()
        {
            var sim = new ElmSimulator();
            var c = await Init(sim);
            int before = sim.Sent.Count;
            var r = await c.ReadAsync(new ValueDefinition { Request = "2E 11 BD 00", Header = "7E0", Formula = "A" }, CancellationToken.None);
            Assert.False(r.Ok);
            r = await c.ReadAsync(new ValueDefinition { Request = "01 0C", Header = "7E0\rATMA", Formula = "A" }, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(before, sim.Sent.Count);
        }

        [Fact]
        public async Task ReadsFaultCodesFromAllEcus()
        {
            var c = await Init(new ElmSimulator());
            var dtcs = await c.ReadDtcsAsync(true, CancellationToken.None);
            Assert.Contains(dtcs, d => d.Code == "P0133" && d.Kind == DtcKind.Stored && d.Ecu == "Engine");
            Assert.Contains(dtcs, d => d.Code == "P0604");
            Assert.Contains(dtcs, d => d.Code == "P0171" && d.Kind == DtcKind.Pending);
            Assert.Equal(true, await c.ReadMilAsync(CancellationToken.None));
        }
    }

    public class PollerTests
    {
        [Fact]
        public async Task PublishesValuesDerivedValuesAndFaults()
        {
            var sim = new ElmSimulator();
            var poller = new ObdPoller(ct => Task.FromResult<IObdTransport>(sim), new ObdPollerOptions { MinCycleMs = 20 });
            var values = new Dictionary<string, double>();
            var states = new List<ObdState>();
            IReadOnlyList<Dtc> fresh = null;
            var gotFaults = new TaskCompletionSource<bool>();
            poller.ValueUpdated += (k, v) => { lock (values) values[k] = v; };
            poller.StateChanged += (s, m) => { lock (states) states.Add(s); };
            poller.DtcsChanged += (all, n) => { fresh = n; gotFaults.TrySetResult(true); };
            poller.SetWanted(new[] { PidCatalog.Find("obd:0C", null), PidCatalog.Find(PidCatalog.BoostKey, null), PidCatalog.Find(PidCatalog.ConsumptionKey, null), PidCatalog.Find(PidCatalog.AdapterVoltageKey, null) });
            poller.Start();
            await Task.WhenAny(gotFaults.Task, Task.Delay(5000));
            await Task.Delay(300);
            await poller.StopAsync();

            Assert.Contains(ObdState.Connected, states);
            lock (values)
            {
                Assert.Equal(1726, values["obd:0C"]);
                Assert.Equal(0.5, values[PidCatalog.BoostKey], 6);
                Assert.True(values.ContainsKey(PidCatalog.ConsumptionKey));
                Assert.Equal(12.6, values[PidCatalog.AdapterVoltageKey], 3);
            }
            Assert.NotNull(fresh);
            Assert.Contains(fresh, d => d.Code == "P0133");
        }

        [Fact]
        public async Task ReconnectsAfterLinkDrop()
        {
            int connects = 0;
            var states = new List<ObdState>();
            var poller = new ObdPoller(ct =>
            {
                connects++;
                var sim = new ElmSimulator { FailAfter = connects == 1 ? 15 : int.MaxValue };
                return Task.FromResult<IObdTransport>(sim);
            }, new ObdPollerOptions { MinCycleMs = 10 });
            poller.StateChanged += (s, m) => { lock (states) states.Add(s); };
            poller.SetWanted(new[] { PidCatalog.Find("obd:0C", null) });
            poller.Start();
            await Task.Delay(4500);
            await poller.StopAsync();
            Assert.True(connects >= 2);
            lock (states)
            {
                Assert.Contains(ObdState.Reconnecting, states);
                Assert.Equal(2, states.Count(s => s == ObdState.Connected));
            }
        }
    }

    public class ObdRobustnessTests
    {
        [Fact]
        public async Task FailedFaultReadIsNotNoFaults()
        {
            var sim = new ElmSimulator();
            var c = new ObdClient(sim);
            await c.InitializeAsync("6", CancellationToken.None);
            sim.Dtc03 = "CAN ERROR";
            Assert.Null(await c.ReadDtcsAsync(false, CancellationToken.None));
            sim.Dtc03 = "NO DATA\r";
            sim.Silent03 = true;
            Assert.Null(await c.ReadDtcsAsync(false, CancellationToken.None));
        }

        [Fact]
        public async Task LampOfAnyControlUnitCounts()
        {
            var sim = new ElmSimulator { SecondUnitMil = true, MilOn = false };
            var c = new ObdClient(sim);
            await c.InitializeAsync("6", CancellationToken.None);
            Assert.True(await c.ReadMilAsync(CancellationToken.None));
        }

        [Fact]
        public async Task BoostStopsWhenItsSourceStops()
        {
            var sim = new ElmSimulator();
            var poller = new ObdPoller(ct => Task.FromResult<IObdTransport>(sim), new ObdPollerOptions { MinCycleMs = 20 });
            int boosts = 0;
            poller.ValueUpdated += (k, v) => { if (k == PidCatalog.BoostKey) Interlocked.Increment(ref boosts); };
            poller.SetWanted(PidCatalog.Special.Where(v => v.Key == PidCatalog.BoostKey));
            poller.Start();
            await Task.Delay(800);
            Assert.True(boosts > 0);
            poller.SetWanted(new List<ValueDefinition>());
            await Task.Delay(3400);
            int after = boosts;
            await Task.Delay(500);
            Assert.Equal(after, boosts);
            await poller.StopAsync();
        }

        [Fact]
        public async Task IgnitionOffDoesNotMakeValuesUnsupported()
        {
            var sim = new ElmSimulator();
            var poller = new ObdPoller(ct => Task.FromResult<IObdTransport>(sim), new ObdPollerOptions { MinCycleMs = 20 });
            var unsupported = new List<string>();
            var lost = new TaskCompletionSource<string>();
            poller.ValueUnsupported += k => { lock (unsupported) unsupported.Add(k); };
            poller.StateChanged += (s, m) => { if (s == ObdState.Reconnecting && m.Contains("ignition")) lost.TrySetResult(m); };
            poller.SetWanted(PidCatalog.Standard.Where(v => v.Key == "obd:0C"));
            poller.Start();
            await Task.Delay(400);
            sim.IgnitionOff = true;
            Assert.Same(lost.Task, await Task.WhenAny(lost.Task, Task.Delay(5000)));
            await poller.StopAsync();
            Assert.Empty(unsupported);
        }
    }

    public class VideoTests
    {
        [Fact]
        public void LoopKeepsFootageAnIncidentMayNeed()
        {
            var now = new DateTime(2026, 11, 25, 12, 0, 0);
            var segs = Enumerable.Range(0, 5).Select(i => new SegmentInfo { BaseName = "s" + i, Start = now.AddMinutes(i - 5), End = now.AddMinutes(i - 4), SizeBytes = 100 }).ToList();
            var all = LoopStoragePolicy.SelectForDeletion(segs, 100, 10000, 0, 100);
            var kept = LoopStoragePolicy.SelectForDeletion(segs, 100, 10000, 0, 100, now.AddSeconds(-90));
            Assert.Equal(5, all.Count);
            Assert.Equal(new[] { "s0", "s1", "s2" }, kept.Select(s => s.BaseName));
        }

        [Fact]
        public void ModesFromCameraFormats()
        {
            var l = new List<VideoMode>();
            VideoModes.Add(l, 1920, 1080, 29.97, VideoMode.Stream);
            VideoModes.Add(l, 1920, 1080, 30, VideoMode.Stream);      // same mode twice
            VideoModes.Add(l, 1920, 1088, 60, VideoMode.Profile);
            VideoModes.Add(l, 1600, 1200, 30, VideoMode.Stream);      // 4:3
            VideoModes.Add(l, 3840, 2160, 30, VideoMode.Stream);
            VideoModes.Add(l, 1280, 720, 14.3, VideoMode.Stream);
            Assert.Equal(new[] { "1080p@30", "1080p@60", "2160p@30" }, VideoModes.Sorted(l).Select(m => m.Id));
            Assert.Equal(1080, l.First(m => m.Fps == 60).Height);
            Assert.Equal(VideoModes.Sorted(l).Select(m => m.ToString()), VideoModes.Parse(VideoModes.Serialize(l)).Select(m => m.ToString()));
            Assert.Empty(VideoModes.Parse("x;1,2;;-5,7,9,1"));
        }

        [Fact]
        public void PicksClosestMode()
        {
            var l = VideoModes.Parse("1280,720,30,0;1920,1080,24,0;1920,1080,30,0;1920,1080,60,1;3840,2160,30,0");
            Assert.Equal("1080p@60", VideoModes.Pick(l, "1080p", 60).Id);
            Assert.Equal("2160p@30", VideoModes.Pick(l, "2160p", 60).Id);
            Assert.Equal("1080p@30", VideoModes.Pick(l.Where(m => !m.Special), "1080p", 60).Id);
            Assert.Equal("1080p@30", VideoModes.Pick(l, "1440p", 30).Id);
            Assert.Null(VideoModes.Pick(new List<VideoMode>(), "720p", 30));
            Assert.Equal(20, VideoModes.RecommendedMbit(1920, 1080, 30));
            Assert.Equal(11, VideoModes.RecommendedMbit(1280, 720, 30));
            Assert.Equal(57, VideoModes.RecommendedMbit(3840, 2160, 30));
            Assert.Equal(60, VideoModes.RecommendedMbit(3840, 2160, 60));
        }

        private static byte[] Box(string type, params byte[][] content)
        {
            var body = content.SelectMany(x => x).ToArray();
            var n = body.Length + 8;
            return new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }.Concat(type.Select(c => (byte)c)).Concat(body).ToArray();
        }

        private static byte[] U32(params uint[] v) { return v.SelectMany(x => new[] { (byte)(x >> 24), (byte)(x >> 16), (byte)(x >> 8), (byte)x }).ToArray(); }

        private static byte[] Clip(uint frames, uint seconds)
        {
            var entry = new byte[36]; entry[32] = 1920 >> 8; entry[33] = 1920 & 255; entry[34] = 1080 >> 8; entry[35] = 1080 & 255;
            Func<string, byte[], byte[]> trak = (handler, stbl) => Box("trak", Box("mdia", Box("mdhd", U32(0, 0, 0, 1000, seconds * 1000, 0)),
                Box("hdlr", U32(0, 0), handler.Select(c => (byte)c).ToArray(), U32(0, 0, 0)), Box("minf", Box("stbl", stbl))));
            var video = Box("stsd", U32(0, 1), entry).Concat(Box("stsz", U32(0, 0, frames))).ToArray();
            return Box("ftyp", U32(0)).Concat(Box("mdat", new byte[500])).Concat(Box("moov", trak("soun", Box("stsz", U32(0, 0, 9))), trak("vide", video))).ToArray();
        }

        [Fact]
        public void MeasuresRealFrameRate()
        {
            var i = Mp4Info.Read(new MemoryStream(Clip(1770, 60)));
            Assert.Equal(29.5, i.Fps, 3);
            Assert.Equal(1920, i.Width);
            Assert.Equal(1080, i.Height);
        }

        [Fact]
        public void PaddedFrameRatesAreSeen()
        {
            Func<uint[], Mp4Info> read = sizes =>
            {
                var stbl = Box("stsz", U32(0, 0, (uint)sizes.Length).Concat(U32(sizes)).ToArray());
                var trak = Box("trak", Box("mdia", Box("mdhd", U32(0, 0, 0, 1000, 1000, 0)), Box("hdlr", U32(0, 0), "vide".Select(c => (byte)c).ToArray(), U32(0, 0, 0)), Box("minf", Box("stbl", stbl))));
                return Mp4Info.Read(new MemoryStream(Box("moov", trak)));
            };
            Assert.Equal(0.5, read(Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? 50000u : 200u).ToArray()).TinyShare, 3);
            Assert.Equal(0, read(Enumerable.Range(0, 60).Select(i => 40000u + (uint)i * 100).ToArray()).TinyShare, 3);
        }

        [Fact]
        public void BrokenClipsAreNotMeasured()
        {
            var good = Clip(100, 5);
            Assert.Null(Mp4Info.Read(new MemoryStream(new byte[0])));
            Assert.Null(Mp4Info.Read(new MemoryStream(Box("ftyp", U32(0)).Concat(Box("mdat", new byte[50])).ToArray())));
            for (int n = 0; n < good.Length; n += 3) Mp4Info.Read(new MemoryStream(good.Take(n).ToArray())); // must not throw
            var rnd = new Random(1);
            for (int k = 0; k < 3000; k++)
            {
                var b = (byte[])good.Clone();
                for (int j = 0; j < 4; j++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                Mp4Info.Read(new MemoryStream(b));
            }
        }
    }

    public class StorageAndEventTests
    {
        private const ulong MB = 1024 * 1024;
        private static List<SegmentInfo> Segs(int n, ulong size)
        {
            var t0 = new DateTime(2026, 11, 22, 10, 0, 0);
            return Enumerable.Range(0, n).Select(i => new SegmentInfo
            {
                BaseName = SegmentNaming.NameFor(t0.AddMinutes(i)), Start = t0.AddMinutes(i), End = t0.AddMinutes(i + 1), SizeBytes = size
            }).ToList();
        }

        [Fact]
        public void DeletesOldestToFitCap()
        {
            var s = Segs(10, 100 * MB);
            var del = LoopStoragePolicy.SelectForDeletion(s, 800 * MB, 10000 * MB, 1000 * MB, 100 * MB);
            Assert.Equal(3, del.Count);
            Assert.Equal(s[0].BaseName, del[0].BaseName);
        }

        [Fact]
        public void UnlimitedStillKeepsReserve()
        {
            var s = Segs(10, 100 * MB);
            Assert.Empty(LoopStoragePolicy.SelectForDeletion(s, null, 5000 * MB, 1000 * MB, 100 * MB));
            var del = LoopStoragePolicy.SelectForDeletion(s, null, 1000 * MB, 1000 * MB, 100 * MB);
            Assert.Single(del);
        }

        [Fact]
        public void NeverDeletesSegmentInProgress()
        {
            var s = Segs(2, 100 * MB);
            s[0].InProgress = true;
            var del = LoopStoragePolicy.SelectForDeletion(s, 10 * MB, 0, 1000 * MB, 100 * MB);
            Assert.DoesNotContain(s[0], del);
        }

        [Fact]
        public void EventWindowCoversPreAndPost()
        {
            var s = Segs(6, MB); // 10:00..10:06
            var evt = new DateTime(2026, 11, 22, 10, 3, 20);
            var picked = EventWindow.SegmentsFor(s, evt, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
            Assert.Equal(new[] { "20261122_100200", "20261122_100300" }, picked.Select(p => p.BaseName));
        }

        [Fact]
        public void NamingRoundTrips()
        {
            var t = new DateTime(2026, 11, 22, 14, 3, 12);
            DateTime back;
            Assert.True(SegmentNaming.TryParse(SegmentNaming.NameFor(t), out back));
            Assert.Equal(t, back);
            Assert.False(SegmentNaming.TryParse("..\\evil", out back));
            Assert.True(SegmentNaming.TryParse("20261122_140312 (2)", out back));
            Assert.True(SegmentNaming.TryParse("20261122_140312_impact", out back));
            Assert.False(SegmentNaming.TryParse("20261122_140312 wedding", out back)); // user's own file: never deleted
            Assert.False(SegmentNaming.TryParse("20261122_140312_impact\\..\\x", out back));
        }
    }

    public class ImpactTests
    {
        [Fact]
        public void TiltedPhoneAtRestDoesNotTrigger()
        {
            var d = new ImpactDetector(2.0);
            var t = DateTime.Now;
            for (int i = 0; i < 1000; i++) Assert.False(d.Feed(0.5, -0.7, 0.5, t.AddMilliseconds(20 * i)));
        }

        [Fact]
        public void SpikeTriggersOnceWithCooldown()
        {
            var d = new ImpactDetector(2.0);
            var t = DateTime.Now;
            int i = 0;
            for (; i < 500; i++) d.Feed(0, 0, -1, t.AddMilliseconds(20 * i));
            Assert.True(d.Feed(3.5, 0, -1, t.AddMilliseconds(20 * i++)));
            Assert.False(d.Feed(-3.5, 0, -1, t.AddMilliseconds(20 * i++)));
            for (int k = 0; k < 600; k++) d.Feed(0, 0, -1, t.AddMilliseconds(20 * i++));
            Assert.True(d.Feed(0, 4, -1, t.AddMilliseconds(20 * i++)));
        }

        [Fact]
        public void PotholeBelowMediumThresholdIgnored()
        {
            var d = new ImpactDetector(ImpactDetector.PresetThreshold("medium"));
            var t = DateTime.Now;
            int i = 0;
            for (; i < 500; i++) d.Feed(0, 0, -1, t.AddMilliseconds(20 * i));
            Assert.False(d.Feed(0, 0, -2.0, t.AddMilliseconds(20 * i++)));
        }

        [Fact]
        public void WarmUpSuppressesMountingBumps()
        {
            var d = new ImpactDetector(1.0);
            var t = DateTime.Now;
            d.Feed(0, 0, -1, t);
            Assert.False(d.Feed(5, 5, 5, t.AddMilliseconds(500)));
        }
    }

    public class OutputFormatTests
    {
        [Fact]
        public void StampAndSrt()
        {
            var now = new DateTime(2026, 11, 22, 14, 3, 21);
            var fix = new GpsFix { Latitude = 50.081234, Longitude = 14.423111, Time = now };
            string s = StampFormatter.Format(now, fix, 87.4, new StampOptions { Label = "1AB 2345\r\nX" });
            Assert.Equal("2026-11-22 14:03:21   87 km/h   N50.08123 E14.42311   1AB 2345X", s);

            var srt = new SrtBuilder(now);
            srt.AddCue(now.AddSeconds(1), now.AddSeconds(2), "line\r\nbreak");
            Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nline  break\r\n\r\n", srt.ToString());
        }

        [Fact]
        public void GpxEscapesName()
        {
            string gpx = GpxBuilder.Build("<x>&", new[] { new GpsFix { Latitude = 1, Longitude = 2, Time = DateTime.UtcNow, SpeedKmh = 36 } });
            Assert.Contains("&lt;x&gt;&amp;", gpx);
            Assert.Contains("<speed>10.00</speed>", gpx);
        }

        [Fact]
        public void GpsSpeedFallback()
        {
            var t = DateTime.Now;
            var a = new GpsFix { Latitude = 50, Longitude = 14, Time = t };
            var b = new GpsFix { Latitude = 50.0001, Longitude = 14, Time = t.AddSeconds(1), Accuracy = 5 };
            Assert.InRange(GeoMath.EffectiveSpeedKmh(a, b).Value, 39, 41);
            b.SpeedKmh = 55;
            Assert.Equal(55, GeoMath.EffectiveSpeedKmh(a, b).Value);
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void DefaultsAreValidAndRoundTrip()
        {
            var s = AppSettings.Defaults();
            Assert.Empty(s.Normalize());
            List<string> notes; string err;
            var back = AppSettings.FromJson(s.ToJson(), out notes, out err);
            Assert.Null(err);
            Assert.Empty(notes);
            Assert.Equal(s.Pages.Count, back.Pages.Count);
            Assert.True(double.IsNaN(back.Pages[0].Widgets[0].WarnAbove));
            Assert.Equal(5500, back.Pages[0].Widgets[1].WarnAbove);
        }

        [Fact]
        public void HostileImportIsSanitised()
        {
            var s = AppSettings.Defaults();
            s.Dashcam.BitrateKbps = int.MaxValue;
            s.Dashcam.SegmentSeconds = -5;
            s.Dashcam.StorageCapGb = double.PositiveInfinity;
            s.Dashcam.EmergencyNumber = "+420900123456"; // premium-rate number must not be accepted
            s.Ui.Accent = "red;background:url(x)";
            s.Pages[0].Rows = 1000;
            s.Pages[0].Widgets[0].ColumnSpan = 99;
            s.Pages[0].Widgets[2].ValueKey = "obd:ZZ";
            s.Obd.CustomValues.Add(new ValueDefinition { Name = "clear", Request = "04", Formula = "A" });
            s.Obd.CustomValues.Add(new ValueDefinition { Name = "inject", Request = "01 0C", Header = "7E0\rATMA", Formula = "A" });
            s.Obd.CustomValues.Add(new ValueDefinition { Name = "ok", Request = "22 11 bd", Header = "7e0", Formula = "(A*256+B)/10" });

            List<string> notes; string err;
            var back = AppSettings.FromJson(s.ToJson(), out notes, out err);
            Assert.Null(err);
            Assert.Equal(60000, back.Dashcam.BitrateKbps);
            Assert.Equal(30, back.Dashcam.SegmentSeconds);
            Assert.Equal(8, back.Dashcam.StorageCapGb);
            Assert.Equal("112", back.Dashcam.EmergencyNumber);
            Assert.Equal("#FF3FA9F5", back.Ui.Accent);
            Assert.Equal(6, back.Pages[0].Rows);
            Assert.True(back.Pages[0].Widgets[0].Column + back.Pages[0].Widgets[0].ColumnSpan <= back.Pages[0].Columns);
            Assert.Equal(PidCatalog.GpsSpeedKey, back.Pages[0].Widgets[2].ValueKey);
            Assert.Single(back.Obd.CustomValues);
            Assert.Equal("22 11 BD", back.Obd.CustomValues[0].Request);
            Assert.Equal("7E0", back.Obd.CustomValues[0].Header);
            Assert.NotEmpty(notes);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"__type\":\"System.IO.FileInfo\"}")]
        public void MalformedJsonIsRejectedWithoutThrowing(string json)
        {
            List<string> notes; string err;
            var s = AppSettings.FromJson(json, out notes, out err);
            if (s != null) Assert.NotNull(s.Dashcam); else Assert.NotNull(err);
        }

        [Fact]
        public void OversizedJsonRejected()
        {
            List<string> notes; string err;
            Assert.Null(AppSettings.FromJson(new string(' ', AppSettings.MaxSettingsBytes + 1), out notes, out err));
            Assert.NotNull(err);
        }
    }
}
