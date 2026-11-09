using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.Core.Networking;

public sealed class UdpTelemetryListener : IDisposable
{
    public const int DefaultPort = 5300;
    private const int ErrorRetryDelayMs = 100;

    private readonly int _port;
    private readonly IPAddress _bindAddress;
    private readonly Channel<TelemetryFrame> _channel;

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private long _packetsReceived;
    private long _packetsConsumed;
    private bool _started;
    private bool _disposed;

    public UdpTelemetryListener(int port = DefaultPort, IPAddress? bindAddress = null)
    {
        _port = port;
        _bindAddress = bindAddress ?? IPAddress.Loopback;
        _channel = Channel.CreateBounded<TelemetryFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    public int Port => _port;
    public IPAddress BindAddress => _bindAddress;
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
    public long PacketsConsumed => Interlocked.Read(ref _packetsConsumed);
    public long PacketsDropped => Math.Max(0L, PacketsReceived - PacketsConsumed);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Listener has already been started.");
        }
        _started = true;

        _client = new UdpClient();
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(_bindAddress, _port));

        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_client, _cts.Token));
    }

    public async IAsyncEnumerable<TelemetryFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (TelemetryFrame frame in _channel.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            Interlocked.Increment(ref _packetsConsumed);
            yield return frame;
        }
    }

    public bool TryReadLatest(out TelemetryFrame frame)
    {
        if (_channel.Reader.TryRead(out frame))
        {
            Interlocked.Increment(ref _packetsConsumed);
            return true;
        }

        return false;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _client?.Close();

        if (_receiveLoop is not null)
        {
            await _receiveLoop.ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (TelemetryPacketParser.TryParse(result.Buffer, Environment.TickCount64, out TelemetryFrame frame))
                {
                    Interlocked.Increment(ref _packetsReceived);
                    ForzaOverlay.Core.Logging.PerfRecorder.OnPacket(frame.ReceivedTickMs);
                    _channel.Writer.TryWrite(frame);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(ErrorRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts?.Cancel();
        _client?.Close();
        _cts?.Dispose();
    }
}
