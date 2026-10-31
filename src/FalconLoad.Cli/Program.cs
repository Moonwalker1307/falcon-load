using System.Runtime.InteropServices;
using FalconLoad.Cli;

// Ctrl+C and `docker stop` both end the test early; the results so far are still printed and written.
using var stop = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

var console = new CliConsole(Console.In, Console.Out, Console.Error, ErrorIsTerminal: !Console.IsErrorRedirected);
return await CliApp.RunAsync(args, console, cancellationToken: stop.Token);

void Stop(PosixSignalContext context)
{
    // A second signal while the test is already stopping ends the process the usual way.
    if (!stop.IsCancellationRequested)
    {
        context.Cancel = true;
        stop.Cancel();
    }
}
