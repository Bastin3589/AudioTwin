using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioTwin.Audio;

/// <summary>
/// Enumerates Windows playback endpoints and raises events when the set of
/// devices changes (USB / Bluetooth plugged in, unplugged, disabled, default
/// device switched). Bluetooth needs no special handling: Windows exposes a
/// connected Bluetooth headset as an ordinary render endpoint.
/// </summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;
    private bool _disposed;

    public DeviceWatcher()
    {
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch
        {
            // Notifications are a convenience; the app still works with the
            // manual "Refresh devices" button if registration fails.
            _registered = false;
        }
    }

    /// <summary>A device was added, removed, enabled or disabled.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>The Windows default playback device changed.</summary>
    public event EventHandler? DefaultRenderDeviceChanged;

    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
    {
        var result = new List<AudioDeviceInfo>();
        string? defaultId = TryGetDefaultRenderDeviceId();

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // A device can vanish between enumeration and property read.
                }
            }
        }
        catch
        {
            // Return whatever we managed to collect.
        }

        result.Sort((a, b) =>
        {
            if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return result;
    }

    public string? TryGetDefaultRenderDeviceId()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                return null;
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    public string? TryGetDefaultRenderDeviceName()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                return null;
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    // ---- IMMNotificationClient (called on arbitrary COM threads) ----

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise(DevicesChanged);

    public void OnDeviceAdded(string pwstrDeviceId) => Raise(DevicesChanged);

    public void OnDeviceRemoved(string deviceId) => Raise(DevicesChanged);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render) return;
        Raise(DevicesChanged);
        Raise(DefaultRenderDeviceChanged);
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Fires very often (volume, format, ...). Ignored on purpose.
    }

    private void Raise(EventHandler? handler)
    {
        if (_disposed) return;
        try { handler?.Invoke(this, EventArgs.Empty); } catch { /* never throw into COM */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_registered) _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
