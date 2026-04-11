using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies the TCS phase-coordination pattern used in the restore command.
/// </summary>
public class RestoreTcsCoordinationTests
{
    [Test]
    public async Task ConfirmRehydration_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        var questionTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipelineTask = Task.Run(async () =>
        {
            questionTcs.TrySetResult(42);
            return await answerTcs.Task;
        });

        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);
        firstSignal.ShouldBe(questionTcs.Task, "question should arrive before pipeline completes");

        var questionValue = await questionTcs.Task;
        questionValue.ShouldBe(42);

        answerTcs.TrySetResult(true);

        var answer = await pipelineTask;
        answer.ShouldBeTrue("pipeline should receive the answer we provided");
    }

    [Test]
    public async Task ConfirmCleanup_TcsPhaseTransition_PipelineUnblocksAfterAnswer()
    {
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipelineTask = Task.Run(async () =>
        {
            cleanupQuestionTcs.TrySetResult((3, 1024L));
            return await cleanupAnswerTcs.Task;
        });

        var (count, bytes) = await cleanupQuestionTcs.Task;
        count.ShouldBe(3);
        bytes.ShouldBe(1024L);

        cleanupAnswerTcs.TrySetResult(false);

        var result = await pipelineTask;
        result.ShouldBeFalse();
    }

    [Test]
    public async Task NoRehydrationNeeded_PipelineCompletesFirst_QuestionTcsNeverSet()
    {
        var questionTcs  = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipelineTask = Task.FromResult(true);

        var firstSignal = await Task.WhenAny(pipelineTask, questionTcs.Task);
        firstSignal.ShouldBe((Task)pipelineTask, "pipeline should complete first");

        questionTcs.Task.IsCompleted.ShouldBeFalse("question TCS should not be set");
    }

    [Test]
    public async Task NoRehydrationNeeded_CleanupFires_PipelineUnblocksWithoutDeadlock()
    {
        // Simulates the deadlock scenario: no rehydration is needed (questionTcs never fires),
        // but the pipeline invokes ConfirmCleanup, blocking on cleanupAnswerTcs.
        // The CLI must detect cleanupQuestionTcs and handle it before awaiting pipelineTask.
        var questionTcs        = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pipeline: does some work, then invokes ConfirmCleanup (blocks on cleanupAnswerTcs)
        var pipelineTask = Task.Run(async () =>
        {
            await Task.Delay(50); // simulate pipeline work
            cleanupQuestionTcs.TrySetResult((5, 2048L));
            return await cleanupAnswerTcs.Task;
        });

        // Simulate the CLI Phase 1 loop: poll for questionTcs OR cleanupQuestionTcs (THE FIX)
        while (!pipelineTask.IsCompleted && !questionTcs.Task.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
        {
            await Task.WhenAny(pipelineTask, questionTcs.Task, cleanupQuestionTcs.Task, Task.Delay(20));
        }

        // No rehydration question — verify
        questionTcs.Task.IsCompleted.ShouldBeFalse("rehydration question should not fire");

        // After loop exits, check if cleanup question fired
        if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
        {
            var (count, bytes) = await cleanupQuestionTcs.Task;
            count.ShouldBe(5);
            bytes.ShouldBe(2048L);
            cleanupAnswerTcs.TrySetResult(true);
        }

        // Now pipeline should complete without deadlock
        var timeoutTask = Task.Delay(5000);
        var finishedFirst = await Task.WhenAny(pipelineTask, timeoutTask);
        finishedFirst.ShouldBe(pipelineTask, "pipeline should complete, not timeout — deadlock detected if this fails");

        var result = await pipelineTask;
        result.ShouldBeTrue("pipeline should receive the cleanup answer");
    }

    [Test]
    public async Task PostRehydration_CleanupFiresDuringDownload_PipelineUnblocksWithoutDeadlock()
    {
        // Simulates the Phase 3 deadlock: rehydration was needed and answered,
        // but during the download phase the pipeline invokes ConfirmCleanup.
        // The Phase 3 live loop must also monitor cleanupQuestionTcs to exit.
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pipeline: does download work, then invokes ConfirmCleanup
        var pipelineTask = Task.Run(async () =>
        {
            await Task.Delay(50); // simulate download
            cleanupQuestionTcs.TrySetResult((3, 1024L));
            return await cleanupAnswerTcs.Task;
        });

        // Simulate Phase 3 live loop (the FIX: also monitor cleanupQuestionTcs)
        while (!pipelineTask.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
        {
            await Task.WhenAny(pipelineTask, cleanupQuestionTcs.Task, Task.Delay(20));
        }

        // After loop: handle cleanup
        if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
        {
            var (count, bytes) = await cleanupQuestionTcs.Task;
            count.ShouldBe(3);
            bytes.ShouldBe(1024L);
            cleanupAnswerTcs.TrySetResult(false);
        }

        var timeoutTask = Task.Delay(5000);
        var finishedFirst = await Task.WhenAny(pipelineTask, timeoutTask);
        finishedFirst.ShouldBe(pipelineTask, "pipeline should complete, not timeout — deadlock detected if this fails");

        var result = await pipelineTask;
        result.ShouldBeFalse();
    }

    [Test]
    public async Task RehydrationFirst_ThenCleanup_PipelineUnblocksWithoutDeadlock()
    {
        // Full flow: rehydration fires first, CLI answers it, pipeline continues
        // into download phase, then cleanup fires and CLI must detect and answer it.
        var questionTcs        = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerTcs          = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pipeline: rehydration question → wait for answer → download work → cleanup question → wait for answer
        var pipelineTask = Task.Run(async () =>
        {
            // Phase 2: invoke ConfirmRehydration
            questionTcs.TrySetResult(42);
            var rehydrate = await answerTcs.Task;
            rehydrate.ShouldBeTrue("CLI should confirm rehydration");

            // Phase 3: download work, then invoke ConfirmCleanup
            await Task.Delay(50); // simulate download
            cleanupQuestionTcs.TrySetResult((7, 4096L));
            return await cleanupAnswerTcs.Task;
        });

        // ── Phase 1 loop: wait for rehydration question or pipeline completion ──
        while (!pipelineTask.IsCompleted && !questionTcs.Task.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
        {
            await Task.WhenAny(pipelineTask, questionTcs.Task, cleanupQuestionTcs.Task, Task.Delay(20));
        }

        // Rehydration question should have fired
        questionTcs.Task.IsCompleted.ShouldBeTrue("rehydration question should fire");
        var rehydrationValue = await questionTcs.Task;
        rehydrationValue.ShouldBe(42);

        // CLI answers: yes, rehydrate
        answerTcs.TrySetResult(true);

        // ── Phase 3 loop: download live display, also monitoring cleanupQuestionTcs ──
        while (!pipelineTask.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
        {
            await Task.WhenAny(pipelineTask, cleanupQuestionTcs.Task, Task.Delay(20));
        }

        // After Phase 3 loop: handle cleanup if pipeline is still running
        if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
        {
            var (count, bytes) = await cleanupQuestionTcs.Task;
            count.ShouldBe(7);
            bytes.ShouldBe(4096L);
            cleanupAnswerTcs.TrySetResult(true);
        }

        // Pipeline should complete without deadlock
        var timeoutTask = Task.Delay(5000);
        var finishedFirst = await Task.WhenAny(pipelineTask, timeoutTask);
        finishedFirst.ShouldBe(pipelineTask, "pipeline should complete, not timeout — deadlock detected if this fails");

        var result = await pipelineTask;
        result.ShouldBeTrue("pipeline should receive the cleanup answer");
    }
}
