using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioTwin.Audio;

/// <summary>
/// The whole engine: one WASAPI loopback capture on the Windows default
/// playback device, fanned out to two independent output branches.
///
///   Windows default endpoint
///            |
///     WasapiLoopbackCapture      (32-bit float PCM, device mix rate)
///            |
///     +------+------+
///     |             |
///  Branch 1      Branch 2        (jitter buffer -> delay -> volume ->
///     |             |             resample -> channel map -> WasapiOut)
///  Device A      Device B
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _sync = new();

    private WasapiLoopbackCapture? _capture;
    private bool _stoppingCapture;
    private bool _disposed;

    /// <summary>Non-fatal information (one output failed, an output recovered, ...).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Routing stopped by itself; the argument explains why.</summary>
    public event EventHandler<string>? StoppedUnexpectedly;

    /// <summary>Something changed that the UI should redraw.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning { get; private set; }

    public OutputBranch? Branch1 { get; private set; }

    public OutputBranch? Branch2 { get; private set; }

    public string? SourceDeviceId { get; private set; }

    public string SourceDeviceName { get; private set; } = "Windows default";

    public WaveFormat? CaptureFormat { get; private set; }

    public void Start(
        string deviceId1, string deviceName1,
        string deviceId2, string deviceName2,
        float volume1, float volume2,
        int delayMs1, int delayMs2)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioRouter));

        lock (_sync)
        {
            StopInternal();

            if (string.Equals(deviceId1, deviceId2, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output 1 and Output 2 must be different devices.");

            MMDevice defaultDevice;
            try
            {
                defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows has no default playback device, so there is nothing to capture.", ex);
            }

            SourceDeviceId = defaultDevice.ID;
            try { SourceDeviceName = defaultDevice.FriendlyName; } catch { SourceDeviceName = "Windows default"; }

            bool passThrough1 = string.Equals(deviceId1, SourceDeviceId, StringComparison.OrdinalIgnoreCase);
            bool passThrough2 = string.Equals(deviceId2, SourceDeviceId, StringComparison.OrdinalIgnoreCase);

            WasapiLoopbackCapture capture;
            try
            {
                capture = new WasapiLoopbackCapture(defaultDevice);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not capture from \"{SourceDeviceName}\": {ex.Message}", ex);
            }

            var format = NormalizeFormat(capture.WaveFormat);
            CaptureFormat = format;

            var branch1 = new OutputBranch(deviceId1, deviceName1, format, passThrough1)
            {
                Volume = volume1,
                DelayMilliseconds = delayMs1
            };
            var branch2 = new OutputBranch(deviceId2, deviceName2, format, passThrough2)
            {
                Volume = volume2,
                DelayMilliseconds = delayMs2
            };
            branch1.Failed += OnBranchFailed;
            branch2.Failed += OnBranchFailed;

            var problems = new List<string>();
            TryStartBranch(branch1, "Output 1", problems);
            TryStartBranch(branch2, "Output 2", problems);

            if (!branch1.IsActive && !branch2.IsActive)
            {
                branch1.Dispose();
                branch2.Dispose();
                try { capture.Dispose(); } catch { }
                CaptureFormat = null;
                throw new InvalidOperationException(
                    problems.Count > 0
                        ? string.Join("  ", problems)
                        : "Neither output device could be opened.");
            }

            _capture = capture;
            Branch1 = branch1;
            Branch2 = branch2;
            _stoppingCapture = false;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                StopInternal();
                throw new InvalidOperationException($"Could not start capture: {ex.Message}", ex);
            }

            IsRunning = true;

            if (problems.Count > 0)
                Notice?.Invoke(this, string.Join("  ", problems));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternal();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-opens any branch that dropped out (typical after a Bluetooth headset
    /// reconnects). Called by the UI when the device list changes.
    /// </summary>
    public void RetryFailedOutputs()
    {
        List<string> recovered = new();

        lock (_sync)
        {
            if (!IsRunning) return;

            foreach (var branch in new[] { Branch1, Branch2 })
            {
                if (branch == null || branch.IsActive || branch.IsPassThrough) continue;
                try
                {
                    branch.Start(_enumerator);
                    recovered.Add($"\"{branch.DeviceName}\" reconnected.");
                }
                catch
                {
                    // Still gone. Try again on the next device change.
                }
            }
        }

        if (recovered.Count > 0)
        {
            Notice?.Invoke(this, string.Join("  ", recovered));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when Windows switched to a different default output since we started.</summary>
    public bool DefaultDeviceChanged()
    {
        if (!IsRunning || SourceDeviceId == null) return false;
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return true;
            var id = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            return !string.Equals(id, SourceDeviceId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void SetVolume(int outputIndex, float volume)
    {
        var branch = outputIndex == 1 ? Branch1 : Branch2;
        if (branch != null) branch.Volume = volume;
    }

    public void SetDelay(int outputIndex, int milliseconds)
    {
        var branch = outputIndex == 1 ? Branch1 : Branch2;
        if (branch != null) branch.DelayMilliseconds = milliseconds;
    }

    private void TryStartBranch(OutputBranch branch, string label, List<string> problems)
    {
        try
        {
            branch.Start(_enumerator);
        }
        catch (Exception ex)
        {
            problems.Add($"{label}: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        Branch1?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        Branch2?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stoppingCapture) return;

        string reason = e.Exception != null
            ? $"Capture from \"{SourceDeviceName}\" stopped: {e.Exception.Message}"
            : $"Capture from \"{SourceDeviceName}\" stopped unexpectedly.";

        try
        {
            lock (_sync) { StopInternal(); }
        }
        catch { }

        StoppedUnexpectedly?.Invoke(this, reason);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBranchFailed(object? sender, string message)
    {
        Notice?.Invoke(this, message + " The other output keeps playing; pick another device or reconnect it.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopInternal()
    {
        _stoppingCapture = true;

        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }

        var b1 = Branch1;
        var b2 = Branch2;
        Branch1 = null;
        Branch2 = null;

        if (b1 != null) { b1.Failed -= OnBranchFailed; try { b1.Dispose(); } catch { } }
        if (b2 != null) { b2.Failed -= OnBranchFailed; try { b2.Dispose(); } catch { } }

        CaptureFormat = null;
        IsRunning = false;
    }

    /// <summary>
    /// WASAPI hands us a WAVEFORMATEXTENSIBLE. Convert it to the plain
    /// equivalent so NAudio's sample-provider converters accept it.
    /// </summary>
    private static WaveFormat NormalizeFormat(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            try { format = extensible.ToStandardWaveFormat(); }
            catch { /* leave as-is */ }
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        return format;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { lock (_sync) { StopInternal(); } } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
