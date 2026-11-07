using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EazyLoop;

internal sealed class EditHistory<T>(Func<T> capture, Action<T> restore, Func<T, T, bool> same, int capacity = 64)
{
    private readonly List<T> undo = [];
    private readonly List<T> redo = [];
    private T current = capture();
    private bool gesture;
    private bool restoring;

    public event Action? Changed;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Reset()
    {
        current = capture();
        gesture = false;
        undo.Clear();
        redo.Clear();
        Changed?.Invoke();
    }

    public void Commit()
    {
        if (gesture || restoring) return;
        T next = capture();
        if (same(current, next))
        {
            current = next;
            return;
        }
        Push(undo, current);
        current = next;
        redo.Clear();
        Changed?.Invoke();
    }

    public void BeginGesture() => gesture = true;

    public void EndGesture()
    {
        if (!gesture) return;
        gesture = false;
        Commit();
    }

    public void WatchDrag(UIElement control)
    {
        control.AddHandler(Mouse.GotMouseCaptureEvent, new MouseEventHandler((_, _) => BeginGesture()), true);
        control.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => EndGesture()), true);
    }

    public bool Step(bool forward)
    {
        if (gesture || restoring) return false;
        var source = forward ? redo : undo;
        var destination = forward ? undo : redo;
        if (source.Count == 0) return false;
        T before = capture();
        restoring = true;
        try
        {
            restore(source[^1]);
            current = capture();
            source.RemoveAt(source.Count - 1);
            Push(destination, before);
        }
        finally { restoring = false; }
        Changed?.Invoke();
        return true;
    }

    private void Push(List<T> list, T state)
    {
        if (list.Count == capacity) list.RemoveAt(0);
        list.Add(state);
    }
}

internal static class HistoryKeys
{
    public static bool Handle(KeyEventArgs e, Action<bool> step)
    {
        var modifiers = Keyboard.Modifiers;
        bool undo = e.Key == Key.Z && modifiers == ModifierKeys.Control;
        bool redo = e.Key == Key.Y && modifiers == ModifierKeys.Control ||
            e.Key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (!undo && !redo || e.OriginalSource is ComboBoxItem or ComboBox { IsDropDownOpen: true })
            return false;
        if (e.OriginalSource is TextBox field && (undo ? field.CanUndo : field.CanRedo))
        {
            if (undo) field.Undo();
            else field.Redo();
        }
        else if (!e.IsRepeat) step(redo);
        e.Handled = true;
        return true;
    }
}
