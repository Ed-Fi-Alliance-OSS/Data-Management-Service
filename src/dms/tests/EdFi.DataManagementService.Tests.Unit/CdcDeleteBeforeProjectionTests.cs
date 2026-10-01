// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcDeleteBeforeProjectionAssertions(CdcProvider provider)
{
    private static readonly Guid Uuid = Guid.Parse("abcdef00-0000-0000-0000-000000000001");
    private CdcBinding _binding = null!;
    private MessageContractConsumer _consumer = null!;
    private int _partition;

    [SetUp]
    public void Setup()
    {
        _binding = new(
            1,
            "deployment",
            "",
            "1",
            "instance",
            1,
            provider,
            "source",
            "connector",
            "public",
            3,
            "kafka-murmur2-v1",
            1
        );
        _partition = MessageContractPartition.ForUuid(Uuid.ToString("D"), 3, _binding.PartitionerAlgorithm);
        _consumer = new(DateTimeOffset.UtcNow);
        _consumer.Assign(
            Enumerable.Range(0, 3).Select(p => new MessageContractPartitionBounds(p, 10, 10)).ToArray()
        );
    }

    [Test]
    public void It_consumes_a_never_published_keys_tombstone_and_retains_positions_after_resumption()
    {
        Apply([Tombstone(10), Tombstone(12)], 14, true);
        _consumer.Documents.Should().BeEmpty();
        _consumer.DurableNextOffsets.Values.Should().OnlyContain(offset => offset == 14);
        Apply([], 18, false);
        Apply([Tombstone(19)], 21, false);
        _consumer.Documents.Should().BeEmpty();
        _consumer.DurableNextOffsets.Values.Should().OnlyContain(offset => offset == 21);
    }

    [Test]
    public void It_rejects_a_completed_but_empty_scan_as_deletion_evidence()
    {
        Action act = () => Apply([], 14, true);
        act.Should().Throw<AssertionException>();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void It_rejects_even_an_upsert_followed_by_a_tombstone(bool beforeRelease)
    {
        var upsert = Tombstone(10) with { Value = new(false, "{\"contentVersion\":901}"u8.ToArray()) };
        Action act = () => Apply([upsert, Tombstone(11)], 14, beforeRelease);
        act.Should().Throw<AssertionException>();
    }

    [TestCase("key")]
    [TestCase("null-key")]
    [TestCase("partition")]
    [TestCase("topic")]
    [TestCase("nonempty-null")]
    public void It_rejects_a_tombstone_with_the_wrong_contract(string mutation)
    {
        var record = Tombstone(10);
        record = mutation switch
        {
            "key" => record with { Key = new(false, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"))) },
            "null-key" => record with { Key = new(true, []) },
            "partition" => record with { Partition = (_partition + 1) % 3 },
            "topic" => record with { Topic = "raw.Document" },
            "nonempty-null" => record with { Value = new(true, "null"u8.ToArray()) },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Action act = () => Apply([record], 14, true);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_json_null_instead_of_a_record_level_null()
    {
        var record = Tombstone(10) with { Value = new(false, "null"u8.ToArray()) };
        Action act = () => Apply([record], 14, true);
        act.Should().Throw<InvalidOperationException>();
    }

    private MessageContractKafkaRecord Tombstone(long offset) =>
        new(
            _binding.TopicName,
            _partition,
            offset,
            new(false, Encoding.UTF8.GetBytes(Uuid.ToString("D"))),
            new(true, []),
            [],
            1
        );

    private void Apply(IReadOnlyList<MessageContractKafkaRecord> records, long end, bool requireTombstone) =>
        CdcDeleteBeforeProjectionAssertions.ApplyScan(
            _binding,
            Uuid,
            new(
                records,
                Enumerable
                    .Range(0, 3)
                    .Select(p => new MessageContractKafkaBoundary(
                        _binding.TopicName,
                        p,
                        _consumer.DurableNextOffsets[p],
                        end
                    ))
                    .ToArray()
            ),
            _consumer,
            requireTombstone
        );
}

public partial class Given_CdcProjectionGate
{
    [Test]
    public async Task It_consumes_deletion_before_releasing_and_drains_before_the_resumed_scan()
    {
        TaskCompletionSource consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer.BeforeWriteAsync = async _ =>
        {
            writerEntered.TrySetResult();
            await writerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        };
        Task resume = CdcDeleteBeforeProjectionAssertions.ConsumeThenResumeAsync(
            _gate,
            token => consumed.Task.WaitAsync(TimeSpan.FromSeconds(2), token),
            CancellationToken.None
        );
        try
        {
            // An early Release fails this checkpoint even if its processor continuation has not run.
            (await _gate.WaitUntilPausedAsync())
                .Should()
                .Be(_observation);
            _writer.Calls.Should().BeEmpty();
            resume.IsCompleted.Should().BeFalse();
            consumed.SetResult();
            await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            resume.IsCompleted.Should().BeFalse("the resumed scan must wait for real processing to drain");
        }
        finally
        {
            consumed.TrySetResult();
            writerFinished.TrySetResult();
            await resume;
        }
        (await _processing).AcknowledgedOrRemovedDurableWork.Should().BeTrue();
        _writer.Calls.Should().HaveCount(2);
    }

    [Test]
    public async Task It_rejects_an_expired_hold_instead_of_counting_idle_as_successful_processing()
    {
        Func<Task> processing = () => _processing;
        await processing.Should().ThrowAsync<TimeoutException>();
        Func<Task> resume = () => _gate.ResumeHeldProcessingAsync(CancellationToken.None);
        await resume.Should().ThrowAsync<InvalidOperationException>();
        _writer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_provider_backoff_during_the_resumed_call()
    {
        _writer.BeforeWriteAsync = _ => Task.FromException(new InvalidOperationException("Provider failure"));
        Func<Task> resume = () => _gate.ResumeHeldProcessingAsync(CancellationToken.None);
        await resume.Should().ThrowAsync<InvalidOperationException>();
        (await _processing).Outcome.Should().Be(DocumentCacheProjectionItemProcessOutcome.TargetBackoff);
    }

    [Test]
    public async Task It_rejects_cancellation_during_the_resumed_call()
    {
        _writer.BeforeWriteAsync = _ => Task.FromException(new OperationCanceledException());
        Func<Task> resume = () => _gate.ResumeHeldProcessingAsync(CancellationToken.None);
        await resume.Should().ThrowAsync<InvalidOperationException>();
        Func<Task> processing = () => _processing;
        await processing.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_does_not_resume_projection_when_deletion_evidence_fails()
    {
        Func<Task> act = () =>
            CdcDeleteBeforeProjectionAssertions.ConsumeThenResumeAsync(
                _gate,
                _ => Task.FromException(new AssertionException("No tombstone")),
                CancellationToken.None
            );
        await act.Should().ThrowAsync<AssertionException>();
        (await _gate.WaitUntilPausedAsync()).Should().Be(_observation);
        _writer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_resume_projection_when_cancelled_after_consumption()
    {
        using var cancellation = new CancellationTokenSource();
        Func<Task> act = () =>
            CdcDeleteBeforeProjectionAssertions.ConsumeThenResumeAsync(
                _gate,
                _ => cancellation.CancelAsync(),
                cancellation.Token
            );
        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _gate.WaitUntilPausedAsync()).Should().Be(_observation);
        _writer.Calls.Should().BeEmpty();
    }
}
