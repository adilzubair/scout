using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Scout.App;
using Scout.Core;

// Opt-in interactive audit; opens only this process's dialogs and saves no user settings.
internal static class FolderPickerSmoke
{
    public static int Run(string output)
    {
        Exception? failure = null;
        var checks = new List<string>();
        var fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "picker-fixture", "Selected 東京");
        Directory.CreateDirectory(fixture);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Scout.App.App(); app.InitializeComponent();
                var settings = new SettingsWindow(new ScoutSettings(), _ => true, () => { });
                try
                {
                    settings.Show();
                    UiDiagnostics.Descendants<Button>(settings).Single(b => AutomationProperties.GetName(b) == "Folders").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    settings.UpdateLayout();
                    UiDiagnostics.Descendants<CheckBox>(settings).Single(b => AutomationProperties.GetName(b) == "Search inside files in selected folders").IsChecked = true;
                    settings.UpdateLayout();
                    var field = UiDiagnostics.Descendants<TextBox>(settings).Single(f => (AutomationProperties.GetLabeledBy(f) as TextBlock)?.Text == "Content search folders");
                    var contentBrowse = UiDiagnostics.Descendants<Button>(settings).Single(b => AutomationProperties.GetName(b) == "Browse for a content search folder");
                    if (!contentBrowse.IsEnabled || !field.IsEnabled) throw new InvalidOperationException("Enabling content search did not enable Browse.");
                    ClickPicker(settings, contentBrowse, fixture);
                    if (!field.Text.Equals(fixture, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Selected folder was not appended to the content list: {field.Text}");
                    checks.Add("Content toggle, native folder selection and Unicode path append");
                    ClickPicker(settings, contentBrowse, null);
                    if (field.Text != fixture) throw new InvalidOperationException("Cancel modified the folder list.");
                    checks.Add("Cancel preserves content folder list and restores owner");
                    ClickPicker(settings, contentBrowse, fixture);
                    if (field.Text != fixture) throw new InvalidOperationException("Repeat selection duplicated the folder.");
                    checks.Add("Repeated native selection does not duplicate the folder");
                    foreach (var name in new[] { "Browse for an additional folder", "Browse for an excluded folder", "Browse for an automation folder" })
                    {
                        ClickPicker(settings, UiDiagnostics.Descendants<Button>(settings).Single(b => AutomationProperties.GetName(b) == name), null);
                        checks.Add(name + " opens and cancels");
                    }
                    var general = UiDiagnostics.Descendants<Button>(settings).Single(b => AutomationProperties.GetName(b) == "General");
                    general.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (AutomationProperties.GetItemStatus(general) != "Selected") throw new InvalidOperationException("Settings stopped responding after closing the picker.");
                    checks.Add("Settings navigation remains responsive after all dialogs");
                }
                finally { settings.Close(); }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) failure = new TimeoutException("Native folder picker audit exceeded 45 seconds.");
        var report = new { passed = failure == null, checks, error = failure?.ToString() };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, checks = checks.Count, report.error }));
        return failure == null ? 0 : 1;
    }

    private static void ClickPicker(Window owner, Button browse, string? folder)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var threadId = GetCurrentThreadId();
        var automation = Task.Run(async () =>
        {
            nint dialog = 0;
            try
            {
                var timer = Stopwatch.StartNew();
                while (dialog == 0 && timer.Elapsed < TimeSpan.FromSeconds(10))
                {
                    EnumThreadWindows(threadId, (handle, _) =>
                    {
                        var name = new StringBuilder(100); GetClassName(handle, name, name.Capacity);
                        if (GetWindow(handle, 4) == ownerHandle && name.ToString() == "#32770") dialog = handle;
                        return true;
                    }, 0);
                    if (dialog == 0) await Task.Delay(50);
                }
                if (dialog == 0) throw new TimeoutException("Native folder dialog did not open.");
                await Task.Delay(800);
                var root = AutomationElement.FromHandle(dialog);
                if (folder != null)
                {
                    object? value = null;
                    while (value == null && timer.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        var edit = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1152"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                        if (edit != null) edit.TryGetCurrentPattern(ValuePattern.Pattern, out value);
                        if (value == null) await Task.Delay(100);
                    }
                    if (value == null) throw new InvalidOperationException("Folder path edit is unavailable.");
                    ((ValuePattern)value).SetValue(folder);
                }
                var button = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, folder == null ? "2" : "1"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                if (button == null || !button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new InvalidOperationException("Native Select/Cancel button is unavailable.");
                button.SetFocus();
                await Task.Delay(200);
                ((InvokePattern)invoke).Invoke();
                await Task.Delay(500);
                // A full path can navigate into the folder before confirming that folder.
                if (folder != null && IsWindowVisible(dialog))
                {
                    var select = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                    ((InvokePattern)select.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }
                while (IsWindowVisible(dialog) && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(50);
                if (IsWindowVisible(dialog)) throw new TimeoutException("Native folder dialog did not close.");
            }
            finally { if (dialog != 0 && IsWindowVisible(dialog)) PostMessage(dialog, 0x0010, 0, 0); }
        });
        browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        automation.GetAwaiter().GetResult();
        if (!IsWindowEnabled(ownerHandle)) throw new InvalidOperationException("Native picker left its owner disabled.");
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint word, nint value);
}
