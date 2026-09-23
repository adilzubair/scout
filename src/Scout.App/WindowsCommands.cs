using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Scout.Core;
using Windows.Devices.Radios;
using WinRadioState = Windows.Devices.Radios.RadioState;

namespace Scout.App;

internal static class WindowsCommands
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nuint wp, string lp, uint flags, uint timeout, out nuint result);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, int opcode, nint reserved, out uint size, out nint data, out int type);
    [DllImport("wlanapi.dll")] private static extern uint WlanSetInterface(nint handle, ref Guid id, int opcode, uint size, ref RadioState state, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint data);
    [StructLayout(LayoutKind.Sequential)] private struct RadioState { public uint Index, Software, Hardware; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WlanInterface { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public uint State; }

    public static async Task ExecuteAsync(string id, nint owner)
    {
        if (!SystemCommands.IsKnown(id)) throw new InvalidOperationException("Unknown system action.");
        switch (id)
        {
            case "lock": if (!LockWorkStation()) throw new System.ComponentModel.Win32Exception(); break;
            case "sleep": if (!SetSuspendState(false, false, false)) throw new System.ComponentModel.Win32Exception(); break;
            case "focus": Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); break;
            case "recycle":
                // Native shell owns the confirmation and progress UI. No no-confirm flag.
                var hr = SHEmptyRecycleBin(owner, null, 0); if (hr < 0 && hr != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(hr); break;
            case "appearance":
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0 ? 1 : 0;
                    key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord); key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
                }
                SendMessageTimeout(0xffff, 0x001a, 0, "ImmersiveColorSet", 2, 1000, out _); break;
            case "hidden-show": case "hidden-hide":
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")) key.SetValue("Hidden", id == "hidden-show" ? 1 : 2, RegistryValueKind.DWord);
                SendMessageTimeout(0xffff, 0x001a, 0, "ShellState", 2, 1000, out _); SHChangeNotify(0x08000000, 0, 0, 0); break;
            case "wifi-on": case "wifi-off": SetWifi(id == "wifi-on"); break;
            case "bluetooth-on": case "bluetooth-off":
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    try { await BluetoothControl.SetAsync(await BluetoothRadiosAsync(timeout.Token), id == "bluetooth-on", timeout.Token); }
                    catch (OperationCanceledException) { throw new IOException("Bluetooth did not respond in time. Check Bluetooth settings."); }
                }
                break;
            default: throw new InvalidOperationException("System action has no Windows implementation.");
        }
    }
    internal static async Task<IDeviceRadio[]> BluetoothRadiosAsync(CancellationToken token)
    {
        // RequestAccessAsync must start on the UI thread. This is the Windows
        // radio API; disabling PnP devices would require elevated privileges.
        var access = await Radio.RequestAccessAsync().AsTask(token);
        if (access != RadioAccessStatus.Allowed) throw new IOException($"Windows denied Bluetooth radio access ({access}). Check Bluetooth settings and radio-control permissions.");
        var radios = await Radio.GetRadiosAsync().AsTask(token);
        return radios.Select(r => (IDeviceRadio)new WindowsRadio(r)).ToArray();
    }
    private sealed class WindowsRadio(Radio radio) : IDeviceRadio
    {
        public string Name => radio.Name;
        public DeviceRadioKind Kind => radio.Kind switch { RadioKind.Bluetooth => DeviceRadioKind.Bluetooth, RadioKind.WiFi => DeviceRadioKind.Wifi, _ => DeviceRadioKind.Other };
        public DeviceRadioState State => radio.State switch { WinRadioState.On => DeviceRadioState.On, WinRadioState.Off => DeviceRadioState.Off, WinRadioState.Disabled => DeviceRadioState.Disabled, _ => DeviceRadioState.Unknown };
        public async Task<bool> SetStateAsync(DeviceRadioState state, CancellationToken token) => await radio.SetStateAsync(state == DeviceRadioState.On ? WinRadioState.On : WinRadioState.Off).AsTask(token) == RadioAccessStatus.Allowed;
    }
    private static void SetWifi(bool enabled)
    {
        nint handle = 0, list = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out handle) != 0 || WlanEnumInterfaces(handle, 0, out list) != 0) throw new IOException("Wi-Fi controls are unavailable.");
            var count = Math.Clamp(Marshal.ReadInt32(list), 0, 32); var updated = 0;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<WlanInterface>(list + 8 + i * Marshal.SizeOf<WlanInterface>());
                nint data = 0;
                try
                {
                    if (WlanQueryInterface(handle, ref item.Id, 4, 0, out _, out data, out _) != 0) continue;
                    var radios = Math.Clamp(Marshal.ReadInt32(data), 0, 64);
                    for (var j = 0; j < radios; j++)
                    {
                        var state = Marshal.PtrToStructure<RadioState>(data + 4 + j * Marshal.SizeOf<RadioState>());
                        if (enabled && state.Hardware == 2) throw new IOException("The physical Wi-Fi switch is off.");
                        state.Software = enabled ? 1u : 2u;
                        if (WlanSetInterface(handle, ref item.Id, 4, (uint)Marshal.SizeOf<RadioState>(), ref state, 0) != 0) throw new IOException("Windows denied the Wi-Fi change. Use Wi-Fi settings.");
                        updated++;
                    }
                }
                finally { if (data != 0) WlanFreeMemory(data); }
            }
            if (updated == 0) throw new IOException("No controllable Wi-Fi radio was found.");
        }
        finally { if (list != 0) WlanFreeMemory(list); if (handle != 0) WlanCloseHandle(handle, 0); }
    }
}
