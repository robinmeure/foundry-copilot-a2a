using FoundryCopilotA2A.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(120)
};

var application = new CliApplication(
    new CliContext(Console.Out, Console.Error, new ProcessRunner(), httpClient));

return await application.RunAsync(args, cancellation.Token);
