using NAudio.Wave;

namespace AudioTwin.Audio;

/// <summary>
/// Maps the captured channel layout onto whatever channel count the output
/// device expects (mono headset, stereo, 5.1 receiver, ...). Deliberately
/// simple and lossless where it can be: no panning laws, no matrixing.
/// </summary>
public sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private float[] _scratch = Array.Empty<float>();

    public ChannelMapSampleProvider(ISampleProvider source, int outputChannels)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (outputChannels < 1) throw new ArgumentOutOfRangeException(nameof(outputChannels));

        _inChannels = source.WaveFormat.Channels;
        _outChannels = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outChannels;
        if (frames == 0) return 0;

        int needed = frames * _inChannels;
        if (_scratch.Length < needed) _scratch = new float[needed];

        int read = _source.Read(_scratch, 0, needed);
        int readFrames = read / _inChannels;

        for (int f = 0; f < readFrames; f++)
        {
            int src = f * _inChannels;
            int dst = offset + (f * _outChannels);

            if (_inChannels == _outChannels)
            {
                for (int c = 0; c < _outChannels; c++)
                    buffer[dst + c] = _scratch[src + c];
            }
            else if (_outChannels == 1)
            {
                // Fold the front pair down to mono.
                int n = Math.Min(_inChannels, 2);
                float sum = 0f;
                for (int c = 0; c < n; c++) sum += _scratch[src + c];
                buffer[dst] = sum / n;
            }
            else if (_inChannels == 1)
            {
                buffer[dst] = _scratch[src];
                buffer[dst + 1] = _scratch[src];
                for (int c = 2; c < _outChannels; c++) buffer[dst + c] = 0f;
            }
            else
            {
                int copy = Math.Min(_inChannels, _outChannels);
                for (int c = 0; c < copy; c++) buffer[dst + c] = _scratch[src + c];
                for (int c = copy; c < _outChannels; c++) buffer[dst + c] = 0f;
            }
        }

        return readFrames * _outChannels;
    }
}
