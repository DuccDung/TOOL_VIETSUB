using SubVid.App.Core;
using System.Text.Json;

namespace SubVid.App.Tests;

public sealed class UiModalOperationDispatcherTests
{
    [Fact]
    public async Task TrySchedule_DefersOperationUntilPostedCallbackRuns()
    {
        Func<Task>? posted = null;
        var operationRan = false;
        var dispatcher = new UiModalOperationDispatcher(
            callback => posted = callback,
            () => true);

        var result = dispatcher.TrySchedule(
            () =>
            {
                operationRan = true;
                return Task.CompletedTask;
            },
            _ => { });

        Assert.Equal(UiModalScheduleResult.Scheduled, result);
        Assert.False(operationRan);
        Assert.True(dispatcher.IsBusy);
        Assert.NotNull(posted);

        await posted!();

        Assert.True(operationRan);
        Assert.False(dispatcher.IsBusy);
    }

    [Fact]
    public async Task TrySchedule_RejectsDuplicateUntilActiveOperationCompletes()
    {
        Func<Task>? posted = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new UiModalOperationDispatcher(
            callback => posted = callback,
            () => true);

        Assert.Equal(
            UiModalScheduleResult.Scheduled,
            dispatcher.TrySchedule(() => release.Task, _ => { }));
        Assert.Equal(
            UiModalScheduleResult.Busy,
            dispatcher.TrySchedule(() => Task.CompletedTask, _ => { }));

        var running = posted!();
        Assert.False(running.IsCompleted);
        release.SetResult();
        await running;

        Assert.Equal(
            UiModalScheduleResult.Scheduled,
            dispatcher.TrySchedule(() => Task.CompletedTask, _ => { }));
    }

    [Fact]
    public async Task PostedOperation_ReportsFailureAndReleasesGate()
    {
        Func<Task>? posted = null;
        Exception? reported = null;
        var dispatcher = new UiModalOperationDispatcher(
            callback => posted = callback,
            () => true);

        dispatcher.TrySchedule(
            () => throw new InvalidOperationException("dialog failed"),
            exception => reported = exception);

        await posted!();

        var failure = Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal("dialog failed", failure.Message);
        Assert.False(dispatcher.IsBusy);
    }

    [Fact]
    public void TrySchedule_WhenOwnerIsUnavailable_DoesNotPost()
    {
        var posted = false;
        var dispatcher = new UiModalOperationDispatcher(
            _ => posted = true,
            () => false);

        var result = dispatcher.TrySchedule(() => Task.CompletedTask, _ => { });

        Assert.Equal(UiModalScheduleResult.Unavailable, result);
        Assert.False(posted);
        Assert.False(dispatcher.IsBusy);
    }

    [Fact]
    public void TrySchedule_WhenUiPostFails_ReleasesGate()
    {
        var dispatcher = new UiModalOperationDispatcher(
            _ => throw new InvalidOperationException("form is closing"),
            () => true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.TrySchedule(() => Task.CompletedTask, _ => { }));

        Assert.Equal("form is closing", exception.Message);
        Assert.False(dispatcher.IsBusy);
    }

    [Fact]
    public async Task PostedOperation_WhenOwnerClosesBeforeDispatch_IsSkippedAndGateIsReleased()
    {
        Func<Task>? posted = null;
        var canRun = true;
        var operationRan = false;
        var dispatcher = new UiModalOperationDispatcher(
            callback => posted = callback,
            () => canRun);

        dispatcher.TrySchedule(
            () =>
            {
                operationRan = true;
                return Task.CompletedTask;
            },
            _ => { });
        canRun = false;

        await posted!();

        Assert.False(operationRan);
        Assert.False(dispatcher.IsBusy);
    }

    [Fact]
    public async Task DeferredMessage_UsesCloneAfterSourceDocumentIsDisposed()
    {
        Func<Task>? posted = null;
        string? selectedMode = null;
        var dispatcher = new UiModalOperationDispatcher(
            callback => posted = callback,
            () => true);

        using (var document = JsonDocument.Parse("{\"type\":\"video:open\",\"mode\":\"link\"}"))
        {
            var message = document.RootElement.Clone();
            dispatcher.TrySchedule(
                () =>
                {
                    selectedMode = message.GetProperty("mode").GetString();
                    return Task.CompletedTask;
                },
                _ => { });
        }

        await posted!();

        Assert.Equal("link", selectedMode);
    }
}
