// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Unit;

public partial class Given_CdcProjectionGate
{
    private async Task<CdcProjectionPauseObservation> ArrangeOverlapAsync()
    {
        _gate.Release();
        await _processing;
        _writer.Calls.Clear();
        _materializer.Calls.Clear();
        _gate.PauseForOverlap();
        _processing = _processor.ProcessItemAsync(Request(_context, 101), _cancellation.Token);
        return await _gate.WaitUntilMaterializedAsync();
    }

    [Test]
    public async Task It_retains_the_actual_materialized_candidate_until_the_newer_commit_and_real_writer_completion()
    {
        var held = await ArrangeOverlapAsync();
        held.Should().Be(new CdcProjectionPauseObservation(101, 11));
        var candidate = _materializer.Candidate;
        _materializer.Calls.Should().ContainSingle();
        _writer.Calls.Should().ContainSingle().Which.Candidate.Should().BeNull();
        _processing.IsCompleted.Should().BeFalse();
        Task<CdcProjectionWriteObservation> written = _gate.WaitUntilWrittenAsync();
        written.IsCompleted.Should().BeFalse();

        // Model a committed N+1 after N's materialization; the real processor must submit the
        // original candidate and selected version to the writer without re-materializing.
        _writer.SuppressCandidate = true;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer.BeforeWriteAsync = async request =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(2), request.CancellationToken);
        };
        _gate.ReleaseCandidate();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            written.IsCompleted.Should().BeFalse("completion must follow the provider write's return");
            _writer.Calls[1].Candidate.Should().BeSameAs(candidate);
            _writer.Calls[1].SelectedRequiredContentVersion.Should().Be(11);
            _materializer.Calls.Should().ContainSingle();
        }
        finally
        {
            finish.TrySetResult();
        }
        (await written)
            .Should()
            .Be(
                new CdcProjectionWriteObservation(
                    101,
                    11,
                    DocumentCacheWriterOutcome.StaleCandidateSuppressed
                )
            );
        (await _processing).AcknowledgedOrRemovedDurableWork.Should().BeFalse();

        _processing = _processor.ProcessItemAsync(
            new(
                _context,
                new(101, 12, ObservedAt, ObservedAt),
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );
        (await _gate.WaitUntilPausedAsync()).Should().Be(new CdcProjectionPauseObservation(101, 12));
        _processing.IsCompleted.Should().BeFalse();
        _writer
            .Calls.Should()
            .HaveCount(2, "intermediate observations precede even the next fast-path write");
        _gate.Release();
        await _processing;
        _writer.Calls.Should().HaveCount(4);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_leaves_other_documents_and_targets_running_during_overlap(bool otherTarget)
    {
        await ArrangeOverlapAsync();
        await using var context = RuntimeContext(
            _materializer,
            _provider.GetRequiredService<IDocumentCacheWriter>(),
            DocumentCacheTargetKey.Create(otherTarget ? "Tenant-B" : "Tenant-A", 1)
        );
        (await _processor.ProcessItemAsync(Request(context, otherTarget ? 101 : 102)))
            .AcknowledgedOrRemovedDurableWork.Should()
            .BeTrue();
        _writer.Calls.Should().HaveCount(3);
        _processing.IsCompleted.Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_cancels_the_materialized_hold_before_delegating_the_candidate(
        bool targetCancellation
    )
    {
        await ArrangeOverlapAsync();
        if (targetCancellation)
        {
            _context.Cancel();
        }
        else
        {
            await _cancellation.CancelAsync();
        }
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _writer.Calls.Should().ContainSingle().Which.Candidate.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_disposes_and_drains_either_overlap_hold(bool nextAttempt)
    {
        await ArrangeOverlapAsync();
        if (nextAttempt)
        {
            _gate.ReleaseCandidate();
            await _processing;
            _processing = _processor.ProcessItemAsync(Request(_context, 101));
            await _gate.WaitUntilPausedAsync();
        }
        int calls = _writer.Calls.Count;
        await _gate.DisposeAsync();
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _writer.Calls.Should().HaveCount(calls);
    }

    [Test]
    public async Task It_times_out_the_materialized_hold_without_publishing_or_reporting_completion()
    {
        await ArrangeOverlapAsync();
        (await _processing).Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.TargetBackoff);
        _writer.Calls.Should().ContainSingle().Which.Candidate.Should().BeNull();
        using var cancellation = new CancellationTokenSource();
        var written = _gate.WaitUntilWrittenAsync(cancellation.Token);
        written.IsCompleted.Should().BeFalse();
        await cancellation.CancelAsync();
        Func<Task> act = () => written;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_times_out_the_next_attempt_before_its_writer_fast_path()
    {
        await ArrangeOverlapAsync();
        _gate.ReleaseCandidate();
        await _processing;
        _processing = _processor.ProcessItemAsync(Request(_context, 101));
        await _gate.WaitUntilPausedAsync();
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<TimeoutException>();
        _writer.Calls.Should().HaveCount(2);
    }

    [Test]
    public async Task It_disposal_cancels_pending_overlap_observations()
    {
        _gate.Release();
        await _processing;
        _gate.PauseForOverlap();
        var materialized = _gate.WaitUntilMaterializedAsync();
        var written = _gate.WaitUntilWrittenAsync();
        var next = _gate.WaitUntilPausedAsync();
        await _gate.DisposeAsync();
        foreach (var pending in new Task[] { materialized, written, next })
        {
            Func<Task> act = () => pending;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
