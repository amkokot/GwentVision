internal sealed class DefaultLiveLayoutValidationCase : IContributorValidationCase
{
    public string Id => "default-live-layout";
    public string Kind => "layout";
    public string Summary => "Keep the default deck, candidate, and snapshot layout compact and structurally complete.";

    public Task RunAsync(ContributorValidationContext context)
    {
        UiShellTests.Navigation(context.ProjectRoot);
        UiShellTests.CompactLive(context.ProjectRoot);
        SummonFocusTests.Run(context.ProjectRoot);
        return Task.CompletedTask;
    }
}
