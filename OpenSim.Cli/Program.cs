using OpenSim.Cli;

// Ctrl+C asks the running command to stop (the optics commands and optics sweeps poll it, OP-97);
// a second Ctrl+C ends the process as usual.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested) return;
    e.Cancel = true;
    cancellation.Cancel();
};
return CliRunner.Run(args, Console.Out, Console.Error, cancellation.Token);
