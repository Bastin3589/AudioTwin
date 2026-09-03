using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioTwin.Audio;

/// <summary>
/// One leg of the duplicator: a jitter buffer fed by the capture thread, then
/// delay -> volume -> (resample) -> (channel map) -> WASAPI shared-mode render.
///
/// A branch can also be a "pass-through" branch. That happens when the chosen
/// output device IS the Windows default device we are capturing from: rendering
/// to it would feed our own output straight back into the loopback capture and
/// build an echo loop. In that case the device already plays the audio natively
/// and this branch does nothing at all.
/// </summary>
public sealed class OutputBranch : IDisposable
{
    public const int MaxDelayMilliseconds = 500;

    private const int OutputLatencyMilliseconds = 100;   // safe for Bluetooth
    private const int JitterBufferMilliseconds = 2000;   // capacity, not fill level
    private const int MaxQueuedMilliseconds = 250;       // drift guard

    private readonly object _sync = new();
    private readonly WaveFormat _captureFormat;

    private BufferedWaveProvider? _queue;
    private DelaySampleProvider? _delay;
    private VolumeSampleProvider? _volume;
    private WasapiOut? _output;
    private MMDevice? _device;
    private byte[] _trimScratch = Array.Empty<byte>();

    private float _volumeValue = 1f;
    private int _delayValue;
    private bool _stopping;
    private bool _disposed;

    public OutputBranch(string deviceId, string deviceName, WaveFormat captureFormat, bool passThrough)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        IsPassThrough = passThrough;
        _captureFormat = captureFormat;
    }

    /// <summary>Raised (from an audio thread) when the device drops out unexpectedly.</summary>
    public event EventHandler<string>? Failed;

    public string DeviceId { get; }

    public string DeviceName { get; private set; }

    public bool IsPassThrough { get; }

    /// <summary>True when audio is flowing (or, for a pass-through branch, when Windows is playing it).</summary>
    public bool IsActive { get; private set; }

    public string? LastError { get; private set; }

    public float Volume
    {
        get => _volumeValue;
        set
        {
            float v = Math.Clamp(value, 0f, 1f);
            _volumeValue = v;
            var provider = _volume;
            if (provider != null) provider.Volume = v;
        }
    }

    public int DelayMilliseconds
    {
        get => _delayValue;
        set
        {
            int ms = Math.Clamp(value, 0, MaxDelayMilliseconds);
            _delayValue = ms;
            var provider = _delay;
            if (provider != null) provider.DelayMilliseconds = ms;
        }
    }

    /// <summary>Opens the device and starts rendering. Throws on failure.</summary>
    public void Start(MMDeviceEnumerator enumerator)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OutputBranch));

        if (IsPassThrough)
        {
            IsActive = true;
            LastError = null;
            return;
        }

        lock (_sync)
        {
            StopInternal();
            _stopping = false;
            LastError = null;

            _device = ResolveDevice(enumerator);

            int rate = _captureFormat.SampleRate;
            int channels = _captureFormat.Channels;
            try
            {
                var mix = _device.AudioClient.MixFormat;
                rate = mix.SampleRate;
                channels = mix.Channels;
            }
            catch
            {
                // Fall back to the capture format; NAudio will convert if needed.
            }

            try
            {
                BuildPipeline(rate, channels);
            }
            catch (Exception first)
            {
                // The audio client is spent after a failed attempt - get a fresh
                // device object and try once more with the capture format.
                DisposePipeline();
                try
                {
                    _device = ResolveDevice(enumerator);
                    BuildPipeline(_captureFormat.SampleRate, _captureFormat.Channels);
                }
                catch
                {
                    DisposePipeline();
                    LastError = Describe(first);
                    throw new InvalidOperationException($"Could not open \"{DeviceName}\": {LastError}", first);
                }
            }

            IsActive = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stopping = true;
            StopInternal();
            IsActive = false;
        }
    }

    /// <summary>Called from the capture thread for every captured block.</summary>
    public void AddSamples(byte[] data, int offset, int count)
    {
        if (IsPassThrough || count <= 0) return;

        var queue = _queue;
        if (queue == null || !IsActive) return;

        try
        {
            queue.AddSamples(data, offset, count);

            // Drift guard: the capture clock and the render clock are not the
            // same clock. If the queue creeps up we drop the oldest audio so
            // latency cannot grow without bound.
            int maxBytes = (int)(_captureFormat.AverageBytesPerSecond * (MaxQueuedMilliseconds / 1000.0));
            int excess = queue.BufferedBytes - maxBytes;
            if (excess > 0)
            {
                excess = Math.Min(excess, _captureFormat.AverageBytesPerSecond);
                excess -= excess % _captureFormat.BlockAlign;
                if (excess > 0)
                {
                    if (_trimScratch.Length < excess) _trimScratch = new byte[excess];
                    queue.Read(_trimScratch, 0, excess);
                }
            }
        }
        catch
        {
            // Never let a buffer hiccup kill the capture thread.
        }
    }

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator)
    {
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(DeviceId);
        }
        catch (Exception ex)
        {
            LastError = "the device is no longer present";
            throw new InvalidOperationException($"\"{DeviceName}\" is no longer available.", ex);
        }

        try
        {
            if (device.State != DeviceState.Active)
                throw new InvalidOperationException($"\"{DeviceName}\" is not active (unplugged or disabled).");
            DeviceName = device.FriendlyName;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Keep the previously known name.
        }

        return device;
    }

    private void BuildPipeline(int targetSampleRate, int targetChannels)
    {
        _queue = new BufferedWaveProvider(_captureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(JitterBufferMilliseconds),
            DiscardOnBufferOverflow = true,
            ReadFully = true   // play silence rather than stall if we run dry
        };

        ISampleProvider chain = _queue.ToSampleProvider();

        _delay = new DelaySampleProvider(chain, MaxDelayMilliseconds) { DelayMilliseconds = _delayValue };
        _volume = new VolumeSampleProvider(_delay) { Volume = _volumeValue };

        ISampleProvider tail = _volume;

        if (tail.WaveFormat.SampleRate != targetSampleRate)
        {
            // WDL resampler: fully managed, high quality, no MediaFoundation dependency.
            tail = new WdlResamplingSampleProvider(tail, targetSampleRate);
        }

        if (tail.WaveFormat.Channels != targetChannels)
        {
            tail = new ChannelMapSampleProvider(tail, targetChannels);
        }

        var output = new WasapiOut(_device!, AudioClientShareMode.Shared, true, OutputLatencyMilliseconds);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(tail);
        output.Play();
        _output = output;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;

        IsActive = false;
        LastError = e.Exception != null
            ? Describe(e.Exception)
            : "playback stopped unexpectedly";

        try { Failed?.Invoke(this, $"\"{DeviceName}\" stopped: {LastError}"); } catch { }
    }

    private void StopInternal()
    {
        DisposePipeline();
    }

    private void DisposePipeline()
    {
        var output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }

        _volume = null;
        _delay = null;
        _queue = null;
        _device = null; // a fresh MMDevice is resolved on the next Start()
    }

    private static string Describe(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message.Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
