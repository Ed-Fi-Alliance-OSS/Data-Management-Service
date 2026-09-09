// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC filesystem state requires Unix owner-only permissions.")]
public class Given_CdcSourcePublicationHistory(CdcProvider provider)
{
    private string _root = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ICdcManagedDatabaseProvisioner _provisioner = null!;
    private CdcManagedProvisioningResult _created = null!;
    private CdcTargetIdentity Target => CdcWorkflowJournalTestData.Target with { Provider = provider };
    private static string Fingerprint => CdcWorkflowJournalTestData.Fingerprint;
    private string HistoryPath =>
        Path.Combine(_root, "source-history", "local", new string('a', 64) + ".json");
    private string JournalPath => Path.Combine(_root, "workflows", "local", "instance", "1.json");

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cdc-history-{Guid.NewGuid():N}");
        _store = new(_root);
        _provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => _provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => _provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._)).Returns(Fingerprint);
        _created = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            _provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private Task<LocalCdcWorkflowJournalStore.Session> AcquireAsync() =>
        _store.AcquireAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), CancellationToken.None);

    private async Task<CdcSourcePublicationHistory> ReadAsync()
    {
        await using var session = await AcquireAsync();
        return await session.ReadSourcePublicationHistoryAsync(Target, Fingerprint, CancellationToken.None);
    }

    private async Task IntentAsync(CdcWorkflowEffect effect)
    {
        await using var session = await AcquireAsync();
        await session.RecordIntentAsync(
            Target,
            _created.WorkflowId,
            Guid.NewGuid(),
            effect,
            [],
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_keeps_established_exposure_active_after_stop_and_historical_after_retirement()
    {
        await using var session = await AcquireAsync();
        var names = CdcArtifactNameGenerator
            .Render(new(Target.DeploymentKey, "journal", Target.InstanceKey, Target.Generation, provider))
            .Inventory!;
        var artifacts = names
            .GovernedArtifacts.Where(a =>
                a.Kind
                    is CdcGovernedArtifactKind.PostgresqlLogicalSlot
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
            )
            .Select(a => new CdcRetainedProviderIdentity(a.Kind, a.Name, Fingerprint))
            .ToImmutableArray();
        await CompleteAsync(
            CdcWorkflowEffect.CreateProvider,
            new CdcWorkflowCompletion.Provider(
                artifacts,
                provider == CdcProvider.Postgresql
                    ?
                    [
                        new(
                            new(names.PostgresqlLogicalSlotName!),
                            new(Ddl.CdcSourceFingerprintMetadata.Version, Fingerprint),
                            Ddl.CdcPostgresqlInitialReplicationSlotProof.CreateDatabaseIdentityToken(
                                "private-source"
                            ),
                            "0/10",
                            "0/10"
                        ),
                    ]
                    : []
            )
        );
        await CompleteAsync(
            CdcWorkflowEffect.EstablishConnector,
            new CdcWorkflowCompletion.Connector(Fingerprint)
        );
        await CompleteAsync(CdcWorkflowEffect.StopConnector, new CdcWorkflowCompletion.Reconciled());
        (await session.ReadSourcePublicationHistoryAsync(Target, Fingerprint, CancellationToken.None))
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Active);
        await CompleteAsync(CdcWorkflowEffect.Retire, new CdcWorkflowCompletion.Reconciled());
        (await session.ReadSourcePublicationHistoryAsync(Target, Fingerprint, CancellationToken.None))
            .Transitions.Select(t => t.Status)
            .Should()
            .Equal(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                DocumentCacheDownstreamPublicationStatus.Possible,
                DocumentCacheDownstreamPublicationStatus.Active,
                DocumentCacheDownstreamPublicationStatus.Historical
            );

        async Task CompleteAsync(CdcWorkflowEffect effect, CdcWorkflowCompletion completion)
        {
            Guid operation = Guid.NewGuid();
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                operation,
                effect,
                [],
                CancellationToken.None
            );
            await session.ReconcileCompletionAsync(
                Target,
                _created.WorkflowId,
                operation,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(completion)
                    ),
                CancellationToken.None
            );
        }
    }

    [Test]
    public async Task It_serializes_exposure_and_binding_work_under_the_same_controller_lock()
    {
        var session = await AcquireAsync();
        try
        {
            await session.RecordSourceExposureAsync(
                Target,
                _created.WorkflowId,
                Fingerprint,
                CancellationToken.None
            );
            // A second process/session cannot read an earlier internal-only proof while the owner
            // is between durable exposure and binding mutation.
            var contender = new LocalCdcWorkflowJournalStore(_root);
            var failure = await FluentActions
                .Awaiting(() =>
                    contender.AcquireAsync(
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(10),
                        CancellationToken.None
                    )
                )
                .Should()
                .ThrowAsync<CdcWorkflowStateException>();
            failure.Which.Failure.Should().Be(CdcWorkflowStateFailure.LockTimeout);
        }
        finally
        {
            await session.DisposeAsync();
        }
        (await ReadAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [Test]
    public async Task It_cannot_replace_existing_source_history_from_a_new_generation_receipt()
    {
        await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        var original = await File.ReadAllTextAsync(HistoryPath);
        // Even an incorrectly supplied second CREATE outcome cannot overwrite surviving exposure.
        await FluentActions
            .Awaiting(() =>
                new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                    Target with
                    {
                        Generation = 2,
                    },
                    _provisioner,
                    purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                )
            )
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        (await File.ReadAllTextAsync(HistoryPath)).Should().Be(original);
        (await ReadAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [TestCase(CdcWorkflowEffect.StopConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    public async Task It_does_not_block_containment_or_cleanup_intent_when_history_is_missing(
        CdcWorkflowEffect effect
    )
    {
        File.Delete(HistoryPath);
        await IntentAsync(effect);
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
        File.Exists(HistoryPath).Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_label_projection_only_work_as_downstream_exposure()
    {
        await IntentAsync(CdcWorkflowEffect.ActivateProjection);
        await IntentAsync(CdcWorkflowEffect.StopConnector);
        (await ReadAsync())
            .Transitions.Select(t => t.Status)
            .Should()
            .Equal(DocumentCacheDownstreamPublicationStatus.InternalOnly);
    }

    [Test]
    public async Task It_attests_managed_non_cdc_creation_and_reopens_the_same_receipt()
    {
        _store = new(_root);
        var history = await ReadAsync();
        history.CreationReceipt.Should().Be(_created.CreationReceipt);
        history.WorkflowId.Should().Be(_created.WorkflowId);
        history.CreationTarget.Should().Be(Target);
        history.PhysicalSourceFingerprint.Should().Be(Fingerprint);
        history
            .Transitions.Select(t => t.Status)
            .Should()
            .Equal(DocumentCacheDownstreamPublicationStatus.InternalOnly);
        var retry = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            _provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        retry.Should().Be(_created);
        (await File.ReadAllTextAsync(HistoryPath))
            .Should()
            .NotContain("Password")
            .And.NotContain("connectionString");
        A.CallTo(() => _provisioner.CreateDatabase()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_does_not_attest_reused_or_unmanaged_databases()
    {
        Directory.Delete(_root, true);
        A.CallTo(() => _provisioner.CreateDatabase()).Returns(false);
        var reused = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            _provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        reused.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Reused);
        File.Exists(HistoryPath).Should().BeFalse();
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
        await FluentActions
            .Awaiting(() => IntentAsync(CdcWorkflowEffect.ReserveBinding))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
    }

    [TestCase(CdcWorkflowEffect.ReserveBinding)]
    [TestCase(CdcWorkflowEffect.CreateProvider)]
    [TestCase(CdcWorkflowEffect.PrepareKafka)]
    [TestCase(CdcWorkflowEffect.RegisterConnector)]
    [TestCase(CdcWorkflowEffect.ResumeConnector)]
    public async Task It_irrevocably_loses_internal_only_before_downstream_intent(CdcWorkflowEffect effect)
    {
        await IntentAsync(effect);
        (await ReadAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
        await IntentAsync(CdcWorkflowEffect.StopConnector);
        (await ReadAsync()).Transitions.Should().HaveCount(2);
    }

    [Test]
    public async Task It_records_other_managed_exposure_without_a_binding_and_never_reattests_on_provisioning_retry()
    {
        await using (var session = await AcquireAsync())
        {
            await session.RecordSourceExposureAsync(
                Target,
                _created.WorkflowId,
                Fingerprint,
                CancellationToken.None
            );
            await session.RecordSourceExposureAsync(
                Target,
                _created.WorkflowId,
                Fingerprint,
                CancellationToken.None
            );
        }
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            _provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        (await ReadAsync())
            .Transitions.Select(t => t.Status)
            .Should()
            .Equal(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                DocumentCacheDownstreamPublicationStatus.Possible
            );
    }

    [Test]
    public async Task It_preserves_history_across_retirement_and_generation_or_instance_changes()
    {
        await using var session = await AcquireAsync();
        Guid retire = Guid.NewGuid();
        await session.RecordIntentAsync(
            Target,
            _created.WorkflowId,
            retire,
            CdcWorkflowEffect.Retire,
            [],
            CancellationToken.None
        );
        await session.ReconcileCompletionAsync(
            Target,
            _created.WorkflowId,
            retire,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Reconciled()
                    )
                ),
            CancellationToken.None
        );
        // Binding/incident cleanup has no ownership of source-history or original workflow evidence.
        var later = await session.ReadSourcePublicationHistoryAsync(
            Target with
            {
                InstanceKey = "replacement",
                Generation = 2,
            },
            Fingerprint,
            CancellationToken.None
        );
        later.Transitions[^1].Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        await session.RecordSourceExposureAsync(
            Target,
            _created.WorkflowId,
            Fingerprint,
            CancellationToken.None
        );
        (await session.ReadSourcePublicationHistoryAsync(Target, Fingerprint, CancellationToken.None))
            .Transitions.Should()
            .Equal(later.Transitions);
    }

    [TestCase("history")]
    [TestCase("receipt")]
    [TestCase("all")]
    public async Task It_never_reconstructs_missing_evidence_on_retry(string missing)
    {
        if (missing == "history")
        {
            File.Delete(HistoryPath);
        }
        if (missing == "receipt")
        {
            File.Delete(JournalPath);
        }
        if (missing == "all")
        {
            Directory.Delete(_root, true);
        }
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
        if (missing == "history")
        {
            await FluentActions
                .Awaiting(() =>
                    new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                        Target,
                        _provisioner,
                        purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                    )
                )
                .Should()
                .ThrowAsync<CdcWorkflowStateException>();
            File.Exists(HistoryPath).Should().BeFalse();
        }
    }

    [TestCase("tenant")]
    [TestCase("data-store")]
    [TestCase("provider")]
    [TestCase("source")]
    [TestCase("deployment")]
    public async Task It_rejects_wrong_normalized_target_or_source(string mismatch)
    {
        var target = mismatch switch
        {
            "tenant" => Target with { TenantKey = "other" },
            "data-store" => Target with { DataStoreId = "2" },
            "provider" => Target with
            {
                Provider =
                    provider == CdcProvider.Postgresql ? CdcProvider.SqlServer : CdcProvider.Postgresql,
            },
            "deployment" => Target with { DeploymentKey = "other" },
            _ => Target,
        };
        await using var session = await AcquireAsync();
        await FluentActions
            .Awaiting(() =>
                session.ReadSourcePublicationHistoryAsync(
                    target,
                    mismatch == "source" ? "sha256:" + new string('b', 64) : Fingerprint,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
    }

    [TestCase("version")]
    [TestCase("unknown-field")]
    [TestCase("duplicate-field")]
    [TestCase("missing-field")]
    [TestCase("reused-receipt")]
    [TestCase("wrong-receipt")]
    [TestCase("wrong-workflow")]
    [TestCase("null-transition")]
    [TestCase("future")]
    [TestCase("unknown-status")]
    [TestCase("reversal")]
    [TestCase("journal-contradiction")]
    public async Task It_rejects_corrupt_contradictory_or_reversed_records_without_leaking_payload(
        string mutation
    )
    {
        if (mutation is "reversal" or "journal-contradiction")
        {
            await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        }
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(HistoryPath))!;
        switch (mutation)
        {
            case "version":
                json["version"] = 99;
                break;
            case "unknown-field":
                json["password"] = "sentinel-secret";
                break;
            case "missing-field":
                json.AsObject().Remove("creationReceipt");
                break;
            case "reused-receipt":
                json["creationReceipt"]!["outcome"] = "Reused";
                break;
            case "wrong-receipt":
                json["creationReceipt"]!["receiptId"] = Guid.NewGuid();
                break;
            case "wrong-workflow":
                json["workflowId"] = Guid.NewGuid();
                break;
            case "null-transition":
                json["transitions"]!.AsArray().Add((JsonNode)null!);
                break;
            case "future":
                json["transitions"]![0]!["recordedAt"] = DateTimeOffset.UtcNow.AddDays(1);
                break;
            case "unknown-status":
                json["transitions"]![0]!["status"] = "Unknown";
                break;
            case "reversal":
                json["transitions"]!.AsArray().Add(json["transitions"]![0]!.DeepClone());
                break;
            case "journal-contradiction":
                json["transitions"]!.AsArray().RemoveAt(1);
                break;
        }
        string payload = json.ToJsonString();
        if (mutation == "duplicate-field")
        {
            payload = payload.Insert(1, "\"version\":1,");
        }
        await File.WriteAllTextAsync(HistoryPath, payload);
        var failure = await FluentActions
            .Awaiting(ReadAsync)
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        failure.Which.ToString().Should().NotContain("sentinel-secret").And.NotContain(_root);
        await FluentActions
            .Awaiting(() => IntentAsync(CdcWorkflowEffect.ReserveBinding))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task It_keeps_exposure_before_reservation_across_write_failures(int boundaryIndex)
    {
        int calls = 0;
        _store = new(
            _root,
            TimeProvider.System,
            _ =>
            {
                if (calls++ == boundaryIndex)
                {
                    throw new IOException("sentinel-secret");
                }
            }
        );
        await FluentActions
            .Awaiting(() => IntentAsync(CdcWorkflowEffect.ReserveBinding))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        _store = new(_root);
        var history = await ReadAsync();
        history
            .Transitions[^1]
            .Status.Should()
            .Be(
                boundaryIndex >= 2
                    ? DocumentCacheDownstreamPublicationStatus.Possible
                    : DocumentCacheDownstreamPublicationStatus.InternalOnly
            );
        await using (var session = await AcquireAsync())
        {
            (await session.ReadAsync(Target, CancellationToken.None))
                .Operations.Should()
                .NotContain(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
        }
        await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        (await ReadAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task It_fails_closed_when_initial_attestation_or_source_association_is_interrupted(
        int boundaryIndex
    )
    {
        Directory.Delete(_root, true);
        int calls = 0;
        bool associating = false;
        A.CallTo(() => _provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Invokes(() => associating = true)
            .Returns(Fingerprint);
        _store = new(
            _root,
            TimeProvider.System,
            _ =>
            {
                if (associating && calls++ == boundaryIndex)
                {
                    throw new IOException("sentinel-secret");
                }
            }
        );
        await FluentActions
            .Awaiting(() =>
                new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                    Target,
                    _provisioner,
                    purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                )
            )
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        _store = new(_root);
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
        await FluentActions
            .Awaiting(() =>
                new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                    Target,
                    _provisioner,
                    purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                )
            )
            .Should()
            .ThrowAsync<CdcManagedProvisioningRecoveryException>();
    }

    [Test]
    public async Task It_validates_owner_only_paths_and_rejects_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.GetUnixFileMode(HistoryPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(
            HistoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead
        );
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
        File.SetUnixFileMode(HistoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(HistoryPath, HistoryPath + ".saved");
        File.CreateSymbolicLink(HistoryPath, HistoryPath + ".saved");
        await FluentActions.Awaiting(ReadAsync).Should().ThrowAsync<CdcWorkflowStateException>();
    }

    [Test]
    public async Task It_preserves_cancellation_and_disallows_disposed_sessions()
    {
        var session = await AcquireAsync();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await FluentActions
            .Awaiting(() =>
                session.RecordSourceExposureAsync(
                    Target,
                    _created.WorkflowId,
                    Fingerprint,
                    cancellation.Token
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
        await session.DisposeAsync();
        await FluentActions
            .Awaiting(() =>
                session.ReadSourcePublicationHistoryAsync(Target, Fingerprint, CancellationToken.None)
            )
            .Should()
            .ThrowAsync<ObjectDisposedException>();
        (await ReadAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
    }
}
