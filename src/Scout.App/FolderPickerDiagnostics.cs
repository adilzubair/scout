using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Scout.App;

public partial class MainWindow
{
    // Opt-in isolated WPF regression: operates only dialogs on this UI thread.
    internal async Task CheckFolderPickerAsync()
    {
        var fixture = Path.Combine(App.DataDirectory, "Folder picker fixture");
        Directory.CreateDirectory(fixture);
        var cancel = false;
        Exception? failure = null;
        var observedNativeDialogs = 0;
        string? Pick(Window owner)
        {
            if (failure != null) throw failure;
            const string title = "Scout folder picker regression";
            var native = nint.Zero;
            var sent = false;
            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
                {
                    var text = new StringBuilder(128);
                    _ = GetWindowText(window, text, text.Capacity);
                    if (text.ToString() == title && GetWindow(window, 4) == new WindowInteropHelper(owner).Handle) native = window;
                    return true;
                }, 0);
                if (native == 0) return;
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) { _ = PostMessage(native, 0x0010, 0, 0); return; }
                var button = GetDlgItem(native, cancel ? 2 : 1);
                if (!sent && button != 0 && IsWindowEnabled(button))
                {
                    sent = PostMessage(native, 0x0111, cancel ? 2 : 1, button);
                    if (sent) observedNativeDialogs++;
                }
            };
            timer.Start();
            try
            {
                var result = FolderPicker.Pick(owner, title, fixture);
                if (!sent || clock.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Native folder dialog did not complete within its regression deadline.");
                if (cancel ? result != null : !string.Equals(result?.TrimEnd('\\'), fixture.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Native folder select/cancel returned the wrong result.");
                return result;
            }
            finally { timer.Stop(); }
        }
        var settings = new SettingsWindow(new(), _ => true, () => { }, pickFolder: Pick) { Owner = this };
        try
        {
            settings.Show();
            UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Multimodal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings.UpdateLayout();
            var field = UiDiagnostics.Descendants<TextBox>(settings).Single(box => (AutomationProperties.GetLabeledBy(box) as TextBlock)?.Text == "Multimodal content folders");
            var browse = UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Browse for a multimodal content folder");
            void Click() => browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click();
            if (field.Text != fixture) throw new IOException("Choosing a folder did not populate multimodal settings.");
            Click(); // Same actual native selection must not append a duplicate.
            cancel = true; Click();
            if (field.Text != fixture || observedNativeDialogs != 3) throw new IOException("Repeat/cancel changed the folder list or skipped the native dialog.");
            foreach (var error in new Exception[] { new COMException("Activation failure", unchecked((int)0x80040154)), new System.ComponentModel.Win32Exception(5), new InvalidOperationException("Dialog unavailable") })
            {
                failure = error; Click();
                if (!settings.IsVisible || field.Text != fixture || !UiDiagnostics.Descendants<TextBlock>(settings).Any(text => text.IsVisible && text.Text.StartsWith("The folder browser couldn't open.", StringComparison.Ordinal)))
                    throw new IOException("A picker failure closed settings, changed its values or lost recovery guidance.");
                field.Text = fixture + Environment.NewLine + Path.Combine(App.DataDirectory, "Manual path");
                field.Text = fixture; // Manual editing must remain usable after failure.
            }
            await Dispatcher.InvokeAsync(() => settings.UpdateLayout(), DispatcherPriority.Background);
            if (!settings.IsEnabled || !browse.IsEnabled) throw new IOException("Settings stayed disabled after the folder dialog closed.");
        }
        finally { settings.Close(); }
    }

    private delegate bool EnumThreadWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, EnumThreadWindowCallback callback, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int item);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
