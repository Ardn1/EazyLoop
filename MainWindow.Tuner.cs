using System.Windows.Controls;

namespace EazyLoop;

public partial class MainWindow
{
    private Locale.Language? tunerLanguage;
    private double tunerFrequency;
    private bool tunerAvailable;

    private void UpdateTuner(double frequency, bool available)
    {
        if (frequency == tunerFrequency && available == tunerAvailable && tunerLanguage == Locale.Current) return;
        tunerLanguage = Locale.Current;
        tunerFrequency = frequency;
        tunerAvailable = available;

        if (frequency <= 0 || !available)
        {
            TunerNote.Text = "—";
            TunerHertz.Text = "—";
            TunerOffset.Text = "—";
            TunerStatus.Text = Locale.T(available ? "Жду ноту" : "Вход недоступен");
            TunerOffset.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            TunerScaleView.Update(null);
            CompactTunerScale.Update(null);
            return;
        }

        double pitch = 69 + 12 * Math.Log2(frequency / 440);
        int note = (int)Math.Round(pitch);
        double cents = 100 * (pitch - note);
        int rounded = (int)Math.Round(cents);
        bool tuned = TunerScale.IsTuned(cents);
        TunerNote.Text = PianoRollSurface.PitchName(note);
        TunerHertz.Text = Locale.F($"{frequency:0.00} Гц");
        TunerOffset.Text = rounded.ToString("+0;−0;0", Locale.Culture) + " ¢";
        TunerStatus.Text = Locale.T(tuned ? "В строе" : cents < 0 ? "Ниже" : "Выше");
        TunerOffset.SetResourceReference(TextBlock.ForegroundProperty, tuned ? "Mint" : "Warning");
        TunerScaleView.Update(cents);
        CompactTunerScale.Update(cents);
    }
}
