using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace DriveDeck.App.Services
{
    /// <summary>Classic Bluetooth (SPP/RFCOMM) link to an ELM327 adapter paired in the phone's Bluetooth settings.</summary>
    public sealed class BluetoothElmTransport : IObdTransport
    {
        private const int MaxReplyChars = ElmResponseParser.MaxResponseChars;
        private StreamSocket _socket;
        private DataWriter _writer;
        private DataReader _reader;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        public string Name { get; private set; }

        public static async Task<IReadOnlyList<DeviceInformation>> ListPairedAdaptersAsync()
        {
            return await DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort));
        }

        /// <param name="service">Resolved on the UI thread (the first access may need user consent).</param>
        public static async Task<BluetoothElmTransport> ConnectAsync(RfcommDeviceService service, CancellationToken ct)
        {
            if (service == null) throw new ObdException("Adapter not found. Pair it in Settings > Devices > Bluetooth (PIN usually 1234).");
            var t = new BluetoothElmTransport { Name = service.ConnectionHostName.DisplayName };
            t._socket = new StreamSocket();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await t._socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                        SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(timeout.Token);
                }
            }
            catch (TaskCanceledException)
            {
                t.Dispose();
                if (ct.IsCancellationRequested) throw;
                throw new ObdException("Adapter did not accept the connection. Is it plugged in and is the ignition on?");
            }
            catch (Exception)
            {
                t.Dispose();
                throw new ObdException("Could not connect to the adapter. It may be connected to another phone.");
            }
            t._writer = new DataWriter(t._socket.OutputStream);
            t._reader = new DataReader(t._socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            return t;
        }

        public async Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            if (command == null || command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
                throw new ArgumentException("Invalid command");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_writer == null) throw new IOException("Not connected");
                _writer.WriteString(command + "\r");
                await _writer.StoreAsync().AsTask(ct).ConfigureAwait(false);

                var sb = new StringBuilder();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeout);
                    while (true)
                    {
                        uint n;
                        try { n = await _reader.LoadAsync(256).AsTask(cts.Token).ConfigureAwait(false); }
                        catch (TaskCanceledException)
                        {
                            if (ct.IsCancellationRequested) throw;
                            throw new TimeoutException("No reply to " + command);
                        }
                        if (n == 0) throw new IOException("Adapter closed the connection");
                        var buf = new byte[n];
                        _reader.ReadBytes(buf);
                        for (int i = 0; i < buf.Length; i++)
                        {
                            byte b = buf[i];
                            if (b == (byte)'>') return sb.ToString();
                            if (b == 0) continue;
                            sb.Append(b < 0x80 ? (char)b : '?'); // ELM327 is ASCII; anything else is noise
                        }
                        if (sb.Length > MaxReplyChars) throw new IOException("Adapter sent too much data");
                    }
                }
            }
            catch (Exception ex) when (!(ex is TimeoutException) && !(ex is OperationCanceledException) && !(ex is IOException) && !(ex is ArgumentException))
            {
                throw new IOException("Bluetooth error", ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            try { if (_writer != null) _writer.DetachStream(); } catch { }
            try { if (_reader != null) _reader.DetachStream(); } catch { }
            try { if (_socket != null) _socket.Dispose(); } catch { }
            _writer = null; _reader = null; _socket = null;
        }
    }
}
