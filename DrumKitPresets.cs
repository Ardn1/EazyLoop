namespace EazyLoop;

internal sealed record DrumKitPreset(string Name, DrumPad[] Pads)
{
    public static readonly DrumKitPreset[] BuiltIn =
    [
        new("Стандартный", DrumKit.Default.Pads),
        new("Акустический",
        [
            Voice(DrumSound.AcousticKick, 360, 42, 80),
            Voice(DrumSound.ConcertSnare, 210, 55, 69, tune: 2),
            Voice(DrumSound.ClosedHat, 79, 40, 41, pan: -22),
            Voice(DrumSound.Ride, 640, 48, 38, pan: 26),
            Voice(DrumSound.Tambourine, 280, 43, 42, pan: -18),
            Voice(DrumSound.LowTom, 460, 35, 66, tune: -3, pan: 16),
            Voice(DrumSound.HighTom, 380, 44, 60, pan: -14),
            Voice(DrumSound.Rim, 95, 35, 49, tune: -3)
        ]),
        new("Фолк",
        [
            Voice(DrumSound.FrameDrum, 510, 34, 77, tune: -7),
            Voice(DrumSound.FrameDrum, 235, 57, 66, tune: 5),
            Voice(DrumSound.Shaker, 132, 54, 52, pan: -26),
            Voice(DrumSound.Tambourine, 390, 49, 54, pan: 23),
            Voice(DrumSound.Clap, 125, 32, 47, pan: 9),
            Voice(DrumSound.FrameDrum, 390, 42, 63, tune: -2, pan: 15),
            Voice(DrumSound.FrameDrum, 270, 63, 56, tune: 8, pan: -15),
            Voice(DrumSound.Rim, 85, 42, 54, tune: 3)
        ]),
        new("Оркестровый",
        [
            Voice(DrumSound.AcousticKick, 1180, 19, 86, tune: -7),
            Voice(DrumSound.ConcertSnare, 360, 47, 73, pan: -10),
            Voice(DrumSound.Tambourine, 235, 63, 40, pan: 21),
            Voice(DrumSound.Ride, 1430, 33, 58, tune: -4, pan: 27),
            Voice(DrumSound.ConcertSnare, 155, 39, 48, tune: -5, pan: -22),
            Voice(DrumSound.Timpani, 1190, 31, 75, tune: -4, pan: -13),
            Voice(DrumSound.Timpani, 930, 40, 69, tune: 3, pan: 13),
            Voice(DrumSound.Tambourine, 620, 72, 45, tune: 5, pan: -22)
        ]),
        new("Техно",
        [
            Voice(DrumSound.DeepKick, 610, 44, 91, tune: -1),
            Voice(DrumSound.Clap, 220, 74, 68),
            Voice(DrumSound.ClosedHat, 48, 75, 46, tune: 3, pan: -23),
            Voice(DrumSound.OpenHat, 280, 84, 48, pan: 25),
            Voice(DrumSound.Clap, 385, 55, 55, tune: -3, pan: 15),
            Voice(DrumSound.PunchKick, 225, 51, 66, tune: 8, pan: -12),
            Voice(DrumSound.HighTom, 155, 75, 61, tune: 6, pan: 13),
            Voice(DrumSound.Rim, 55, 86, 51, tune: 8)
        ]),
        new("Рок",
        [
            Voice(DrumSound.AcousticKick, 290, 71, 90, tune: -2),
            Voice(DrumSound.TightSnare, 245, 76, 81, tune: -2),
            Voice(DrumSound.ClosedHat, 93, 72, 52, pan: -25),
            Voice(DrumSound.OpenHat, 465, 70, 51, pan: -18),
            Voice(DrumSound.Ride, 860, 61, 53, pan: 30),
            Voice(DrumSound.LowTom, 570, 43, 73, tune: -4, pan: 18),
            Voice(DrumSound.HighTom, 410, 59, 68, tune: -2, pan: -13),
            Voice(DrumSound.Rim, 105, 62, 62, tune: 1)
        ])
    ];

    private static DrumPad Voice(DrumSound sound, float decay, float tone, float level, float tune = 0, float pan = 0) =>
        new(sound, tune, decay, tone, level, pan);
}
