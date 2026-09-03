using NAudio.Wave;

namespace AudioTwin.Audio;

/// <summary>
/// A fixed-capacity circular delay line, used to line up a wired output with a
/// higher-latency Bluetooth output. The delay can be changed while audio is
/// playing (a change may produce one small click, which is acceptable for a
/// control the user drags once and forgets).
/// </summary>
public sealed class DelaySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _maxDelayFrames;
    private readonly int _maxDelaySamples;
    private readonly float[] _ring;
    private readonly int _maxChunk;

    private float[] _scratch = Array.Empty<float>();
    private int _writePosition;
    private int _currentDelaySamples;
    private volatile int _requestedDelayMs;

    public DelaySampleProvider(ISampleProvider source, int maxDelayMilliseconds)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        MaxDelayMilliseconds = Math.Max(0, maxDelayMilliseconds);

        _maxDelayFrames = (int)((long)MaxDelayMilliseconds * _sampleRate / 1000);
        _maxDelaySamples = _maxDelayFrames * _channels;

        // One extra second of head room so a single Read() block always fits.
        _ring = new float[_maxDelaySamples + (_sampleRate * _channels) + _channels];
        _maxChunk = _ring.Length - _maxDelaySamples - _channels;
    }

    public int MaxDelayMilliseconds { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Delay in milliseconds. Safe to set from the UI thread at any time.</summary>
    public int DelayMilliseconds
    {
        get => _requestedDelayMs;
        set => _requestedDelayMs = Math.Clamp(value, 0, MaxDelayMilliseconds);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Snap the requested delay to a whole frame so channels stay aligned.
        int desiredFrames = Math.Clamp((int)((long)_requestedDelayMs * _sampleRate / 1000), 0, _maxDelayFrames);
        _currentDelaySamples = desiredFrames * _channels;

        int total = 0;
        while (total < count)
        {
            int chunk = Math.Min(count - total, _maxChunk);
            if (_scratch.Length < chunk) _scratch = new float[chunk];

            int read = _source.Read(_scratch, 0, chunk);
            if (read <= 0) break;

            // Write the new samples into the ring.
            int write = _writePosition;
            for (int i = 0; i < read; i++)
            {
                _ring[write] = _scratch[i];
                if (++write == _ring.Length) write = 0;
            }
            _writePosition = write;

            // Read back the samples that entered the line _currentDelaySamples ago.
            int readPosition = write - read - _currentDelaySamples;
            while (readPosition < 0) readPosition += _ring.Length;

            for (int i = 0; i < read; i++)
            {
                buffer[offset + total + i] = _ring[readPosition];
                if (++readPosition == _ring.Length) readPosition = 0;
            }

            total += read;
        }

        return total;
    }
}
