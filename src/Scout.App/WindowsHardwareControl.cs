using System.Management;
using System.Runtime.InteropServices;
using Scout.Core;

namespace Scout.App;

internal sealed class CoreAudioControl : IAudioControl
{
    public Task StepAsync(bool increase)
    {
        UseDefaultEndpoint(volume => ThrowIfFailed(increase ? volume.VolumeStepUp(0) : volume.VolumeStepDown(0)));
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted)
    {
        UseDefaultEndpoint(volume => ThrowIfFailed(volume.SetMute(muted, 0)));
        return Task.CompletedTask;
    }

    private static void UseDefaultEndpoint(Action<IAudioEndpointVolume> action)
    {
        object? enumeratorObject = null;
        object? endpointObject = null;
        object? volumeObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var result = enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, AudioRole.Console, out var endpoint);
            if (result == unchecked((int)0x80070490)) throw new IOException("No default output audio device was found.");
            ThrowIfFailed(result);
            endpointObject = endpoint;
            var interfaceId = typeof(IAudioEndpointVolume).GUID;
            ThrowIfFailed(endpoint.Activate(ref interfaceId, 23, 0, out volumeObject));
            action((IAudioEndpointVolume)volumeObject);
        }
        finally
        {
            Release(volumeObject);
            Release(endpointObject);
            Release(enumeratorObject);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}

internal sealed class WmiBrightnessControl : IBrightnessControl
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<BrightnessDisplay>> GetDisplaysAsync()
    {
        try
        {
            return await Task.Run<IReadOnlyList<BrightnessDisplay>>(() =>
            {
                using var searcher = CreateSearcher("SELECT Active, CurrentBrightness, InstanceName, Level FROM WmiMonitorBrightness");
                using var results = searcher.Get();
                var displays = new List<BrightnessDisplay>();
                foreach (ManagementObject result in results)
                {
                    using (result)
                    {
                        if (result["InstanceName"] is not string instanceName || string.IsNullOrWhiteSpace(instanceName) ||
                            result["Active"] is not bool active || result["CurrentBrightness"] is not byte current ||
                            result["Level"] is not byte[] levels)
                            throw InvalidResponse("integrated-display brightness data");
                        displays.Add(new(instanceName, active, current, levels));
                    }
                }
                return displays;
            });
        }
        catch (ManagementException exception)
        {
            throw WmiFailure("read integrated-display brightness controls", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw AccessDenied("read integrated-display brightness controls", exception);
        }
    }

    public async Task SetBrightnessAsync(string instanceName, byte brightness)
    {
        try
        {
            await Task.Run(() =>
            {
                using var searcher = CreateSearcher("SELECT * FROM WmiMonitorBrightnessMethods");
                using var results = searcher.Get();
                var methods = new List<ManagementObject>();
                try
                {
                    foreach (ManagementObject method in results) methods.Add(method);
                    var matches = methods.Where(method => method["InstanceName"] is string name && string.Equals(name, instanceName, StringComparison.Ordinal)).ToArray();
                    if (methods.Any(method => method["InstanceName"] is not string name || string.IsNullOrWhiteSpace(name)))
                        throw InvalidResponse("integrated-display brightness method data");
                    if (matches.Length == 0) throw new IOException("Windows did not expose a brightness method for the integrated display.");
                    if (matches.Length > 1) throw new IOException("Windows exposed ambiguous brightness methods for the integrated display.");

                    using var input = matches[0].GetMethodParameters("WmiSetBrightness");
                    if (input == null) throw InvalidResponse("WmiSetBrightness parameters");
                    input["Timeout"] = 0u;
                    input["Brightness"] = brightness;
                    using var output = matches[0].InvokeMethod("WmiSetBrightness", input, new InvokeMethodOptions { Timeout = ProviderTimeout });
                    if (output?["ReturnValue"] is not object returnValue) throw InvalidResponse("WmiSetBrightness result");
                    uint result;
                    try { result = Convert.ToUInt32(returnValue); }
                    catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
                    {
                        throw InvalidResponse("WmiSetBrightness result", exception);
                    }
                    if (result != 0) throw new IOException($"Windows rejected the brightness change (WMI result {result}).");
                }
                finally
                {
                    foreach (var method in methods) method.Dispose();
                }
            });
        }
        catch (ManagementException exception)
        {
            throw WmiFailure("change integrated-display brightness", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw AccessDenied("change integrated-display brightness", exception);
        }
    }

    private static ManagementObjectSearcher CreateSearcher(string query)
    {
        var searcher = new ManagementObjectSearcher(@"root\wmi", query);
        searcher.Options.Timeout = ProviderTimeout;
        searcher.Options.ReturnImmediately = false;
        return searcher;
    }

    private static IOException WmiFailure(string action, ManagementException exception) => exception.ErrorCode switch
    {
        ManagementStatus.Timedout => new IOException($"Windows could not {action} because the brightness provider did not respond within five seconds. Check display drivers and Windows brightness settings.", exception),
        ManagementStatus.AccessDenied or ManagementStatus.PrivilegeNotHeld => AccessDenied(action, exception),
        _ => new IOException($"Windows could not {action} because the brightness provider failed ({exception.ErrorCode}). Check display drivers and Windows brightness settings.", exception)
    };

    private static IOException AccessDenied(string action, Exception exception) =>
        new($"Windows denied permission to {action}. Check Windows policy and brightness-control permissions.", exception);

    private static IOException InvalidResponse(string data, Exception? exception = null) =>
        new($"Windows returned invalid {data}. Check display drivers and Windows brightness settings.", exception);
}

internal enum AudioDataFlow { Render, Capture, All }
internal enum AudioRole { Console, Multimedia, Communications }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, AudioRole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object interfaceObject);
    [PreserveSig] int OpenPropertyStore(uint storageMode, out nint properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(nint notify);
    [PreserveSig] int UnregisterControlChangeNotify(nint notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, nint eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, nint eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(nint eventContext);
    [PreserveSig] int VolumeStepDown(nint eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
}
