// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>One fixed sequence; bounded execution and independent finalization reserves.
/// Scenarios must honor cancellation. No selection, retry, reset, or resume path.</summary>
internal sealed class CdcScenarioRunner
{
    private readonly TimeSpan _executionTimeout;
    private readonly TimeSpan _finalizationTimeout;

    public CdcScenarioRunner()
        : this(TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(30)) { }

    internal CdcScenarioRunner(TimeSpan executionTimeout, TimeSpan finalizationTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(executionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(finalizationTimeout, TimeSpan.Zero);
        _executionTimeout = executionTimeout;
        _finalizationTimeout = finalizationTimeout;
    }

    public async Task RunAsync(
        string path,
        string invocationId,
        ICdcApiScenarios scenarios,
        CancellationToken token
    )
    {
        var writer = new CdcScenarioReportWriter(path, invocationId);
        var report = new CdcScenarioReport(invocationId);
        // Initialize even if execution was already cancelled. Never attach without a fresh report.
        using (var initialization = new CancellationTokenSource(_finalizationTimeout))
        {
            try
            {
                await writer.WriteAsync(report, initialization.Token);
            }
            catch
            {
                throw new InvalidOperationException("CDC_API_REPORT_INITIALIZATION_FAILED");
            }
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        execution.CancelAfter(_executionTimeout);
        var ct = execution.Token;
        CdcScenarioResult current = report.Attachment;
        string primaryFailure = "";
        bool persistenceFailed = false;
        Task pending = Task.CompletedTask;
        try
        {
            current.Outcome = CdcScenarioOutcome.Running;
            await writer.WriteAsync(report, ct);
            ct.ThrowIfCancellationRequested();
            var attachment = scenarios.AttachAsync(ct);
            pending = attachment;
            report.Identity = await attachment.WaitAsync(ct);
            current.Outcome = CdcScenarioOutcome.Passed;
            await writer.WriteAsync(report, ct);

            Func<CancellationToken, Task>[] phases =
            [
                scenarios.StudentCrudAsync,
                scenarios.DescriptorCrudAsync,
                scenarios.OverlapAsync,
                scenarios.DeleteBeforeProjectionAsync,
                scenarios.OnlineRebuildAsync,
                scenarios.ExecutorRestartAsync,
                scenarios.UnavailableEvidenceAsync,
                scenarios.TerminalLossAsync,
            ];
            for (int index = 0; index < phases.Length; index++)
            {
                current = report.Scenarios[index];
                current.Outcome = CdcScenarioOutcome.Running;
                await writer.WriteAsync(report, ct);
                ct.ThrowIfCancellationRequested();
                pending = phases[index](ct);
                await pending.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                current.Outcome = CdcScenarioOutcome.Passed;
                await writer.WriteAsync(report, ct);
            }
        }
        catch (Exception exception)
        {
            current.Outcome = CdcScenarioOutcome.Failed;
            current.Failure = Classify(exception, token);
            primaryFailure = $"CDC_API_{current.Id}_{current.Failure}";
        }
        finally
        {
            report.Disposal.Outcome = CdcScenarioOutcome.Running;
            await PersistFinalizationAsync();
            // Cancellation of work must not cancel report persistence or local resource disposal.
            CdcScenarioFailure shutdownFailure = CdcScenarioFailure.None;
            try
            {
                await execution.CancelAsync().WaitAsync(_finalizationTimeout);
            }
            catch (Exception exception)
            {
                shutdownFailure = Classify(exception, CancellationToken.None);
            }
            // Let a cancelled operation unwind before disposing its resources. Never mark disposal
            // passed if an operation outlives this independent bounded drain.
            try
            {
                await pending.WaitAsync(_finalizationTimeout);
            }
            catch (TimeoutException) when (!pending.IsCompleted)
            {
                shutdownFailure = CdcScenarioFailure.TimedOut;
            }
            catch
            { /* The execution failure is already recorded above. */
            }

            try
            {
                await scenarios.DisposeAsync().AsTask().WaitAsync(_finalizationTimeout);
                report.Disposal.Outcome =
                    shutdownFailure == CdcScenarioFailure.None
                        ? CdcScenarioOutcome.Passed
                        : CdcScenarioOutcome.Failed;
                report.Disposal.Failure = shutdownFailure;
            }
            catch (Exception exception)
            {
                report.Disposal.Outcome = CdcScenarioOutcome.Failed;
                report.Disposal.Failure = Classify(exception, CancellationToken.None);
            }
            await PersistFinalizationAsync();
        }

        // Preserve the original failing stage/category over cleanup failures without leaking raw
        // assertion bodies, transport errors, credentials, or exception inner chains into NUnit/TRX.
        if (primaryFailure.Length > 0)
        {
            throw new InvalidOperationException(primaryFailure);
        }
        if (report.Disposal.Outcome != CdcScenarioOutcome.Passed)
        {
            throw new InvalidOperationException($"CDC_API_DISPOSAL_{report.Disposal.Failure}");
        }
        if (persistenceFailed)
        {
            throw new InvalidOperationException("CDC_API_REPORT_PERSISTENCE_FAILED");
        }

        async Task PersistFinalizationAsync()
        {
            using var reserve = new CancellationTokenSource(_finalizationTimeout);
            try
            {
                await writer.WriteAsync(report, reserve.Token);
            }
            catch
            {
                persistenceFailed = true;
            }
        }
    }

    private static CdcScenarioFailure Classify(Exception exception, CancellationToken caller) =>
        exception switch
        {
            NotImplementedException => CdcScenarioFailure.Unimplemented,
            OperationCanceledException => caller.IsCancellationRequested
                ? CdcScenarioFailure.Cancelled
                : CdcScenarioFailure.TimedOut,
            TimeoutException => CdcScenarioFailure.TimedOut,
            _ => CdcScenarioFailure.Error,
        };
}
