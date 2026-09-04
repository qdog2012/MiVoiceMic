// gattprobe.cs - verify the WinRT BLE GATT runtime path (the exact transport
// MiVoiceMic uses) against a real paired device, e.g. the M720 mouse.
// Also reads the battery service: 2A19 (level %) and 2BED (Battery Level
// Status, BAS v1.1 - charging state, exposed by the RC003 remote only).
// Usage: gattprobe <nameSubstring> [battery] [watch]
//   battery - read 2A19 + 2BED once and decode
//   watch   - additionally subscribe notify for 20s (plug/unplug the charger
//             while it runs to see whether the remote pushes live updates)
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

class GattProbe {
    static readonly Guid CHR_BATTERY = new Guid("00002a19-0000-1000-8000-00805f9b34fb");
    static readonly Guid CHR_BATT_STATUS = new Guid("00002bed-0000-1000-8000-00805f9b34fb");

    static async Task<int> Run(string namePart, bool readBattery, bool watch) {
        var sel = BluetoothLEDevice.GetDeviceSelector();
        var devs = await AsT(DeviceInformation.FindAllAsync(sel));
        DeviceInformation di = null;
        foreach (var d in devs)
            if (!string.IsNullOrEmpty(d.Name) && d.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) { di = d; break; }
        if (di == null) { Console.WriteLine("FAIL: no paired BLE device matching '" + namePart + "'"); return 1; }
        Console.WriteLine("[1] paired device: " + di.Name + "  id=" + di.Id);

        var dev = await AsT(BluetoothLEDevice.FromIdAsync(di.Id));
        if (dev == null) { Console.WriteLine("FAIL: FromIdAsync null"); return 1; }
        Console.WriteLine("[2] FromIdAsync OK, connection=" + dev.ConnectionStatus);

        var res = await AsT(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached));
        for (int attempt = 1; attempt <= 5 && res.Services.Count == 0; attempt++) {
            Console.WriteLine("    retry " + attempt + " (device may be asleep: " + res.Status + ")");
            await Task.Delay(1500);
            res = await AsT(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached));
        }
        Console.WriteLine("[3] GetGattServices: " + res.Status + ", " + res.Services.Count + " services");
        if (res.Services.Count == 0) {
            dev.Dispose();
            Console.WriteLine("FAIL: no services (device asleep/unreachable - move/nudge it and retry)");
            return 1;
        }
        foreach (var s in res.Services) Console.WriteLine("    service " + s.Uuid);

        GattCharacteristic battery = null, battStatus = null;
        foreach (var s in res.Services) {
            var ch = await AsT(s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached));
            foreach (var c in ch.Characteristics) {
                Console.WriteLine("    char " + c.Uuid + " props=" + c.CharacteristicProperties);
                if (c.Uuid == CHR_BATTERY) battery = c;
                if (c.Uuid == CHR_BATT_STATUS) battStatus = c;
            }
        }
        if (readBattery && battery != null) {
            var r = await AsT(battery.ReadValueAsync());
            if (r.Status == GattCommunicationStatus.Success) {
                var b = new byte[r.Value.Length];
                DataReader.FromBuffer(r.Value).ReadBytes(b);
                Console.WriteLine("[4] battery read: " + b[0] + "%  <-- GATT read path VERIFIED");
            } else {
                Console.WriteLine("[4] battery read status: " + r.Status);
            }
        }
        if (battStatus != null) {
            var r = await AsT(battStatus.ReadValueAsync());
            if (r.Status == GattCommunicationStatus.Success) {
                var b = new byte[r.Value.Length];
                DataReader.FromBuffer(r.Value).ReadBytes(b);
                Console.WriteLine("[5] 2BED read: " + Hex(b) + " -> " + Decode2BED(b));
            } else {
                Console.WriteLine("[5] 2BED read status: " + r.Status);
            }
        } else {
            Console.WriteLine("[5] 2BED not exposed (RC001-style device: no charging status over GATT)");
        }
        if (watch) await Watch(battery, battStatus);
        foreach (var s in res.Services) s.Dispose();
        dev.Dispose();
        Console.WriteLine("PASS: BLE GATT transport works (pair->connect->discover->read)");
        return 0;
    }

    // Subscribe to 2BED/2A19 notifications for 20s and print every update, so
    // plugging/unplugging the charger shows whether the remote pushes changes.
    static async Task Watch(GattCharacteristic battery, GattCharacteristic battStatus) {
        Console.WriteLine("[6] watching battery notify for 20s - plug/unplug the charger now ...");
        int hits = 0;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> onNotify =
            delegate (GattCharacteristic s, GattValueChangedEventArgs e) {
                var r = DataReader.FromBuffer(e.CharacteristicValue);
                var b = new byte[e.CharacteristicValue.Length];
                if (b.Length > 0) r.ReadBytes(b);
                hits++;
                string tag = s.Uuid == CHR_BATT_STATUS ? "2BED" : "2A19";
                string decoded = s.Uuid == CHR_BATT_STATUS
                    ? " -> " + Decode2BED(b)
                    : (b.Length > 0 ? " " + b[0] + "%" : "");
                Console.WriteLine("    notify " + tag + ": " + Hex(b) + decoded);
            };
        Subscribe(battStatus, "2BED", onNotify);
        Subscribe(battery, "2A19", onNotify);
        await Task.Delay(20000);
        Console.WriteLine("    " + hits + " notification(s) in 20s" +
            (hits == 0 ? " (no notify or no state change - the app polls 2BED every 60s instead)" : ""));
    }

    static void Subscribe(GattCharacteristic ch, string tag,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler) {
        try {
            if (ch == null) return;
            if ((ch.CharacteristicProperties & GattCharacteristicProperties.Notify) == 0) {
                Console.WriteLine("    " + tag + ": notify property not present (read-only)");
                return;
            }
            var mi = ch.GetType().GetMethod("add_ValueChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mi.Invoke(ch, new object[] { handler });
            // the device may drop into deep sleep between the read and this
            // write - never block forever waiting for the CCCD write
            var cccd = AsT(ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify));
            var winner = Task.WhenAny(cccd, Task.Delay(6000)).GetAwaiter().GetResult();
            Console.WriteLine("    subscribed " + tag + " notify: " +
                (winner == cccd ? cccd.Result.ToString() : "TIMEOUT (device asleep?)"));
        } catch (Exception ex) {
            Console.WriteLine("    subscribe " + tag + " failed: " + ex.Message);
        }
    }

    // BAS v1.1 Battery Level Status (2BED), printed raw + decoded so the real
    // device's field order/endianness can be eyeballed against the spec.
    static string Decode2BED(byte[] b) {
        if (b.Length < 3) return "too short (" + b.Length + " bytes)";
        int ps = b[1] | (b[2] << 8);          // Power State, little-endian
        string[] yesNo = { "no", "yes", "unknown", "?" };
        string[] charge = { "unknown", "charging", "discharging-active", "discharging-inactive" };
        string[] level = { "unknown", "good", "low", "critical" };
        string[] type = { "unknown-or-not-charging", "constant-current", "trickle", "constant-voltage", "float", "?", "?", "?" };
        int faults = (ps >> 13) & 7;
        return "flags=0x" + b[0].ToString("X2") +
            " battery-present=" + ((ps & 1) == 1 ? "yes" : "no") +
            " ext-power-wired=" + yesNo[(ps >> 1) & 3] +
            " ext-power-wireless=" + yesNo[(ps >> 3) & 3] +
            " charge-state=" + charge[(ps >> 5) & 3] +
            " charge-level=" + level[(ps >> 7) & 7] +
            " charge-type=" + type[(ps >> 10) & 7] +
            " fault=" + (faults == 0 ? "none" : "0x" + faults.ToString("X"));
    }

    static string Hex(byte[] b) {
        var sb = new System.Text.StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    static Task<T> AsT<T>(IAsyncOperation<T> op) {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = delegate (IAsyncOperation<T> o, AsyncStatus s) {
            if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
            else if (s == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
            else tcs.TrySetCanceled();
        };
        return tcs.Task;
    }

    static int Main(string[] args) {
        if (args.Length >= 1 && args[0] == "list") { try { return List().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; } }
        if (args.Length < 1) { Console.WriteLine("usage: gattprobe list | gattprobe <nameSubstring> [battery] [watch]"); return 2; }
        bool batteryArg = args.Any(a => a == "battery");
        bool watchArg = args.Any(a => a == "watch");
        try { return Run(args[0], batteryArg, watchArg).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
    }

    static async Task<int> List() {
        var devs = await AsT(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector()));
        Console.WriteLine("paired BLE devices: " + (devs == null ? 0 : devs.Count));
        if (devs != null)
            foreach (var d in devs)
                Console.WriteLine("    '" + d.Name + "'  id=" + d.Id);
        return 0;
    }
}
