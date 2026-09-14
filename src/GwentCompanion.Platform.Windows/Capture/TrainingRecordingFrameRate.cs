namespace GwentCompanion.Platform.Windows.Capture;

public static class TrainingRecordingFrameRate
{
    public const int LowStorage = 1;
    public const int Recommended = 2;
    public const int Contributor = 10;

    public static IReadOnlyList<int> Supported { get; } = [LowStorage, Recommended, Contributor];

    public static int Normalize(int framesPerSecond) => framesPerSecond == 5
        ? Contributor // Migrate the former contributor preset without silently lowering retained evidence.
        : Supported.Contains(framesPerSecond) ? framesPerSecond : Recommended;

    public static TimeSpan Interval(int framesPerSecond) =>
        TimeSpan.FromSeconds(1d / Normalize(framesPerSecond));
}
