using System;
using System.Collections.Generic;

// Endpoint IDs survive renames and device-list reordering. Name matching is
// retained only for configs written before the device picker was introduced.
sealed class AudioDeviceInfo {
    public string Id, Name, LegacyName;
    public uint WaveId;
    public bool Available = true;
    public override string ToString() { return Available ? Name : "未找到：" + Name; }
}

static class AudioDevices {
    public static AudioDeviceInfo Resolve(IList<AudioDeviceInfo> devices, string name, string id) {
        if (!string.IsNullOrEmpty(id)) {
            foreach (var device in devices)
                if (device.Available && string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)) return device;
            return null; // A missing selected endpoint must not silently select another microphone.
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        AudioDeviceInfo match = null;
        int count = 0;
        foreach (var device in devices) {
            if (device.Available && (string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.LegacyName, name, StringComparison.OrdinalIgnoreCase))) { match = device; count++; }
        }
        if (count != 0) return count == 1 ? match : null;
        foreach (var device in devices) {
            if (device.Available && ((device.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (device.LegacyName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) { match = device; count++; }
        }
        return count == 1 ? match : null;
    }
}
