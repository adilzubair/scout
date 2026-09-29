namespace Scout.Core;

public interface IAudioControl
{
    Task StepAsync(bool increase);
    Task SetMutedAsync(bool muted);
}

public sealed record BrightnessDisplay(string InstanceName, bool Active, byte Current, IReadOnlyList<byte> Levels);

public interface IBrightnessControl
{
    Task<IReadOnlyList<BrightnessDisplay>> GetDisplaysAsync();
    Task SetBrightnessAsync(string instanceName, byte brightness);
}

public static class HardwareControl
{
    public static async Task ExecuteAsync(string id, IAudioControl audio, IBrightnessControl brightness)
    {
        switch (id)
        {
            case "volume-up": await audio.StepAsync(true); break;
            case "volume-down": await audio.StepAsync(false); break;
            case "mute-on": await audio.SetMutedAsync(true); break;
            case "mute-off": await audio.SetMutedAsync(false); break;
            case "brightness-up": await ChangeBrightnessAsync(brightness, true); break;
            case "brightness-down": await ChangeBrightnessAsync(brightness, false); break;
            default: throw new InvalidOperationException("Unknown hardware control action.");
        }
    }

    private static async Task ChangeBrightnessAsync(IBrightnessControl brightness, bool increase)
    {
        var active = (await brightness.GetDisplaysAsync()).Where(display => display.Active).ToArray();
        if (active.Length == 0) throw new IOException("Integrated-display brightness controls are unavailable.");
        if (active.Length > 1) throw new IOException("Brightness controls are ambiguous because Windows reported multiple active integrated displays.");

        var display = active[0];
        var levels = display.Levels.Distinct().Where(level => level <= 100).Order().ToArray();
        if (levels.Length == 0) throw new IOException("The integrated display did not report supported brightness levels.");
        if (!levels.Contains(display.Current)) throw new IOException("The integrated display reported an unsupported current brightness level.");

        var candidates = levels.Where(level => increase ? level > display.Current : level < display.Current).ToArray();
        if (candidates.Length == 0) return;
        var target = Math.Clamp(display.Current + (increase ? 10 : -10), 0, 100);
        var next = candidates.OrderBy(level => Math.Abs(level - target)).ThenBy(level => level).First();
        await brightness.SetBrightnessAsync(display.InstanceName, next);
    }
}
