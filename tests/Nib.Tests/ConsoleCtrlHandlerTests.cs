using Nib.Terminal;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// What the console control handler does with each event.
///
/// Returning true means "handled, do not tear the process down". Ctrl+C is a copy
/// key in nib, so its event is swallowed; everything else is a genuine request to
/// go away and gets the terminal restored before the default handler runs.
///
/// In normal operation the Ctrl+C case never fires at all, because clearing
/// ENABLE_PROCESSED_INPUT stops the console generating the event. That is exactly
/// why it is worth pinning: the input mode was the only thing standing between a
/// stray Ctrl+C and Windows terminating the editor mid-edit with the buffer
/// unsaved, and a single point of failure with nothing behind it is not a plan.
/// </summary>
public class ConsoleCtrlHandlerTests
{
    private const uint CtrlC = 0;
    private const uint CtrlBreak = 1;
    private const uint CtrlClose = 2;
    private const uint CtrlLogoff = 5;
    private const uint CtrlShutdown = 6;

    [Fact]
    public void Ctrl_C_is_swallowed_so_it_cannot_kill_the_editor() =>
        Assert.True(ConsoleHost.OnConsoleCtrl(CtrlC));

    /// <summary>Ctrl+Break stays fatal deliberately — there has to be a way out.</summary>
    [Theory]
    [InlineData(CtrlBreak)]
    [InlineData(CtrlClose)]
    [InlineData(CtrlLogoff)]
    [InlineData(CtrlShutdown)]
    public void Every_other_event_still_tears_the_process_down(uint ctrlType) =>
        Assert.False(ConsoleHost.OnConsoleCtrl(ctrlType));
}
