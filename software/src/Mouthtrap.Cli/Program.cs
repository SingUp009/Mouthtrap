using ConsoleAppFramework;
using Mouthtrap.Cli;

// Await cancellation and resource release instead of the framework's forced five-second shutdown.
ConsoleApp.Timeout = Timeout.InfiniteTimeSpan;
var app = ConsoleApp.Create();
app.Add<Commands>();
await app.RunAsync(args);
