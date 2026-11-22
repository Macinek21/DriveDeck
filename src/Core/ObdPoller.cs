using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DriveDeck.Core
{
    public enum ObdState { Off, Connecting, Connected, Reconnecting, Failed }

    public sealed class ObdPollerOptions
    {
        public string Protocol = "0";
        public int DtcIntervalSeconds = 30;
        public bool ReadPermanentDtcs = false;
        public string FuelType = "petrol"; // petrol | diesel
        public int MediumIntervalMs = 1000;
        public int SlowIntervalMs = 5000;
        public int MinCycleMs = 50;
    }

    /// <summary>
    /// Background loop: connects, polls only the values currently shown (plus what derived values need),
    /// checks fault codes periodically and right after the check-engine lamp turns on, and reconnects with back-off.
    /// Events fire on a background thread.
    /// </summary>
    public sealed class ObdPoller
    {
        private readonly Func<CancellationToken, Task<IObdTransport>> _connect;
        private readonly ObdPollerOptions _opt;
        private readonly object _lock = new object();
        private List<ValueDefinition> _wanted = new List<ValueDefinition>();
        private CancellationTokenSource _cts;
        private Task _loop;
        private readonly Dictionary<string, double> _last = new Dictionary<string, double>();
        private List<Dtc> _lastDtcs = new List<Dtc>();

        public event Action<string, double> ValueUpdated;
        public event Action<string> ValueUnsupported;
        public event Action<ObdState, string> StateChanged;
        public event Action<IReadOnlyList<Dtc>, IReadOnlyList<Dtc>> DtcsChanged; // (all current, newly appeared)

        public ObdState State { get; private set; }
        public ObdClient Client { get; private set; }

        public ObdPoller(Func<CancellationToken, Task<IObdTransport>> connect, ObdPollerOptions options)
        {
            _connect = connect;
            _opt = options ?? new ObdPollerOptions();
        }

        /// <summary>Sets which values to poll. Derived values pull in their sources automatically.</summary>
        public void SetWanted(IEnumerable<ValueDefinition> defs)
        {
            var list = new List<ValueDefinition>();
            foreach (var d in defs.Where(d => d != null))
            {
                if (d.Key == PidCatalog.BoostKey) { AddStd(list, "0B"); AddStd(list, "33"); }
                else if (d.Key == PidCatalog.ConsumptionKey) { AddStd(list, "0D"); AddStd(list, "5E"); AddStd(list, "10"); }
                else if (!string.IsNullOrEmpty(d.Request) || d.Key == PidCatalog.AdapterVoltageKey) list.Add(d);
            }
            lock (_lock) _wanted = list.GroupBy(x => x.Key).Select(g => g.First()).ToList();
        }

        private static void AddStd(List<ValueDefinition> list, string pid)
        {
            var d = PidCatalog.Standard.FirstOrDefault(x => x.Key == "obd:" + pid);
            if (d != null) list.Add(d);
        }

        public void Start()
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }

        public async Task StopAsync()
        {
            if (_cts == null) return;
            _cts.Cancel();
            try { if (_loop != null) await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
            SetState(ObdState.Off, "Stopped");
        }

        private void SetState(ObdState s, string msg)
        {
            State = s;
            var h = StateChanged; if (h != null) h(s, msg);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                IObdTransport transport = null;
                try
                {
                    SetState(attempt == 0 ? ObdState.Connecting : ObdState.Reconnecting, "Connecting to adapter…");
                    transport = await _connect(ct).ConfigureAwait(false);
                    var client = new ObdClient(transport);
                    await client.InitializeAsync(_opt.Protocol, ct).ConfigureAwait(false);
                    Client = client;
                    attempt = 0;
                    SetState(ObdState.Connected, (client.AdapterVersion ?? "ELM327") + ", protocol " + client.ProtocolNumber);
                    await PollAsync(client, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Client = null;
                    attempt++;
                    string msg = ex is ObdException ? ex.Message
                               : ex is TimeoutException ? "Adapter stopped answering"
                               : ex is IOException ? "Bluetooth connection lost"
                               : "Connection error: " + ex.Message;
                    SetState(attempt >= 3 ? ObdState.Failed : ObdState.Reconnecting, msg);
                }
                finally
                {
                    if (transport != null) { try { transport.Dispose(); } catch { } }
                }
                int delay = Math.Min(30, 2 * attempt + 1);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollAsync(ObdClient client, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            long lastMedium = -100000, lastSlow = -100000, lastDtc = -100000;
            var unsupported = new HashSet<string>();
            int consecutiveErrors = 0;
            bool? lastMil = null;

            while (!ct.IsCancellationRequested)
            {
                long cycleStart = sw.ElapsedMilliseconds;
                bool doMedium = cycleStart - lastMedium >= _opt.MediumIntervalMs;
                bool doSlow = cycleStart - lastSlow >= _opt.SlowIntervalMs;
                if (doMedium) lastMedium = cycleStart;
                if (doSlow) lastSlow = cycleStart;

                List<ValueDefinition> wanted;
                lock (_lock) wanted = _wanted.ToList();

                foreach (var def in wanted)
                {
                    if (unsupported.Contains(def.Key)) continue;
                    if (def.Priority == 1 && !doMedium) continue;
                    if (def.Priority >= 2 && !doSlow) continue;

                    ObdReading r;
                    if (def.Key == PidCatalog.AdapterVoltageKey) r = await client.ReadAdapterVoltageAsync(ct).ConfigureAwait(false);
                    else if (!client.IsStandardPidSupported(def)) r = ObdReading.Fail("Not supported by car", true);
                    else r = await client.ReadAsync(def, ct).ConfigureAwait(false);

                    if (r.Ok) { consecutiveErrors = 0; Publish(def.Key, r.Value); }
                    else if (r.Unsupported) { unsupported.Add(def.Key); var h = ValueUnsupported; if (h != null) h(def.Key); }
                    else if (++consecutiveErrors > 12) throw new ObdException("Too many errors from adapter");
                }
                PublishDerived();

                // Fault codes: on schedule, and immediately when the check-engine lamp switches on.
                if (doSlow)
                {
                    bool? mil = null;
                    try { mil = await client.ReadMilAsync(ct).ConfigureAwait(false); } catch (TimeoutException) { }
                    bool milTurnedOn = mil == true && lastMil != true;
                    lastMil = mil;
                    if (milTurnedOn || cycleStart - lastDtc >= _opt.DtcIntervalSeconds * 1000L)
                    {
                        lastDtc = cycleStart;
                        var dtcs = await client.ReadDtcsAsync(_opt.ReadPermanentDtcs, ct).ConfigureAwait(false);
                        var fresh = dtcs.Where(d => !_lastDtcs.Contains(d)).ToList();
                        bool changed = fresh.Count > 0 || dtcs.Count != _lastDtcs.Count;
                        _lastDtcs = dtcs;
                        if (changed) { var h = DtcsChanged; if (h != null) h(dtcs, fresh); }
                    }
                }

                long spent = sw.ElapsedMilliseconds - cycleStart;
                if (spent < _opt.MinCycleMs) await Task.Delay((int)(_opt.MinCycleMs - spent), ct).ConfigureAwait(false);
            }
        }

        private void Publish(string key, double value)
        {
            lock (_last) _last[key] = value;
            var h = ValueUpdated; if (h != null) h(key, value);
        }

        private double Last(string key)
        {
            double v;
            lock (_last) return _last.TryGetValue(key, out v) ? v : double.NaN;
        }

        private void PublishDerived()
        {
            double map = Last("obd:0B"), baro = Last("obd:33");
            if (!double.IsNaN(map)) Publish(PidCatalog.BoostKey, DerivedValues.BoostBar(map, double.IsNaN(baro) ? 101.3 : baro));
            double c = DerivedValues.ConsumptionPer100Km(Last("obd:0D"), Last("obd:5E"), Last("obd:10"), _opt.FuelType);
            if (!double.IsNaN(c)) Publish(PidCatalog.ConsumptionKey, c);
        }
    }

    public static class DerivedValues
    {
        public static double BoostBar(double mapKpa, double baroKpa) { return (mapKpa - baroKpa) / 100.0; }

        /// <summary>L/100 km from fuel rate (preferred) or, for petrol only, from MAF at stoichiometric AFR. NaN when not computable or nearly stopped.</summary>
        public static double ConsumptionPer100Km(double speedKmh, double fuelRateLh, double mafGs, string fuelType)
        {
            if (double.IsNaN(speedKmh) || speedKmh < 5) return double.NaN;
            double lh = fuelRateLh;
            if (double.IsNaN(lh))
            {
                if (fuelType != "petrol" || double.IsNaN(mafGs)) return double.NaN;
                lh = mafGs * 3600.0 / (14.7 * 745.0); // g/s air -> L/h petrol (density 745 g/L)
            }
            double v = lh / speedKmh * 100.0;
            return Math.Min(v, 99.9);
        }
    }
}
