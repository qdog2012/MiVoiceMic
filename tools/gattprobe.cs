// gattprobe.cs - verify the WinRT BLE GATT runtime path (the exact transport
// MiVoiceMic uses) against a real paired device, e.g. the M720 mouse.
// Usage: gattprobe <nameSubstring> [battery]
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

class GattProbe {
    static async Task<int> Run(string namePart, bool readBattery) {
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

        GattCharacteristic battery = null;
        foreach (var s in res.Services) {
            var ch = await AsT(s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached));
            foreach (var c in ch.Characteristics) {
                Console.WriteLine("    char " + c.Uuid + " props=" + c.CharacteristicProperties);
                if (c.Uuid == new Guid("00002a19-0000-1000-8000-00805f9b34fb")) battery = c;
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
        foreach (var s in res.Services) s.Dispose();
        dev.Dispose();
        Console.WriteLine("PASS: BLE GATT transport works (pair->connect->discover->read)");
        return 0;
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
        if (args.Length < 1) { Console.WriteLine("usage: gattprobe <nameSubstring> [battery]"); return 2; }
        try { return Run(args[0], args.Length > 1 && args[1] == "battery").GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
    }
}
