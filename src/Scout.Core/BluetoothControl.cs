namespace Scout.Core;

public enum DeviceRadioKind { Other, Wifi, Bluetooth }
public enum DeviceRadioState { Unknown, On, Off, Disabled }

public interface IDeviceRadio
{
    string Name { get; }
    DeviceRadioKind Kind { get; }
    DeviceRadioState State { get; }
    Task<bool> SetStateAsync(DeviceRadioState state, CancellationToken token);
}

public static class BluetoothControl
{
    public static async Task SetAsync(IEnumerable<IDeviceRadio> radios, bool enabled, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var bluetooth = radios.Where(r => r.Kind == DeviceRadioKind.Bluetooth).ToArray();
        if (bluetooth.Length == 0) throw new IOException("No Bluetooth radio was found. Check Bluetooth settings or the adapter's driver.");
        if (bluetooth.Any(r => r.State == DeviceRadioState.Disabled)) throw new IOException("Bluetooth is disabled by hardware or Windows policy. Check Bluetooth settings.");
        var desired = enabled ? DeviceRadioState.On : DeviceRadioState.Off;
        foreach (var radio in bluetooth)
        {
            token.ThrowIfCancellationRequested();
            if (radio.State == desired) continue;
            if (!await radio.SetStateAsync(desired, token)) throw new IOException($"Windows denied the Bluetooth change for {radio.Name}. Check Bluetooth settings.");
            // Windows can acknowledge the request before publishing the new state.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (radio.State != desired && DateTime.UtcNow < deadline) await Task.Delay(50, token);
            if (radio.State != desired) throw new IOException($"Bluetooth did not turn {(enabled ? "on" : "off")}. Check Bluetooth settings or the adapter's hardware switch.");
        }
    }
}
