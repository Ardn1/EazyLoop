using System.Windows;
using System.Windows.Controls;

namespace EazyLoop;

public partial class TrackRow
{
    private sealed record CopiedRack(bool Bypassed, bool EqEnabled, EqBand[] Bands, FxWindow.CopiedEffect[] Slots);
    private static CopiedRack? copiedFx;
    private readonly EditHistory<FxRack.RackState> fxHistory;
    private FxWindow? effects;

    private void OpenEffects(object sender, RoutedEventArgs e)
    {
        if (effects != null) { effects.Activate(); return; }
        effects = new FxWindow(Data.Fx, TrackName.Text, NotifyFxEdited, fxHistory, ApplyFxPreset) { Owner = Window.GetWindow(this) };
        effects.Closed += (_, _) => effects = null;
        effects.Show();
    }

    internal void UpdateFxButton()
    {
        bool configured = !Data.Fx.Bypassed &&
            (!Data.Fx.Bands.SequenceEqual(FxCatalog.Flat) || Data.Fx.Slots.Length > 0);
        FxButton.SetResourceReference(Control.BackgroundProperty, configured ? "AccentPanel" : "Field");
        FxButton.SetResourceReference(Control.ForegroundProperty, configured ? "Violet" : "Soft");
    }

    internal void CaptureFxState()
    {
        if (effects != null) CaptureEffectStates();
        if (midiEditor?.CaptureState() == true) ContentEdited?.Invoke();
    }

    private void CaptureEffectStates()
    {
        if (!Data.Fx.SavePluginStates()) return;
        fxHistory.Commit();
        NotifyFxEdited();
    }

    private void CopyAllFxClicked(object sender, RoutedEventArgs e)
    {
        var rack = Data.Fx;
        if (rack.Snapshot() == null) return;
        try
        {
            CaptureEffectStates();
            copiedFx = new CopiedRack(rack.Bypassed, rack.EqEnabled, [.. rack.Bands],
                rack.Slots.Select(FxWindow.CopySettings).ToArray());
        }
        catch (Exception ex) { ReportFx(ex); }
    }

    private void PasteAllFxClicked(object sender, RoutedEventArgs e)
    {
        if (copiedFx is not { } copy) return;
        try { ApplyCopiedFx(copy); }
        catch (Exception ex) { ReportFx(ex); }
    }

    private void ApplyFxPreset(TrackFxSettings settings) => ApplyCopiedFx(
        new CopiedRack(settings.Bypassed, settings.EqEnabled, settings.Bands,
            settings.Slots.Select(slot => new FxWindow.CopiedEffect(slot, false)).ToArray()));

    private void ApplyCopiedFx(CopiedRack copy)
    {
        CaptureEffectStates();
        Exception? connectionError = null;
        var slots = copy.Slots.Select(item =>
        {
            var slot = FxWindow.CreateCopy(item, out var error);
            connectionError ??= error;
            return slot;
        }).ToArray();
        ReplaceFx(copy.Bypassed, copy.EqEnabled, [.. copy.Bands], slots);
        fxHistory.Commit();
        NotifyFxEdited();
        if (connectionError != null) ReportFx(connectionError);
    }

    private void RestoreFx(FxRack.RackState state)
    {
        var old = Data.Fx.Slots;
        Exception? connectionError = null;
        var slots = state.Slots.Select(saved =>
        {
            var slot = Array.Find(old, item => item.Id == saved.Id);
            if (slot != null)
            {
                slot.Update(saved.Settings with { Plugin = slot.Settings.Plugin });
                return slot;
            }
            slot = FxWindow.CreateCopy(new FxWindow.CopiedEffect(saved.Settings, saved.Connected), out var error, saved.Id);
            connectionError ??= error;
            return slot;
        }).ToArray();
        ReplaceFx(state.Bypassed, state.EqEnabled, state.Bands, slots);
        if (connectionError != null) ReportFx(connectionError);
    }

    private void ReplaceFx(bool bypassed, bool eqEnabled, EqBand[] bands, FxSlot[] slots)
    {
        var removed = Data.Fx.Slots.Except(slots).ToArray();
        foreach (var slot in removed) effects?.CloseSlotEditor(slot);
        Data.Fx.Replace(bypassed, eqEnabled, bands, slots);
        foreach (var slot in removed) slot.Dispose();
        effects?.RefreshRack();
    }

    private void NotifyFxEdited()
    {
        UpdateFxButton();
        ContentEdited?.Invoke();
    }

    private void ReportFx(Exception error) => MessageBox.Show(Window.GetWindow(this), error.Message,
        Locale.T("Эффекты дорожки"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
