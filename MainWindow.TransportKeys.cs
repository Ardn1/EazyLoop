using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace EazyLoop;

public partial class MainWindow
{
    private const int KeyDownMessage = 0x0100;
    private const int SpaceVirtualKey = 0x20;
    private const int PreviousKeyState = 1 << 30;
    private const uint RootWindow = 2;
    private const uint OwnerWindow = 4;
    private const string RecordShortcuts = "R / Ctrl+Space";
    private const string PlaybackKeys = $"Пробел  Играть / Стоп       {RecordShortcuts}  Запись       M  Метроном       Ctrl+T  Дорожка";
    private const string RecordingKeys = $"Пробел  Запись       {RecordShortcuts}  Запись       M  Метроном       Ctrl+T  Дорожка";
    private bool repeatRecording;

    private string? RecordBlockReason()
    {
        if (transport == Transport.Finishing) return Locale.T("Завершение записи…");
        if (missingTrackInput) return Locale.T("Выберите другой вход дорожки");
        if (sound?.CanPlay != true)
            return outputError == null ? Locale.T("Подключение звука…")
                : $"{Locale.T("Ошибка вывода · проверьте настройки")}\n{outputError}";
        if (!sound.CanRecord)
        {
            string? error = sound.InputError;
            string reason = Locale.T(error == null ? "Микрофон пока не передаёт звук." : "Ошибка микрофона.");
            string help = Locale.T("Проверьте выбранный микрофон и доступ к нему в Windows и антивирусе.");
            return error == null ? $"{reason} {help}" : $"{reason} {help}\n{error}";
        }
        return recordingRow == null && rows.Count >= LoopEngine.MaxTracks && NextTrack() == null
            ? Locale.T("Достигнут лимит дорожек.") : null;
    }

    private void TransportKeyMessage(ref MSG message, ref bool handled)
    {
        if (handled || message.message != KeyDownMessage || message.wParam != SpaceVirtualKey ||
            fileBusy || settingsActive || ComponentDispatcher.IsThreadModal)
            return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Control)) return;

        Window? window = FindMessageWindow(message.hwnd);
        if (window == null || !OwnsWindow(window) || !window.IsEnabled) return;

        var focus = Keyboard.FocusedElement;
        if (focus is TextBox { Name: "TitleEditor" } or ComboBox { IsDropDownOpen: true } or ComboBoxItem ||
            IsNativeTextField(message.hwnd))
            return;

        handled = true;
        if ((message.lParam.ToInt64() & PreviousKeyState) != 0) return;
        if (focus is TextBox) Keyboard.ClearFocus();

        Refresh();
        if (modifiers == ModifierKeys.Control) ToggleRecord();
        else if (window == this && repeatRecording)
        {
            if (RecordBlockReason() == null) ToggleRecord();
            else if (transport != Transport.Finishing) TogglePlay();
        }
        else TogglePlay();
    }

    private bool OwnsWindow(Window window)
    {
        for (Window? current = window; current != null; current = current.Owner)
            if (current == this) return true;
        return false;
    }

    private static Window? FindMessageWindow(IntPtr handle)
    {
        // A focused control in a plug-in may have a native child HWND; popups have an owner HWND.
        for (IntPtr window = GetAncestor(handle, RootWindow); window != IntPtr.Zero; window = GetWindow(window, OwnerWindow))
            if (HwndSource.FromHwnd(window)?.RootVisual is Window match) return match;
        return null;
    }

    private static bool IsNativeTextField(IntPtr handle)
    {
        if (HwndSource.FromHwnd(handle) != null) return false;
        var name = new StringBuilder(64);
        if (GetClassName(handle, name, name.Capacity) == 0) return false;
        string kind = name.ToString();
        return kind.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr handle, uint kind);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindow(IntPtr handle, uint kind);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
}
