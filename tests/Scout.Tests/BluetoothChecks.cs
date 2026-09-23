using Scout.Core;

internal static class BluetoothChecks
{
    public static void Run(Action<string, Action> test)
    {
        var memory = new Dictionary<string, LibraryItem>();
        void Check(bool pass) { if (!pass) throw new Exception("Bluetooth assertion failed"); }
        SearchResult Find(string query, Scope scope = Scope.All) => SearchEngine.Search(query, scope, SystemCommands.Items, memory, DateTimeOffset.Now);
        foreach (var query in new[] { "bluetooth off", "turn off bluetooth", "disable bluetooth", "switch bluetooth off", "bt off", "please turn off my bluetooth" })
            test("Bluetooth off intent: " + query, () => { var r = Find(query); Check(r.Hits[0].Candidate.Target == "bluetooth-off" && RankingPolicy.Ready(r, r.Hits)); });
        foreach (var query in new[] { "bluetooth on", "turn on bluetooth", "enable bluetooth", "switch bluetooth on", "bt on" })
            test("Bluetooth on intent: " + query, () => Check(Find(query).Hits[0].Candidate.Target == "bluetooth-on"));
        test("Bare Bluetooth remains ambiguous", () => { var r = Find("bluetooth"); Check(r.Hits.Count == 2 && !RankingPolicy.Ready(r, r.Hits)); });
        test("Bluetooth command obeys Files scope", () => Check(Find("bluetooth off", Scope.Files).Hits.Count == 0));
        test("Bluetooth commands cannot be grouped or pinned", () => Check(Find("bluetooth off").Hits.All(h => !h.Candidate.IsGroupable && !h.Candidate.IsPinnable)));
        test("Turning Bluetooth off leaves Wi-Fi untouched", () =>
        {
            var bluetooth = new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On);
            var wifi = new FakeRadio(DeviceRadioKind.Wifi, DeviceRadioState.On);
            BluetoothControl.SetAsync([wifi, bluetooth], false).GetAwaiter().GetResult();
            Check(bluetooth.State == DeviceRadioState.Off && bluetooth.Calls == 1 && wifi.State == DeviceRadioState.On && wifi.Calls == 0);
            BluetoothControl.SetAsync([wifi, bluetooth], true).GetAwaiter().GetResult();
            Check(bluetooth.State == DeviceRadioState.On && wifi.Calls == 0);
        });
        test("Bluetooth changes are idempotent", () => { var radio = new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.Off); BluetoothControl.SetAsync([radio], false).GetAwaiter().GetResult(); Check(radio.Calls == 0); });
        test("Every Bluetooth adapter changes", () => { var a = new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On); var b = new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On); BluetoothControl.SetAsync([a, b], false).GetAwaiter().GetResult(); Check(a.State == DeviceRadioState.Off && b.State == DeviceRadioState.Off); });
        void Fails(IDeviceRadio[] radios, string explanation)
        {
            try { BluetoothControl.SetAsync(radios, false).GetAwaiter().GetResult(); }
            catch (IOException e) { Check(e.Message.Contains(explanation, StringComparison.OrdinalIgnoreCase)); return; }
            throw new Exception("Expected actionable radio failure");
        }
        test("Missing Bluetooth does not fall back to Wi-Fi", () => { var wifi = new FakeRadio(DeviceRadioKind.Wifi, DeviceRadioState.On); Fails([wifi], "No Bluetooth"); Check(wifi.Calls == 0); });
        test("Denied Bluetooth reports failure", () => Fails([new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On) { Allowed = false }], "denied"));
        test("Disabled Bluetooth reports hardware or policy block", () => Fails([new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.Disabled)], "disabled"));
        test("Bluetooth state must actually change after acceptance", () => Fails([new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On) { PublishState = false }], "did not turn off"));
        test("Cancelled Bluetooth request cannot modify a radio", () =>
        {
            var radio = new FakeRadio(DeviceRadioKind.Bluetooth, DeviceRadioState.On); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { BluetoothControl.SetAsync([radio], false, cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Check(radio.Calls == 0); return; }
            throw new Exception("Expected cancellation");
        });
    }
    private sealed class FakeRadio(DeviceRadioKind kind, DeviceRadioState state) : IDeviceRadio
    {
        public string Name => "Test radio";
        public DeviceRadioKind Kind => kind;
        public DeviceRadioState State { get; private set; } = state;
        public bool Allowed { get; init; } = true;
        public bool PublishState { get; init; } = true;
        public int Calls { get; private set; }
        public Task<bool> SetStateAsync(DeviceRadioState value, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; if (Allowed && PublishState) State = value; return Task.FromResult(Allowed); }
    }
}
