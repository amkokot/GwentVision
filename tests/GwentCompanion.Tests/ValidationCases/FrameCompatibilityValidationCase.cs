internal sealed class FrameCompatibilityValidationCase : IContributorValidationCase
{
    public string Id => "frame-compatibility";
    public string Kind => "detector-safety";
    public string Summary => "Keep resolution handling bounded and reject unvalidated screen geometry without committing board evidence.";

    public Task RunAsync(ContributorValidationContext context)
    {
        _ = context;
        VisionFrameCompatibilityTests.Run();
        return Task.CompletedTask;
    }
}
