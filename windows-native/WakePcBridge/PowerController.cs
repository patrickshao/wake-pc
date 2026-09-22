using System.Windows.Forms;

namespace WakePcBridge;

public interface IPowerController
{
    Task<bool> SleepAsync(int delayMilliseconds, CancellationToken cancellationToken);
}

public sealed class WindowsPowerController : IPowerController
{
    public async Task<bool> SleepAsync(
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        return Application.SetSuspendState(
            PowerState.Suspend,
            force: false,
            disableWakeEvent: false);
    }
}
