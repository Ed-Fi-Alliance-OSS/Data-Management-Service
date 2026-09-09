// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;
using Provider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Provider.Postgresql)]
[TestFixture(Provider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
public class Given_CdcRecordSizeAcknowledgement(Provider provider)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private string _root = null!;
    private LocalCdcWorkflowJournalStore.Session _session = null!;
    private CdcRecordSizeIncreaseScope _scope = null!;
    private Guid _workflow;
    private CdcRecordSizeAcknowledgementInvocation _invocation = null!;
    private CdcRecordSizeIncreaseConfirmation _confirmation = null!;
    private Action<CdcWorkflowWriteBoundary> _onWrite = _ => { };
    private int _effects;
    private CdcTargetIdentity Target => _scope.BindingIdentity.ToTargetIdentity();
    private string JournalPath =>
        Path.Combine(
            _root,
            "workflows",
            Target.DeploymentKey,
            Target.InstanceKey,
            $"{Target.Generation}.json"
        );

    [SetUp]
    public async Task Setup()
    {
        _effects = 0;
        _onWrite = _ => { };
        _root = Path.Combine(Path.GetTempPath(), $"cdc-ack-{Guid.NewGuid():N}");
        _workflow = Guid.NewGuid();
        var binding = CdcConnectorTemplateTestData.BuildRequest(provider).Binding;
        _scope = new(Guid.NewGuid(), binding.ToCompleteBindingIdentity(), 1000, 2000);
        _session = await AcquireAsync();
        await _session.CreateAsync(
            _workflow,
            Target,
            CancellationToken.None,
            CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await SeedAsync(
            CdcWorkflowEffect.CreateDatabase,
            new CdcWorkflowCompletion.Database(new(Guid.NewGuid(), CdcDatabaseCreationOutcome.Created))
        );
        await SeedAsync(
            CdcWorkflowEffect.AssociateSource,
            new CdcWorkflowCompletion.Source(_scope.BindingIdentity.PhysicalSourceFingerprint)
        );
        await SeedAsync(CdcWorkflowEffect.ReserveBinding, new CdcWorkflowCompletion.Reconciled());
        Renew();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _session.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private Task<LocalCdcWorkflowJournalStore.Session> AcquireAsync() =>
        new LocalCdcWorkflowJournalStore(_root, _clock, boundary => _onWrite(boundary)).AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );

    private async Task SeedAsync(CdcWorkflowEffect effect, CdcWorkflowCompletion completion)
    {
        var id = Guid.NewGuid();
        await _session.RecordIntentAsync(Target, _workflow, id, effect, [], CancellationToken.None);
        await _session.ReconcileCompletionAsync(
            Target,
            _workflow,
            id,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(completion)
                ),
            CancellationToken.None
        );
    }

    private void Renew()
    {
        _clock.Now = _clock.Now.AddSeconds(1);
        _invocation = _session.BeginRecordSizeAcknowledgement(_workflow, _scope);
        _confirmation = new(
            _scope,
            new(
                _invocation.InvocationId,
                "operator",
                _clock.Now,
                false,
                [new("consumer-a", "revision-1", "owner-a", "capacity-2000-v1")]
            ),
            true
        );
    }

    private Task<CdcWorkflowJournal> ReadAsync() => _session.ReadAsync(Target, CancellationToken.None);

    private Task<int> RunAsync(CancellationToken token = default) =>
        _invocation.ConfirmAndRunAsync(_confirmation, _ => Task.FromResult(++_effects), token);

    private async Task RejectAsync()
    {
        string before = await File.ReadAllTextAsync(JournalPath);
        Func<Task> act = () => RunAsync();
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        _effects.Should().Be(0);
        (await File.ReadAllTextAsync(JournalPath)).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_persists_full_scope_and_explicit_confirmation_before_advancing(bool noConsumers)
    {
        if (noConsumers)
        {
            _confirmation = _confirmation with
            {
                Acknowledgement = _confirmation.Acknowledgement with { NoConsumers = true, Consumers = [] },
            };
        }
        int result = await _invocation.ConfirmAndRunAsync(
            _confirmation,
            async token =>
            {
                var journal = await _session.ReadAsync(Target, token);
                journal.HasPendingRecordSizeIncrease.Should().BeTrue();
                var operation = journal.Operations.Last();
                operation.OperationId.Should().Be(_scope.OperationId);
                operation.Completions.Should().BeEmpty();
                operation
                    .RecordSizeIncrease.Single()
                    .Should()
                    .BeEquivalentTo(
                        new CdcRecordSizeIncreaseJournal(
                            _scope.BindingIdentity,
                            1000,
                            2000,
                            [_confirmation.Acknowledgement]
                        )
                    );
                string json = await File.ReadAllTextAsync(JournalPath, token);
                json.Should()
                    .Contain(_scope.BindingIdentity.PhysicalSourceFingerprint)
                    .And.Contain(_scope.BindingIdentity.TopicName)
                    .And.NotContain("ready");
                return 42;
            },
            CancellationToken.None
        );
        result.Should().Be(42);
    }

    [Test]
    public async Task It_requires_renewed_confirmation_after_reopening_the_state_root()
    {
        await RunAsync();
        var previous = _confirmation;
        await _session.DisposeAsync();
        _session = await AcquireAsync();
        Renew();
        _confirmation = previous;
        _effects = 0;
        await RejectAsync();
        Renew();
        await RunAsync();
        var increase = (await ReadAsync()).Operations.Last().RecordSizeIncrease.Single();
        increase.Acknowledgements.Should().HaveCount(2);
        increase.Acknowledgements[0].Should().BeEquivalentTo(previous.Acknowledgement);
        increase.Acknowledgements[1].Should().BeEquivalentTo(_confirmation.Acknowledgement);
        increase.RequestedMaxRecordBytes.Should().Be(2000);
    }

    [Test]
    public async Task It_does_not_enter_twice_with_the_same_in_memory_invocation()
    {
        await RunAsync();
        _effects = 0;
        await RejectAsync();
    }

    [TestCase("missing")]
    [TestCase("missing-ack")]
    [TestCase("missing-scope")]
    [TestCase("unconfirmed")]
    [TestCase("omitted-inventory")]
    [TestCase("empty-inventory")]
    [TestCase("no-consumers-with-inventory")]
    [TestCase("duplicate-deployment")]
    [TestCase("null-consumer")]
    [TestCase("deployment")]
    [TestCase("revision")]
    [TestCase("owner")]
    [TestCase("evidence")]
    [TestCase("operator")]
    [TestCase("invocation")]
    [TestCase("empty-invocation")]
    [TestCase("future")]
    [TestCase("before-invocation")]
    [TestCase("no-time")]
    [TestCase("non-utc")]
    public async Task It_rejects_missing_incomplete_or_stale_confirmation(string mutation)
    {
        var ack = _confirmation.Acknowledgement;
        var consumer = ack.Consumers[0];
        ack = mutation switch
        {
            "omitted-inventory" => ack with { Consumers = default, NoConsumers = true },
            "empty-inventory" => ack with { Consumers = [] },
            "no-consumers-with-inventory" => ack with { NoConsumers = true },
            "duplicate-deployment" => ack with
            {
                Consumers = [consumer, consumer with { Revision = "revision-2" }],
            },
            "null-consumer" => ack with { Consumers = [null!] },
            "deployment" => ack with { Consumers = [consumer with { DeploymentIdentity = "" }] },
            "revision" => ack with { Consumers = [consumer with { Revision = "" }] },
            "owner" => ack with { Consumers = [consumer with { ConfirmingOwner = "" }] },
            "evidence" => ack with { Consumers = [consumer with { EvidenceReference = "" }] },
            "operator" => ack with { OperatorIdentity = "" },
            "invocation" => ack with { InvocationId = Guid.NewGuid() },
            "empty-invocation" => ack with { InvocationId = Guid.Empty },
            "future" => ack with { ConfirmedAt = _clock.Now.AddTicks(1) },
            "before-invocation" => ack with { ConfirmedAt = _invocation.StartedAt.AddTicks(-1) },
            "no-time" => ack with { ConfirmedAt = default },
            "non-utc" => ack with { ConfirmedAt = _clock.Now.ToOffset(TimeSpan.FromHours(1)) },
            _ => ack,
        };
        _confirmation = _confirmation with { Acknowledgement = ack };
        _confirmation = mutation switch
        {
            "missing" => null!,
            "missing-ack" => _confirmation with { Acknowledgement = null! },
            "missing-scope" => _confirmation with { Scope = null! },
            "unconfirmed" => _confirmation with { CompleteInventoryAndCapacityConfirmed = false },
            _ => _confirmation,
        };
        await RejectAsync();
    }

    [TestCase("operation")]
    [TestCase("deployment")]
    [TestCase("tenant")]
    [TestCase("store")]
    [TestCase("instance")]
    [TestCase("generation")]
    [TestCase("provider")]
    [TestCase("source")]
    [TestCase("connector")]
    [TestCase("topic")]
    [TestCase("previous-ceiling")]
    [TestCase("requested-ceiling")]
    public async Task It_rejects_confirmation_for_any_other_operation_scope(string mutation)
    {
        var identity = _scope.BindingIdentity;
        identity = mutation switch
        {
            "deployment" => identity with { DeploymentKey = "other" },
            "tenant" => identity with { TenantKey = "other" },
            "store" => identity with { DataStoreId = "999" },
            "instance" => identity with { InstanceKey = "other" },
            "generation" => identity with { Generation = identity.Generation + 1 },
            "provider" => identity with
            {
                Provider =
                    identity.Provider == CdcProvider.Postgresql
                        ? CdcProvider.SqlServer
                        : CdcProvider.Postgresql,
            },
            "source" => identity with { PhysicalSourceFingerprint = "sha256:" + new string('b', 64) },
            "connector" => identity with { ConnectorName = "other" },
            "topic" => identity with { TopicName = "other" },
            _ => identity,
        };
        var scope = _scope with { BindingIdentity = identity };
        scope = mutation switch
        {
            "operation" => scope with { OperationId = Guid.NewGuid() },
            "previous-ceiling" => scope with { PreviousMaxRecordBytes = 999 },
            "requested-ceiling" => scope with { RequestedMaxRecordBytes = 2001 },
            _ => scope,
        };
        _confirmation = _confirmation with { Scope = scope };
        await RejectAsync();
    }

    [TestCase("https://user:secret-sentinel@evidence.example/report")]
    [TestCase("https://evidence.example/report?token=secret-sentinel")]
    [TestCase("https://evidence.example/secret-sentinel")]
    [TestCase("password=secret-sentinel")]
    [TestCase("Bearer secret-sentinel")]
    [TestCase("${file:/secret-sentinel:password}")]
    [TestCase("../secret-sentinel")]
    public async Task It_rejects_credential_bearing_references_without_echoing_them(string reference)
    {
        _confirmation = _confirmation with
        {
            Acknowledgement = _confirmation.Acknowledgement with
            {
                Consumers =
                [
                    _confirmation.Acknowledgement.Consumers[0] with
                    {
                        EvidenceReference = reference,
                    },
                ],
            },
        };
        Func<Task> act = () => RunAsync();
        var exception = (await act.Should().ThrowAsync<CdcWorkflowStateException>()).Which;
        exception.ToString().Should().NotContain(reference).And.NotContain("secret-sentinel");
        JsonSerializer.Serialize(_confirmation).Should().NotContain("secret-sentinel");
        _confirmation.ToString().Should().NotContain("secret-sentinel");
        (await File.ReadAllTextAsync(JournalPath)).Should().NotContain("secret-sentinel");
        _effects.Should().Be(0);
    }

    [TestCase("revision", false)]
    [TestCase("deployment", false)]
    [TestCase("owner", false)]
    [TestCase("revision", true)]
    [TestCase("deployment", true)]
    [TestCase("owner", true)]
    public async Task It_requires_updated_evidence_for_changed_consumer_deployments(
        string change,
        bool updated
    )
    {
        await RunAsync();
        Renew();
        var consumer = _confirmation.Acknowledgement.Consumers[0];
        consumer = change switch
        {
            "revision" => consumer with { Revision = "revision-2" },
            "deployment" => consumer with { DeploymentIdentity = "consumer-b" },
            _ => consumer with { ConfirmingOwner = "owner-b" },
        };
        if (updated)
        {
            consumer = consumer with { EvidenceReference = "capacity-2000-v2" };
        }
        _confirmation = _confirmation with
        {
            Acknowledgement = _confirmation.Acknowledgement with { Consumers = [consumer] },
        };
        _effects = 0;
        if (updated)
        {
            await RunAsync();
            (await ReadAsync())
                .Operations.Last()
                .RecordSizeIncrease.Single()
                .Acknowledgements.Should()
                .HaveCount(2);
        }
        else
        {
            await RejectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_requires_new_capacity_evidence_for_a_later_higher_ceiling(bool updated)
    {
        await RunAsync();
        await _session.ReconcileCompletionAsync(
            Target,
            _workflow,
            _scope.OperationId,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Reconciled()
                    )
                ),
            CancellationToken.None
        );
        _scope = _scope with
        {
            OperationId = Guid.NewGuid(),
            PreviousMaxRecordBytes = 2000,
            RequestedMaxRecordBytes = 3000,
        };
        Renew();
        if (updated)
        {
            _confirmation = _confirmation with
            {
                Acknowledgement = _confirmation.Acknowledgement with
                {
                    Consumers =
                    [
                        _confirmation.Acknowledgement.Consumers[0] with
                        {
                            EvidenceReference = "capacity-3000-v1",
                        },
                    ],
                },
            };
        }
        _effects = 0;
        if (updated)
        {
            await RunAsync();
        }
        else
        {
            await RejectAsync();
        }
    }

    [TestCase("other-operation")]
    [TestCase("previous-ceiling")]
    [TestCase("requested-ceiling")]
    [TestCase("other-source")]
    public async Task It_cannot_change_a_pending_rollout_by_confirming_a_new_scope(string mutation)
    {
        await RunAsync();
        _scope = mutation switch
        {
            "other-operation" => _scope with { OperationId = Guid.NewGuid() },
            "previous-ceiling" => _scope with { PreviousMaxRecordBytes = 999 },
            "requested-ceiling" => _scope with { RequestedMaxRecordBytes = 3000 },
            _ => _scope with
            {
                BindingIdentity = _scope.BindingIdentity with
                {
                    PhysicalSourceFingerprint = "sha256:" + new string('b', 64),
                },
            },
        };
        Renew();
        _effects = 0;
        await RejectAsync();
    }

    [Test]
    public async Task It_leaves_partial_rollout_pending_after_an_effect_fails()
    {
        Func<Task> act = () =>
            _invocation.ConfirmAndRunAsync<int>(
                _confirmation,
                _ => throw new InvalidOperationException("effect failed"),
                CancellationToken.None
            );
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeTrue();
        _effects = 0;
        await RejectAsync();
    }

    [Test]
    public async Task It_preserves_cancellation_and_does_not_write_or_advance_when_already_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => RunAsync(cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeFalse();
        _effects.Should().Be(0);
    }

    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite, false)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush, false)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement, false)]
    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite, true)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush, true)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement, true)]
    public async Task It_never_advances_after_a_failed_acknowledgement_write(int boundaryValue, bool resume)
    {
        var boundary = (CdcWorkflowWriteBoundary)boundaryValue;
        if (resume)
        {
            await RunAsync();
            Renew();
        }
        _effects = 0;
        var interrupted = _confirmation;
        _onWrite = current =>
        {
            if (current == boundary)
            {
                throw new IOException("injected write failure");
            }
        };
        Func<Task> act = () => RunAsync();
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        _effects.Should().Be(0);
        _onWrite = _ => { };
        await _session.DisposeAsync();
        _session = await AcquireAsync();
        var journal = await ReadAsync();
        var increases = journal
            .Operations.Where(o => o.Effect == CdcWorkflowEffect.IncreaseRecordSize)
            .ToArray();
        int count = (resume ? 1 : 0) + (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement ? 1 : 0);
        increases
            .SelectMany(o => o.RecordSizeIncrease)
            .SelectMany(i => i.Acknowledgements)
            .Should()
            .HaveCount(count);
        Renew();
        _confirmation = interrupted;
        await RejectAsync();
        Renew();
        await RunAsync();
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_stale_evidence_inserted_directly_into_persisted_history()
    {
        await RunAsync();
        Renew();
        await RunAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        json["operations"]!.AsArray()[^1]!["recordSizeIncrease"]![0]!["acknowledgements"]![1]!["consumers"]![
            0
        ]!["revision"] = "revision-2";
        await File.WriteAllTextAsync(JournalPath, json.ToJsonString());
        Func<Task> act = () => ReadAsync();
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_does_not_reopen_a_completed_increase()
    {
        await RunAsync();
        await _session.ReconcileCompletionAsync(
            Target,
            _workflow,
            _scope.OperationId,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Reconciled()
                    )
                ),
            CancellationToken.None
        );
        Renew();
        _effects = 0;
        await RejectAsync();
    }

    [Test]
    public async Task It_preserves_committed_confirmation_but_does_not_advance_after_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _onWrite = boundary =>
        {
            if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            {
                cancellation.Cancel();
            }
        };
        Func<Task> act = () => RunAsync(cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeTrue();
        _effects.Should().Be(0);
    }

    [Test]
    public async Task It_retains_the_controller_lock_through_rollout_and_readiness_work()
    {
        await _invocation.ConfirmAndRunAsync(
            _confirmation,
            async token =>
            {
                var competing = new LocalCdcWorkflowJournalStore(_root);
                Func<Task> acquire = async () =>
                {
                    await using var other = await competing.AcquireAsync(
                        TimeSpan.FromMilliseconds(40),
                        TimeSpan.FromMilliseconds(10),
                        token
                    );
                };
                (await acquire.Should().ThrowAsync<CdcWorkflowStateException>())
                    .Which.Failure.Should()
                    .Be(CdcWorkflowStateFailure.LockTimeout);
                return true;
            },
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_rejects_a_disposed_controller_session_without_advancing()
    {
        await _session.DisposeAsync();
        Func<Task> act = () => RunAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
        _effects.Should().Be(0);
    }

    [TestCase("operation")]
    [TestCase("binding")]
    [TestCase("scope")]
    [TestCase("previous")]
    [TestCase("decrease")]
    [TestCase("equal")]
    public void It_rejects_invalid_increase_scope_before_starting_an_invocation(string mutation)
    {
        var scope = mutation switch
        {
            "operation" => _scope with { OperationId = Guid.Empty },
            "binding" => _scope with { BindingIdentity = null! },
            "scope" => null!,
            "previous" => _scope with { PreviousMaxRecordBytes = 0 },
            "decrease" => _scope with { RequestedMaxRecordBytes = 999 },
            _ => _scope with { RequestedMaxRecordBytes = 1000 },
        };
        Action act = () => _session.BeginRecordSizeAcknowledgement(_workflow, scope);
        act.Should().Throw<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_rejects_a_confirmation_for_another_workflow()
    {
        _invocation = _session.BeginRecordSizeAcknowledgement(Guid.NewGuid(), _scope);
        _confirmation = _confirmation with
        {
            Acknowledgement = _confirmation.Acknowledgement with { InvocationId = _invocation.InvocationId },
        };
        await RejectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_unavailable_or_corrupt_workflow_state(bool corrupt)
    {
        if (corrupt)
        {
            await File.WriteAllTextAsync(JournalPath, "{invalid");
        }
        else
        {
            File.Delete(JournalPath);
        }
        Func<Task> act = () => RunAsync();
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        _effects.Should().Be(0);
    }
}
