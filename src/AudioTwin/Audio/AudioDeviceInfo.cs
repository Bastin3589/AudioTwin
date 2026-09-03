namespace AudioTwin.Audio;

/// <summary>
/// Lightweight, detached description of a Windows playback endpoint.
/// We never hold live MMDevice objects in the UI: they go stale when a device
/// disappears. Only the endpoint ID is kept, and a fresh MMDevice is resolved
/// from the enumerator at the moment it is actually needed.
/// </summary>
public sealed class AudioDeviceInfo : IEquatable<AudioDeviceInfo>
{
    public AudioDeviceInfo(string id, string name, bool isDefault)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? "(unnamed device)" : name;
        IsDefault = isDefault;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>True when this endpoint is the current Windows default output.</summary>
    public bool IsDefault { get; }

    public override string ToString() => IsDefault ? Name + "   (Windows default)" : Name;

    public bool Equals(AudioDeviceInfo? other) =>
        other is not null && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as AudioDeviceInfo);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
}
