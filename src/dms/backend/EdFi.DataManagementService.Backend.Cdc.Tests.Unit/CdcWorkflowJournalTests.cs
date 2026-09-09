// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;
using CdcProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class CdcWorkflowJournalTestData
{
    public static readonly CdcTargetIdentity Target = new(
        "local",
        "default",
        "1",
        "instance",
        1,
        CdcProvider.Postgresql
    );
    public static readonly string Fingerprint = "sha256:" + new string('a', 64);
}

[TestFixture]
[Platform(Exclude = "Win", Reason = "Local CDC filesystem state requires Unix owner-only permissions.")]
public class Given_CdcWorkflowJournal
{
    private string _root = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private LocalCdcWorkflowJournalStore.Session _session = null!;
    private Guid _workflow;
    private CdcTargetIdentity _target = null!;
    private string JournalPath => Path.Combine(_root, "workflows", "local", "instance", "1.json");

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cdc-journal-{Guid.NewGuid():N}");
        _store = new(_root);
        _session = await AcquireAsync(_store);
        _workflow = Guid.NewGuid();
        _target = CdcWorkflowJournalTestData.Target;
        await _session.CreateAsync(
            _workflow,
            _target,
            CancellationToken.None,
            CdcWorkflowPurpose.InitialCdcProvisioning
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        await _session.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Task<LocalCdcWorkflowJournalStore.Session> AcquireAsync(
        LocalCdcWorkflowJournalStore store
    ) => store.AcquireAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20), CancellationToken.None);

    private Task<CdcWorkflowJournal> ReadAsync() => _session.ReadAsync(_target, CancellationToken.None);

    private Task<CdcWorkflowJournal> IntentAsync(
        Guid id,
        CdcWorkflowEffect effect,
        ImmutableArray<CdcRecordSizeIncreaseJournal> increase = default
    ) =>
        _session.RecordIntentAsync(
            _target,
            _workflow,
            id,
            effect,
            increase.IsDefault ? [] : increase,
            CancellationToken.None
        );

    private Task<CdcWorkflowJournal> CompleteAsync(Guid id, CdcWorkflowCompletion completion) =>
        _session.ReconcileCompletionAsync(
            _target,
            _workflow,
            id,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(completion)
                ),
            CancellationToken.None
        );

    private async Task SourceAsync(CdcDatabaseCreationOutcome outcome = CdcDatabaseCreationOutcome.Created)
    {
        Guid creation = Guid.NewGuid();
        await IntentAsync(creation, CdcWorkflowEffect.CreateDatabase);
        await CompleteAsync(creation, new CdcWorkflowCompletion.Database(new(Guid.NewGuid(), outcome)));
        Guid source = Guid.NewGuid();
        await IntentAsync(source, CdcWorkflowEffect.AssociateSource);
        await CompleteAsync(source, new CdcWorkflowCompletion.Source(CdcWorkflowJournalTestData.Fingerprint));
    }

    [Test]
    public async Task It_persists_intent_before_a_receipt_and_requires_live_reconciliation_again_on_retry()
    {
        Guid operation = Guid.NewGuid();
        await IntentAsync(operation, CdcWorkflowEffect.CreateDatabase);
        (await ReadAsync()).Operations.Single().Completions.Should().BeEmpty();
        var receipt = new CdcWorkflowCompletion.Database(
            new(Guid.NewGuid(), CdcDatabaseCreationOutcome.Created)
        );
        await CompleteAsync(operation, receipt);
        int calls = 0;
        await _session.ReconcileCompletionAsync(
            _target,
            _workflow,
            operation,
            async (_, token) =>
            {
                calls++;
                (await File.ReadAllTextAsync(JournalPath, token)).Should().Contain(operation.ToString());
                return new CdcTransportResult<CdcWorkflowCompletion>.Observed(receipt);
            },
            CancellationToken.None
        );
        calls.Should().Be(1);
        await _session.DisposeAsync();
        _session = await AcquireAsync(new(_root));
        (await ReadAsync()).Operations.Single().Completions.Single().Evidence.Should().Be(receipt);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_does_not_complete_from_absent_or_unavailable_evidence(bool absent)
    {
        Guid operation = Guid.NewGuid();
        await IntentAsync(operation, CdcWorkflowEffect.CreateDatabase);
        Func<Task> act = () =>
            _session.ReconcileCompletionAsync(
                _target,
                _workflow,
                operation,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        absent
                            ? new CdcTransportResult<CdcWorkflowCompletion>.Absent()
                            : new CdcTransportResult<CdcWorkflowCompletion>.Unavailable(
                                new(CdcDeploymentComponent.WorkflowState, CdcDeploymentFailure.Unavailable)
                            )
                    ),
                CancellationToken.None
            );
        (await act.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(absent ? CdcWorkflowStateFailure.Contradictory : CdcWorkflowStateFailure.Unavailable);
        (await ReadAsync()).Operations.Single().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_completion_without_durable_intent()
    {
        Func<Task> act = () => CompleteAsync(Guid.NewGuid(), new CdcWorkflowCompletion.Reconciled());
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        (await ReadAsync()).Operations.Should().BeEmpty();
    }

    [Test]
    public async Task It_never_relabels_a_creation_receipt_or_a_reused_database()
    {
        await SourceAsync(CdcDatabaseCreationOutcome.Reused);
        var journal = await ReadAsync();
        Func<Task> changeReceipt = () =>
            CompleteAsync(
                journal.Operations[0].OperationId,
                new CdcWorkflowCompletion.Database(new(Guid.NewGuid(), CdcDatabaseCreationOutcome.Created))
            );
        await changeReceipt.Should().ThrowAsync<CdcWorkflowStateException>();
        Func<Task> reserve = () => IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.ReserveBinding);
        await reserve.Should().ThrowAsync<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_rejects_a_different_workflow_or_target_at_the_same_path()
    {
        Func<Task> wrongWorkflow = () =>
            _session.RecordIntentAsync(
                _target,
                Guid.NewGuid(),
                Guid.NewGuid(),
                CdcWorkflowEffect.CreateDatabase,
                [],
                CancellationToken.None
            );
        Func<Task> wrongTarget = () =>
            _session.ReadAsync(_target with { DataStoreId = "2" }, CancellationToken.None);
        await wrongWorkflow.Should().ThrowAsync<CdcWorkflowStateException>();
        await wrongTarget.Should().ThrowAsync<CdcWorkflowStateException>();
        Func<Task> overwrite = () =>
            _session.CreateAsync(
                Guid.NewGuid(),
                _target,
                CancellationToken.None,
                CdcWorkflowPurpose.InitialCdcProvisioning
            );
        await overwrite.Should().ThrowAsync<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_preserves_provider_creation_establishment_and_verified_shutdown_evidence()
    {
        await EstablishedAsync();
        Guid stop = Guid.NewGuid();
        await IntentAsync(stop, CdcWorkflowEffect.StopConnector);
        (await ReadAsync()).Operations.Last().Completions.Should().BeEmpty();
        await CompleteAsync(stop, new CdcWorkflowCompletion.Reconciled());
        await _session.DisposeAsync();
        _session = await AcquireAsync(new(_root));
        var journal = await ReadAsync();
        var provider = (CdcWorkflowCompletion.Provider)journal.Operations[2].Completions.Single().Evidence;
        provider.InitialSlotProofs.Single().RetainedRestartLsn.Should().Be("0/10");
        provider.Artifacts.Single().IdentityHash.Should().Be(CdcWorkflowJournalTestData.Fingerprint);
        journal
            .Operations[3]
            .Completions.Single()
            .Evidence.Should()
            .BeOfType<CdcWorkflowCompletion.Connector>();
        journal.Operations.Last().Completions.Should().ContainSingle();
    }

    private async Task EstablishedAsync()
    {
        await SourceAsync();
        string slot = CdcArtifactNameGenerator
            .Render(
                new(
                    _target.DeploymentKey,
                    "journal",
                    _target.InstanceKey,
                    _target.Generation,
                    _target.Provider
                )
            )
            .Inventory!.PostgresqlLogicalSlotName!;
        Guid provider = Guid.NewGuid();
        await IntentAsync(provider, CdcWorkflowEffect.CreateProvider);
        await CompleteAsync(
            provider,
            new CdcWorkflowCompletion.Provider(
                [
                    new(
                        CdcGovernedArtifactKind.PostgresqlLogicalSlot,
                        slot,
                        CdcWorkflowJournalTestData.Fingerprint
                    ),
                ],
                [
                    new(
                        new(slot),
                        new(CdcSourceFingerprintMetadata.Version, CdcWorkflowJournalTestData.Fingerprint),
                        CdcPostgresqlInitialReplicationSlotProof.CreateDatabaseIdentityToken(
                            "private-database"
                        ),
                        "0/10",
                        "0/10"
                    ),
                ]
            )
        );
        Guid connector = Guid.NewGuid();
        await IntentAsync(connector, CdcWorkflowEffect.EstablishConnector);
        await CompleteAsync(
            connector,
            new CdcWorkflowCompletion.Connector(CdcWorkflowJournalTestData.Fingerprint)
        );
    }

    [Test]
    public async Task It_burns_initial_enable_eligibility_on_writer_intent_even_without_completion()
    {
        await EstablishedAsync();
        await IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.AuthorizeWriterPublication);
        (await ReadAsync()).WriterPublicationAuthorized.Should().BeTrue();
        Func<Task> initial = () => IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.RegisterConnector);
        await initial.Should().ThrowAsync<CdcWorkflowStateException>();
        string json = await File.ReadAllTextAsync(JournalPath);
        json.Should().NotContain("ready").And.NotContain("barrier").And.NotContain("private-database");
    }

    [Test]
    public async Task It_retains_pending_increase_scope_and_append_only_acknowledgement_history()
    {
        await SourceAsync();
        var binding = CdcConnectorTemplateTestData
            .BuildRequest(EdFi.DataManagementService.Backend.Ddl.CdcProvider.Postgresql)
            .Binding;
        var identity = binding.ToCompleteBindingIdentity() with
        {
            DeploymentKey = _target.DeploymentKey,
            TenantKey = _target.TenantKey,
            DataStoreId = _target.DataStoreId,
            InstanceKey = _target.InstanceKey,
            Generation = _target.Generation,
            PhysicalSourceFingerprint = CdcWorkflowJournalTestData.Fingerprint,
        };
        var names = CdcArtifactNameGenerator
            .Render(new("local", "edfi", "instance", 1, CdcProvider.Postgresql))
            .Inventory!;
        identity = identity with { ConnectorName = names.ConnectorName, TopicName = names.TopicName };
        Guid increaseId = Guid.NewGuid();
        CdcRecordSizeAcknowledgement acknowledgement = new(
            Guid.NewGuid(),
            "operator",
            DateTimeOffset.UtcNow,
            true,
            []
        );
        await IntentAsync(
            increaseId,
            CdcWorkflowEffect.IncreaseRecordSize,
            [new(identity, 1000, 2000, [acknowledgement])]
        );
        await _session.DisposeAsync();
        _session = await AcquireAsync(new(_root));
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeTrue();
        var next = await _session.AppendAcknowledgementAsync(
            _target,
            _workflow,
            increaseId,
            acknowledgement with
            {
                InvocationId = Guid.NewGuid(),
                ConfirmedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );
        var scope = next.Operations.Last().RecordSizeIncrease.Single();
        scope.PreviousMaxRecordBytes.Should().Be(1000);
        scope.RequestedMaxRecordBytes.Should().Be(2000);
        scope.BindingIdentity.Should().Be(identity);
        scope.Acknowledgements.Should().HaveCount(2).And.Contain(acknowledgement);
        await CompleteAsync(increaseId, new CdcWorkflowCompletion.Reconciled());
        (await ReadAsync()).HasPendingRecordSizeIncrease.Should().BeFalse();
    }

    [TestCase("missing", CdcWorkflowStateFailure.Missing)]
    [TestCase("malformed", CdcWorkflowStateFailure.Invalid)]
    [TestCase("missing-purpose", CdcWorkflowStateFailure.Invalid)]
    [TestCase("unknown-purpose", CdcWorkflowStateFailure.Invalid)]
    [TestCase("legacy-version", CdcWorkflowStateFailure.UnsupportedVersion)]
    [TestCase("version", CdcWorkflowStateFailure.UnsupportedVersion)]
    [TestCase("duplicate", CdcWorkflowStateFailure.Invalid)]
    [TestCase("unknown", CdcWorkflowStateFailure.Invalid)]
    [TestCase("omitted", CdcWorkflowStateFailure.Invalid)]
    [TestCase("null-operation", CdcWorkflowStateFailure.Invalid)]
    [TestCase("wrong-target", CdcWorkflowStateFailure.Contradictory)]
    [TestCase("unknown-effect", CdcWorkflowStateFailure.Invalid)]
    public async Task It_fails_closed_on_missing_corrupt_or_contradictory_state(
        string mutation,
        CdcWorkflowStateFailure failure
    )
    {
        await IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.CreateDatabase);
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        switch (mutation)
        {
            case "missing":
                File.Delete(JournalPath);
                break;
            case "malformed":
                await File.WriteAllTextAsync(JournalPath, "{partial");
                break;
            case "missing-purpose":
                json.AsObject().Remove("purpose");
                break;
            case "unknown-purpose":
                json["purpose"] = "Unknown";
                break;
            case "legacy-version":
                json["version"] = 1;
                break;
            case "version":
                json["version"] = 999;
                break;
            case "unknown":
                json["password"] = "super-secret";
                break;
            case "omitted":
                json.AsObject().Remove("workflowId");
                break;
            case "null-operation":
                json["operations"]!.AsArray().Add((JsonNode)null!);
                break;
            case "wrong-target":
                json["target"]!["dataStoreId"] = "2";
                break;
            case "unknown-effect":
                json["operations"]![0]!["effect"] = "unknown";
                break;
        }
        if (mutation == "duplicate")
        {
            await File.WriteAllTextAsync(
                JournalPath,
                json.ToJsonString()
                    .Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal)
            );
        }
        else if (mutation is not ("missing" or "malformed"))
        {
            await File.WriteAllTextAsync(JournalPath, json.ToJsonString());
        }
        Func<Task> act = ReadAsync;
        var error = (await act.Should().ThrowAsync<CdcWorkflowStateException>()).Which;
        error.Failure.Should().Be(failure);
        error.ToString().Should().NotContain(_root).And.NotContain("super-secret");
        error.InnerException.Should().BeNull();
    }

    [Test]
    public async Task It_rejects_unsafe_paths_case_collisions_links_and_shared_files()
    {
        Func<Task> traversal = () =>
            _session.ReadAsync(_target with { InstanceKey = "../escape" }, CancellationToken.None);
        await traversal.Should().ThrowAsync<CdcWorkflowStateException>();
        File.Move(JournalPath, JournalPath + ".saved");
        File.CreateSymbolicLink(JournalPath, JournalPath + ".saved");
        Func<Task> read = ReadAsync;
        await read.Should().ThrowAsync<CdcWorkflowStateException>();
        File.Delete(JournalPath);
        File.Move(JournalPath + ".saved", JournalPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                JournalPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead
            );
        }
        await read.Should().ThrowAsync<CdcWorkflowStateException>();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(JournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Directory.CreateDirectory(Path.Combine(_root, "WORKFLOWS"));
        await read.Should().ThrowAsync<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_creates_owner_only_files_and_directories()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(_root)
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(JournalPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Path.Combine(_root, "controller.lock"))
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.CreateDatabase);
        Directory.GetFiles(Path.GetDirectoryName(JournalPath)!, "*.tmp").Should().BeEmpty();
    }

    [Test]
    public async Task It_bounds_lock_contention_and_preserves_cancellation()
    {
        LocalCdcWorkflowJournalStore contender = new(_root);
        Func<Task> timeout = () =>
            contender.AcquireAsync(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            );
        (await timeout.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.LockTimeout);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        Func<Task> cancel = () =>
            contender.AcquireAsync(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(20),
                cancellation.Token
            );
        (await cancel.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        await _session.DisposeAsync();
        await using var acquired = await AcquireAsync(contender);
        Func<Task> disposed = ReadAsync;
        await disposed.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task It_serializes_two_controller_processes_and_releases_the_lock_after_abrupt_exit()
    {
        await _session.DisposeAsync();
        using Process process = StartChild("hold");
        try
        {
            (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))
                .Should()
                .Be("locked");
            Func<Task> act = () =>
                _store.AcquireAsync(
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(10),
                    CancellationToken.None
                );
            (await act.Should().ThrowAsync<CdcWorkflowStateException>())
                .Which.Failure.Should()
                .Be(CdcWorkflowStateFailure.LockTimeout);
        }
        finally
        {
            await KillAsync(process);
        }
        _session = await AcquireAsync(_store);
        (await ReadAsync()).WorkflowId.Should().Be(_workflow);
    }

    [TestCase("BeforeTemporaryWrite", 0)]
    [TestCase("AfterTemporaryFlush", 0)]
    [TestCase("AfterAtomicReplacement", 1)]
    public async Task It_never_exposes_partial_state_when_the_controller_process_crashes(
        string boundary,
        int expectedOperations
    )
    {
        await _session.DisposeAsync();
        using Process process = StartChild(boundary);
        try
        {
            (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))
                .Should()
                .Be("boundary");
        }
        finally
        {
            await KillAsync(process);
        }
        _session = await AcquireAsync(_store);
        var journal = await ReadAsync();
        journal.Operations.Should().HaveCount(expectedOperations);
        journal.WorkflowId.Should().Be(_workflow);
        journal.Operations.SelectMany(operation => operation.Completions).Should().BeEmpty();
    }

    [Test]
    public async Task It_keeps_the_controller_lock_until_inflight_reconciliation_and_disposal_finish()
    {
        Guid id = Guid.NewGuid();
        await IntentAsync(id, CdcWorkflowEffect.CreateDatabase);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CdcWorkflowJournal> completion = _session.ReconcileCompletionAsync(
            _target,
            _workflow,
            id,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                    new CdcWorkflowCompletion.Database(
                        new(Guid.NewGuid(), CdcDatabaseCreationOutcome.Created)
                    )
                );
            },
            CancellationToken.None
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = _session.DisposeAsync().AsTask();
        try
        {
            Func<Task> contender = () =>
                new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(10),
                    CancellationToken.None
                );
            (await contender.Should().ThrowAsync<CdcWorkflowStateException>())
                .Which.Failure.Should()
                .Be(CdcWorkflowStateFailure.LockTimeout);
            disposal.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await completion;
            await disposal;
        }
        _session = await AcquireAsync(_store);
        (await ReadAsync()).Operations.Single().Completions.Should().ContainSingle();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_retains_intent_when_reconciliation_is_cancelled_or_throws_sensitive_text(bool cancel)
    {
        Guid id = Guid.NewGuid();
        await IntentAsync(id, CdcWorkflowEffect.CreateDatabase);
        using CancellationTokenSource cancellation = new();
        Func<Task> act = () =>
            _session.ReconcileCompletionAsync(
                _target,
                _workflow,
                id,
                (_, token) =>
                {
                    if (cancel)
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                    throw new HttpRequestException("Password=super-secret;Host=private-source");
                },
                cancellation.Token
            );
        if (cancel)
        {
            (await act.Should().ThrowAsync<OperationCanceledException>())
                .Which.CancellationToken.Should()
                .Be(cancellation.Token);
        }
        else
        {
            var exception = (await act.Should().ThrowAsync<CdcWorkflowStateException>()).Which;
            exception.ToString().Should().NotContain("super-secret").And.NotContain("private-source");
            exception.InnerException.Should().BeNull();
        }
        (await ReadAsync()).Operations.Single().Completions.Should().BeEmpty();
    }

    [TestCase("BeforeTemporaryWrite", 0)]
    [TestCase("AfterTemporaryFlush", 0)]
    [TestCase("AfterAtomicReplacement", 1)]
    public async Task It_preserves_a_complete_snapshot_and_sanitizes_interrupted_write_errors(
        string boundary,
        int operationCount
    )
    {
        await _session.DisposeAsync();
        _store = new(
            _root,
            TimeProvider.System,
            step =>
            {
                if (step.ToString() == boundary)
                {
                    throw new IOException("Password=super-secret;" + _root);
                }
            }
        );
        _session = await AcquireAsync(_store);
        Func<Task> act = () => IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.CreateDatabase);
        var error = (await act.Should().ThrowAsync<CdcWorkflowStateException>()).Which;
        error.ToString().Should().NotContain("super-secret").And.NotContain(_root);
        (await ReadAsync()).Operations.Should().HaveCount(operationCount);
        Directory.GetFiles(Path.GetDirectoryName(JournalPath)!, "*.tmp").Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_raw_source_identifiers_before_writing_them()
    {
        Guid creation = Guid.NewGuid();
        await IntentAsync(creation, CdcWorkflowEffect.CreateDatabase);
        await CompleteAsync(
            creation,
            new CdcWorkflowCompletion.Database(new(Guid.NewGuid(), CdcDatabaseCreationOutcome.Created))
        );
        Guid source = Guid.NewGuid();
        await IntentAsync(source, CdcWorkflowEffect.AssociateSource);
        Func<Task> act = () =>
            CompleteAsync(
                source,
                new CdcWorkflowCompletion.Source("Server=private-source;Password=super-secret")
            );
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        (await File.ReadAllTextAsync(JournalPath))
            .Should()
            .NotContain("super-secret")
            .And.NotContain("private-source");
    }

    [Test]
    public async Task It_rejects_an_unrecorded_creation_outcome_instead_of_inferring_it_from_later_evidence()
    {
        await IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.CreateDatabase);
        Func<Task> act = () => IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.AssociateSource);
        (await act.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.Contradictory);
        (await ReadAsync()).Operations.Should().ContainSingle();
    }

    [Test]
    public async Task It_roundtrips_SQL_Server_capture_identities_without_PostgreSQL_creation_proof()
    {
        await _session.DisposeAsync();
        File.Delete(JournalPath);
        _session = await AcquireAsync(_store);
        _target = _target with { Provider = CdcProvider.SqlServer };
        await _session.CreateAsync(
            _workflow,
            _target,
            CancellationToken.None,
            CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await SourceAsync();
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(
                new(
                    _target.DeploymentKey,
                    "journal",
                    _target.InstanceKey,
                    _target.Generation,
                    _target.Provider
                )
            )
            .Inventory!;
        var artifacts = inventory
            .GovernedArtifacts.Where(a =>
                a.Kind
                    is CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
            )
            .Select(a => new CdcRetainedProviderIdentity(
                a.Kind,
                a.Name,
                CdcWorkflowJournalTestData.Fingerprint
            ))
            .ToImmutableArray();
        Guid id = Guid.NewGuid();
        await IntentAsync(id, CdcWorkflowEffect.CreateProvider);
        await CompleteAsync(id, new CdcWorkflowCompletion.Provider(artifacts, []));
        var evidence = (CdcWorkflowCompletion.Provider)
            (await ReadAsync()).Operations.Last().Completions.Single().Evidence;
        evidence.Artifacts.Should().BeEquivalentTo(artifacts);
        evidence.InitialSlotProofs.Should().BeEmpty();
    }

    private CdcRecordSizeIncreaseJournal IncreaseScope()
    {
        CdcArtifactInventory names = CdcArtifactNameGenerator
            .Render(
                new(_target.DeploymentKey, "edfi", _target.InstanceKey, _target.Generation, _target.Provider)
            )
            .Inventory!;
        CdcCompleteBindingIdentity identity = new(
            _target.DeploymentKey,
            _target.TenantKey,
            _target.DataStoreId,
            _target.InstanceKey,
            _target.Generation,
            _target.Provider,
            CdcWorkflowJournalTestData.Fingerprint,
            names.ConnectorName,
            names.TopicName
        );
        return new(identity, 1000, 2000, [new(Guid.NewGuid(), "operator", DateTimeOffset.UtcNow, true, [])]);
    }

    [TestCase("source")]
    [TestCase("generation")]
    [TestCase("topic")]
    [TestCase("ceiling")]
    [TestCase("missing-acknowledgement")]
    [TestCase("omitted-consumers")]
    [TestCase("sensitive-reference")]
    [TestCase("duplicate-confirmation")]
    public async Task It_rejects_incomplete_or_mismatched_increase_scope_before_persisting_intent(
        string change
    )
    {
        await SourceAsync();
        CdcRecordSizeIncreaseJournal scope = IncreaseScope();
        CdcRecordSizeAcknowledgement acknowledgement = scope.Acknowledgements[0];
        scope = change switch
        {
            "source" => scope with
            {
                BindingIdentity = scope.BindingIdentity with
                {
                    PhysicalSourceFingerprint = "sha256:" + new string('b', 64),
                },
            },
            "generation" => scope with { BindingIdentity = scope.BindingIdentity with { Generation = 2 } },
            "topic" => scope with
            {
                BindingIdentity = scope.BindingIdentity with { TopicName = "unrelated" },
            },
            "ceiling" => scope with { RequestedMaxRecordBytes = 999 },
            "missing-acknowledgement" => scope with { Acknowledgements = [] },
            "omitted-consumers" => scope with
            {
                Acknowledgements = [acknowledgement with { NoConsumers = false }],
            },
            "sensitive-reference" => scope with
            {
                Acknowledgements =
                [
                    acknowledgement with
                    {
                        NoConsumers = false,
                        Consumers = [new("consumer", "v1", "owner", "Password=super-secret")],
                    },
                ],
            },
            "duplicate-confirmation" => scope with { Acknowledgements = [acknowledgement, acknowledgement] },
            _ => throw new ArgumentException("Unknown test case."),
        };
        Func<Task> act = () => IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.IncreaseRecordSize, [scope]);
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        (await ReadAsync()).Operations.Should().HaveCount(2);
        (await File.ReadAllTextAsync(JournalPath)).Should().NotContain("super-secret");
    }

    [Test]
    public async Task It_rejects_a_second_pending_rollout_and_null_completion_provenance()
    {
        await SourceAsync();
        await IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.IncreaseRecordSize, [IncreaseScope()]);
        Func<Task> second = () =>
            IntentAsync(Guid.NewGuid(), CdcWorkflowEffect.IncreaseRecordSize, [IncreaseScope()]);
        await second.Should().ThrowAsync<CdcWorkflowStateException>();
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        json["operations"]![2]!["completions"]!.AsArray().Add((JsonNode)null!);
        await File.WriteAllTextAsync(JournalPath, json.ToJsonString());
        Func<Task> read = ReadAsync;
        (await read.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.Invalid);
    }

    [Test]
    public async Task It_creates_nested_state_roots_and_rejects_symlink_aliases_or_shared_writable_roots()
    {
        string parent = Path.Combine(_root, "new-parent");
        string nested = Path.Combine(parent, "state");
        await using (var session = await AcquireAsync(new(nested)))
        {
            await session.CreateAsync(
                Guid.NewGuid(),
                _target,
                CancellationToken.None,
                CdcWorkflowPurpose.InitialCdcProvisioning
            );
        }
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(parent)
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, parent);
        Func<Task> linked = () => AcquireAsync(new(Path.Combine(alias, "state")));
        await linked.Should().ThrowAsync<CdcWorkflowStateException>();
        Directory.Delete(alias);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                nested,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupWrite
            );
        }
        Func<Task> shared = () => AcquireAsync(new(nested));
        await shared.Should().ThrowAsync<CdcWorkflowStateException>();
    }

    private Process StartChild(string mode)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(CdcWorkflowJournalProcess).Assembly.Location);
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(mode);
        return Process.Start(start)!;
    }

    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
