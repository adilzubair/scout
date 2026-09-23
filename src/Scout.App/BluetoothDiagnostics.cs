using Scout.Core;

namespace Scout.App;

public partial class MainWindow
{
    internal async Task<object> DiagnoseBluetoothAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!index.Catalog.Contains("system:bluetooth-off")) await Task.Delay(25, timeout.Token);
        var selections = new List<object>();
        foreach (var (query, target) in new[] { ("bluetooth off", "bluetooth-off"), ("turn off bluetooth", "bluetooth-off"), ("disable bluetooth", "bluetooth-off"), ("bluetooth on", "bluetooth-on"), ("enable bluetooth", "bluetooth-on"), ("wifi off", "wifi-off") })
        {
            Query.Text = query; await WaitForSearch();
            if (Selected?.Target != target || Selected.Kind != ItemKind.System) throw new InvalidOperationException("Wrong desktop command for " + query);
            selections.Add(new { query, target = Selected.Target });
        }
        var radios = await WindowsCommands.BluetoothRadiosAsync(timeout.Token);
        var bluetooth = radios.Where(r => r.Kind == DeviceRadioKind.Bluetooth).ToArray();
        if (bluetooth.Length == 0) throw new IOException("No Bluetooth radio found for the hardware integration check.");
        var before = radios.ToDictionary(r => r, r => r.State);
        foreach (var radio in bluetooth)
        {
            if (radio.State is not (DeviceRadioState.On or DeviceRadioState.Off)) throw new IOException("Bluetooth is not in a controllable state: " + radio.State);
            // Exercise the native Windows setter with the already-current state.
            // This verifies real access without disconnecting paired peripherals.
            if (!await radio.SetStateAsync(before[radio], timeout.Token)) throw new IOException("Windows rejected the Bluetooth radio setter.");
            if (radio.State != before[radio]) throw new IOException("Bluetooth changed unexpectedly during the same-state check.");
        }
        var uiExecuted = false;
        if (bluetooth.Select(r => r.State).Distinct().Count() == 1)
        {
            Query.Text = bluetooth[0].State == DeviceRadioState.On ? "bluetooth on" : "bluetooth off";
            await WaitForSearch(); await OpenSelected();
            if (IsVisible) throw new IOException("The desktop Bluetooth action failed: " + Status.Text);
            uiExecuted = true;
        }
        if (radios.Any(r => r.State != before[r])) throw new IOException("A radio changed unexpectedly during the same-state check.");
        return new { passed = true, selections, bluetoothRadios = bluetooth.Select(r => new { name = r.Name, state = r.State.ToString() }), nativeSetterAccepted = true, uiExecuted, radioStatesPreserved = true, note = "Real hardware setter and UI dispatch exercised with the current state; off/on transitions covered by simulated radios." };
    }
}
