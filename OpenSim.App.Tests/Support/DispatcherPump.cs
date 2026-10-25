using System.Windows.Threading;

namespace OpenSim.App.Tests.Support;

/// <summary>
/// Runs an async view-model command the way the application does: its continuations come back
/// to the calling thread through a WPF <see cref="Dispatcher"/>, pumped here until the task
/// completes, so Freezables and collections the view models created on this thread are only
/// ever touched from it. Without this, the continuation after a command's <c>await Task.Run</c>
/// lands on a thread-pool thread and the first <c>PointCollection</c> or <c>Model3DGroup</c>
/// it touches throws "Cannot use a DependencyObject that belongs to a different thread".
/// </summary>
public static class DispatcherPump
{
    public static void Run(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var frame = new DispatcherFrame();
            Task? task = null;
            dispatcher.BeginInvoke(() =>
            {
                task = action();
                task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            });
            Dispatcher.PushFrame(frame);
            task!.GetAwaiter().GetResult();      // rethrows the command's own failure, if any
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
