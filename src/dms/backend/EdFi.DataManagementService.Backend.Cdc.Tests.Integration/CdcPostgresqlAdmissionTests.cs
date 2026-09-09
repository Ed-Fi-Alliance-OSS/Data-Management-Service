// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category(CdcControllerCategories.Admission)]
[Category("PostgresqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Postgresql_Controller_Admission
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;

    [SetUp]
    public async Task Setup()
    {
        _timeout = new(TimeSpan.FromMinutes(8));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(CdcProvider.Postgresql, Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
        _timeout.Dispose();
    }

    [Test]
    public async Task It_admits_a_fresh_owned_source_only_after_live_barrier_and_lag()
    {
        await _fixture.RegisterAsync(Token);
        var journal = await _fixture.JournalAsync(Token);
        journal.WriterPublicationAuthorized.Should().BeFalse();
        var publication = Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeTrue();
        _fixture.Barriers.Should().NotBeEmpty();
        _fixture.ProjectionObservations.Should().HaveCountGreaterThanOrEqualTo(2);
        _fixture.ProviderModes.Should().Contain(CdcProviderSetupMode.InitialCreateOrExactMatch);
        _fixture
            .ProviderModes.Skip(1)
            .Should()
            .OnlyContain(mode => mode == CdcProviderSetupMode.ValidateOnly);
        _fixture.Lag.Should().NotBeEmpty();
        _fixture.Lag[^1].LagState.Should().Be(CoreCdc.CdcConnectorLagState.WithinThreshold);
        publication.AuthorizedAt.Should().BeOnOrAfter(_fixture.RuntimeStoppedAt);
        (publication.AuthorizedAt - _fixture.Lag[^1].ObservedAt)
            .Should()
            .BeLessThan(_fixture.Request.Timing.MaximumObservationAge);
        var barrier = _fixture.Barriers[^1];
        _fixture
            .ProjectionObservations.Should()
            .Contain(o =>
                o.ObservedAt <= barrier.BarrierCapturedAt
                && o.Targets.Single().CaughtUp.Status
                    == EdFi.DataManagementService.Core.DocumentCache.DocumentCacheCaughtUpStatus.CaughtUp
            );
        _fixture
            .ProjectionObservations.Should()
            .Contain(o =>
                o.ObservedAt >= barrier.BarrierCapturedAt
                && o.Targets.Single().CaughtUp.Status
                    == EdFi.DataManagementService.Core.DocumentCache.DocumentCacheCaughtUpStatus.CaughtUp
            );
        var offset = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        );
        CoreCdc
            .CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                CoreCdc
                    .CdcPostgresqlProviderPosition.ParseWalLsn(barrier.PostgresqlBarrierLsn)
                    .Position!.Value,
                offset.Postgresql
            )
            .AtOrBeyondBarrier.Should()
            .BeTrue();
        var retained = journal
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider)
            .Completions.Single()
            .Evidence;
        ((CdcWorkflowCompletion.Provider)retained).InitialSlotProofs.Should().ContainSingle();
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM dms.\"Document\"", Token)).Should().Be(0);
    }

    [TestCase(CdcControllerBoundary.Binding, CdcControllerEdge.Before, "Disabled")]
    [TestCase(CdcControllerBoundary.Binding, CdcControllerEdge.After, "Disabled")]
    [TestCase(CdcControllerBoundary.Activation, CdcControllerEdge.Before, "Disabled")]
    [TestCase(CdcControllerBoundary.Activation, CdcControllerEdge.After, "Tracking")]
    public async Task It_retries_exact_empty_binding_after_interrupted_binding_or_activation(
        CdcControllerBoundary boundary,
        CdcControllerEdge edge,
        string lifecycle
    )
    {
        bool interrupted = false;
        _fixture.Hooks.OnBoundary = e =>
        {
            if (!interrupted && e.Boundary == boundary && e.Edge == edge)
            {
                interrupted = true;
                throw new IOException("injected activation interruption");
            }
        };
        (await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        (await _fixture.ScalarAsync<string>(LifecycleSql, Token)).Should().Be(lifecycle);
        _fixture.Hooks.OnBoundary = _ => { };
        await _fixture.ReopenRuntimeAsync(Token);
        await _fixture.RegisterAsync(Token);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
    }

    [TestCase("unbound-tracking")]
    [TestCase("canonical")]
    [TestCase("cache")]
    [TestCase("work")]
    [TestCase("latch")]
    [TestCase("rebuilding")]
    [TestCase("source-mismatch")]
    public async Task It_rejects_ineligible_live_databases_without_mutating_binding_or_capture(
        string mutation
    )
    {
        bool bound = mutation != "unbound-tracking";
        if (bound)
        {
            Observed(
                await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token)
            );
        }
        if (mutation is "cache" or "work")
        {
            await _fixture.ExecuteAsync(
                "INSERT INTO dms.\"Document\" (\"DocumentId\", \"DocumentUuid\", \"ResourceKeyId\") OVERRIDING SYSTEM VALUE SELECT 42, gen_random_uuid(), \"ResourceKeyId\" FROM dms.\"ResourceKey\" LIMIT 1; DELETE FROM dms.\"DocumentProjectionWork\";",
                Token
            );
        }
        string sql = mutation switch
        {
            "unbound-tracking" =>
                "UPDATE dms.\"DocumentCacheState\" SET \"ProjectionLifecycleState\" = 'Tracking'",
            "rebuilding" =>
                "UPDATE dms.\"DocumentCacheState\" SET \"ProjectionLifecycleState\" = 'Rebuilding'",
            "latch" => "UPDATE dms.\"DocumentCacheState\" SET \"CacheAheadRecoveryRequired\" = true",
            "source-mismatch" =>
                "UPDATE dms.\"DataStoreIdentity\" SET \"SourceIdentity\" = gen_random_uuid()",
            "canonical" =>
                "INSERT INTO dms.\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") SELECT gen_random_uuid(), \"ResourceKeyId\" FROM dms.\"ResourceKey\" LIMIT 1",
            "cache" =>
                "INSERT INTO dms.\"DocumentCache\" (\"DocumentId\", \"DocumentUuid\", \"ProjectName\", \"ResourceName\", \"ResourceVersion\", \"ContentVersion\", \"StreamEtag\", \"LastModifiedAt\", \"DocumentJson\") SELECT 42, \"DocumentUuid\", 'Test', 'School', '1', 1, 'test', now(), '{}' FROM dms.\"Document\" WHERE \"DocumentId\" = 42",
            "work" => "INSERT INTO dms.\"DocumentProjectionWork\" VALUES (42, 1, now(), now())",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await _fixture.ExecuteAsync(sql, Token);
        string before = await _fixture.ScalarAsync<string>(LifecycleSql, Token);
        (await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await _fixture.ScalarAsync<string>(LifecycleSql, Token)).Should().Be(before);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(_fixture.Request.Binding, Token))
            .Status.Should()
            .Be(
                bound
                    ? CoreCdc.CdcControlPlaneOperationStatus.Succeeded
                    : CoreCdc.CdcControlPlaneOperationStatus.BindingMissing
            );
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM pg_replication_slots", Token)).Should().Be(0);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_a_mismatched_binding_without_rewriting_the_existing_generation()
    {
        var r = _fixture.Request;
        Observed(await _fixture.Controllers.Activation.ActivateAsync(r, _fixture.Runtime, Token));
        var request = new CdcDeploymentRequest(
            r.Binding with
            {
                PartitionCount = 2,
            },
            r.DmsSettings,
            r.ProviderSetup,
            r.ConnectEndpoint,
            r.WorkerMetricsEndpoint,
            r.ConnectorPolicy,
            r.WorkerPolicy,
            r.ProviderConnectionProperties,
            r.KafkaClientSecurityProperties,
            r.Timing
        );
        (await _fixture.Controllers.Activation.ActivateAsync(request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(r.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await _fixture.ScalarAsync<string>(LifecycleSql, Token)).Should().Be("Tracking");
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM pg_replication_slots", Token)).Should().Be(0);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_an_existing_slot_when_its_creation_reply_was_lost()
    {
        Observed(
            await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token)
        );
        bool interrupted = false;
        _fixture.Hooks.OnBoundary = e =>
        {
            if (
                !interrupted
                && e.Boundary == CdcControllerBoundary.ProviderProof
                && e.Edge == CdcControllerEdge.After
            )
            {
                interrupted = true;
                throw new IOException("lost provider creation result");
            }
        };
        (await _fixture.Controllers.ProviderSetup.SetupAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM pg_replication_slots", Token)).Should().Be(1);
        _fixture.Hooks.OnBoundary = _ => { };
        await _fixture.ReopenRuntimeAsync(Token);
        (await _fixture.Controllers.ProviderSetup.SetupAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM pg_replication_slots", Token)).Should().Be(1);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_reconciles_a_lost_registration_response_without_recreating_the_connector()
    {
        bool interrupted = false;
        _fixture.Hooks.OnBoundary = e =>
        {
            if (
                !interrupted
                && e.Boundary == CdcControllerBoundary.Registration
                && e.Edge == CdcControllerEdge.After
            )
            {
                interrupted = true;
                throw new IOException("lost registration response");
            }
        };
        await _fixture.RegisterAsync(Token);
        interrupted.Should().BeTrue();
        _fixture
            .Hooks.Trace.Count(e =>
                e.Boundary == CdcControllerBoundary.Registration && e.Edge == CdcControllerEdge.Before
            )
            .Should()
            .Be(1);
        _fixture.Hooks.OnBoundary = _ => { };
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
    }

    [TestCase("queue")]
    [TestCase("barrier")]
    [TestCase("second-observation")]
    [TestCase("handoff")]
    public async Task It_repeats_fresh_readiness_after_an_interrupted_offline_wait(string boundary)
    {
        await _fixture.RegisterAsync(Token);
        bool interrupted = false;
        _fixture.AfterRuntimeCall = name =>
        {
            bool selected = boundary switch
            {
                "queue" => name == nameof(ICdcProjectionRuntime.StartProcessingAsync),
                "barrier" => name == nameof(ICdcProjectionRuntime.CaptureBarrierAsync),
                "second-observation" => name == nameof(ICdcProjectionRuntime.ObserveAsync)
                    && _fixture.Barriers.Count > 0,
                "handoff" => name == nameof(ICdcProjectionRuntime.DisposeAsync),
                _ => false,
            };
            if (!interrupted && selected)
            {
                interrupted = true;
                throw new IOException("interrupted offline wait");
            }
        };
        (
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
        int barriers = _fixture.Barriers.Count;
        _fixture.AfterRuntimeCall = _ => { };
        await _fixture.ReopenRuntimeAsync(Token);
        int providerCalls = _fixture.ProviderModes.Count;
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        _fixture.Barriers.Count.Should().BeGreaterThan(barriers);
        _fixture
            .ProviderModes.Skip(providerCalls)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(m => m == CdcProviderSetupMode.ValidateOnly);
    }

    [Test]
    public async Task It_forbids_initial_retry_after_atomic_publication_intent_loses_its_reply()
    {
        await _fixture.RegisterAsync(Token);
        bool disposed = false;
        bool interrupted = false;
        _fixture.AfterRuntimeCall = name => disposed |= name == nameof(ICdcProjectionRuntime.DisposeAsync);
        _fixture.Hooks.OnBoundary = e =>
        {
            if (
                !interrupted
                && disposed
                && e.Boundary == CdcControllerBoundary.JournalWrite
                && e.Edge == CdcControllerEdge.AfterAtomicReplacement
            )
            {
                interrupted = true;
                throw new IOException("lost publication intent reply");
            }
        };
        (
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        _fixture.Hooks.OnBoundary = _ => { };
        _fixture.AfterRuntimeCall = _ => { };
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeTrue();
        await _fixture.ReopenRuntimeAsync(Token);
        (await _fixture.Controllers.Activation.ActivateAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("unavailable")]
    [TestCase("timeout")]
    [TestCase("expired")]
    [TestCase("cancelled")]
    public async Task It_requires_independent_fresh_lag_even_after_idle_heartbeat_barrier_progress(
        string fault
    )
    {
        await _fixture.RegisterAsync(Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int collections = 0;
        var admission = _fixture.AdmissionWithMetrics(
            async (actual, ct) =>
            {
                Observed(actual);
                collections++;
                if (fault == "cancelled")
                {
                    await cancellation.CancelAsync();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                if (fault == "timeout")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                if (fault == "expired")
                {
                    await Task.Delay(TimeSpan.FromSeconds(11), ct);
                    return actual;
                }
                return new CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable(
                    new(CdcDeploymentComponent.Metrics, CdcDeploymentFailure.Unavailable)
                );
            }
        );
        var request = _fixture.WithTiming(
            new(
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(45),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(10)
            )
        );
        if (fault == "cancelled")
        {
            Func<Task> act = () =>
                admission.PreparePublicationAsync(request, _fixture.Runtime, 60_000, cancellation.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await admission.PreparePublicationAsync(request, _fixture.Runtime, 60_000, cancellation.Token))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        collections.Should().BeGreaterThan(0);
        _fixture.Barriers.Should().NotBeEmpty().And.OnlyContain(b => b.Succeeded);
        (await _fixture.ScalarAsync<long>("SELECT COUNT(*) FROM dms.\"Document\"", Token)).Should().Be(0);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase("lost-create-result")]
    [TestCase("receipt-flushed")]
    [TestCase("receipt-replaced")]
    public async Task It_cannot_reconstruct_initial_ownership_after_creation_receipt_interruption(
        string fault
    )
    {
        string database = "receipt_" + Guid.NewGuid().ToString("N");
        var target = _fixture.Request.TargetIdentity with { InstanceKey = "receipt" };
        var source = _fixture.CreateManagedSource(database);
        bool created = false;
        bool interrupted = false;
        var wrapper = new CreationBoundary(
            source,
            () =>
            {
                created = true;
                if (fault == "lost-create-result")
                {
                    interrupted = true;
                    throw new IOException("lost CREATE result");
                }
            }
        );
        _fixture.Hooks.OnBoundary = e =>
        {
            if (
                !interrupted
                && created
                && e.Boundary == CdcControllerBoundary.JournalWrite
                && e.Edge
                    == (
                        fault == "receipt-flushed"
                            ? CdcControllerEdge.AfterTemporaryFlush
                            : CdcControllerEdge.AfterAtomicReplacement
                    )
            )
            {
                interrupted = true;
                throw new IOException("interrupted receipt persistence");
            }
        };
        Func<Task> provision = () =>
            new CdcManagedDatabaseProvisioning(_fixture.Infrastructure.CreateJournalStore()).ProvisionAsync(
                target,
                wrapper,
                Token,
                purpose: CdcWorkflowPurpose.InitialCdcProvisioning
            );
        await provision.Should().ThrowAsync<Exception>();
        interrupted.Should().BeTrue();
        _fixture.Hooks.OnBoundary = _ => { };
        (
            await _fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM pg_database WHERE datname = '{database}'",
                Token
            )
        )
            .Should()
            .Be(1);
        // Reopen the production journal and use the same real database. Existence/emptiness is not a receipt.
        Func<Task> retry = () =>
            new CdcManagedDatabaseProvisioning(
                new LocalCdcWorkflowJournalStore(_fixture.Infrastructure.StateRoot)
            ).ProvisionAsync(target, source, Token, purpose: CdcWorkflowPurpose.InitialCdcProvisioning);
        await retry.Should().ThrowAsync<CdcManagedProvisioningRecoveryException>();
        await using var session = await new LocalCdcWorkflowJournalStore(
            _fixture.Infrastructure.StateRoot
        ).AcquireAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), Token);
        var journal = await session.ReadAsync(target, Token);
        journal.WriterPublicationAuthorized.Should().BeFalse();
        journal.Operations.Should().ContainSingle(o => o.Effect == CdcWorkflowEffect.CreateDatabase);
        journal.Operations.Single().Completions.Length.Should().Be(fault == "receipt-replaced" ? 1 : 0);
    }

    [Test]
    public async Task It_records_reused_database_without_granting_initial_eligibility()
    {
        var target = _fixture.Request.TargetIdentity with { InstanceKey = "reused" };
        var provisioned = await new CdcManagedDatabaseProvisioning(
            _fixture.Infrastructure.CreateJournalStore()
        ).ProvisionAsync(
            target,
            _fixture.CreateManagedSource(_fixture.Database),
            Token,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        provisioned.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Reused);
        var artifacts = CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms", "edfi.documents", "reused", 1, CoreCdc.CdcProvider.Postgresql)
            )
            .Inventory!;
        var binding = _fixture.Request.Binding with
        {
            InstanceKey = "reused",
            ConnectorName = artifacts.ConnectorName,
            TopicName = artifacts.TopicName,
        };
        var original = _fixture.Request.ProviderSetup;
        var setup = new CdcProviderSetupRequest(
            original.Provider,
            original.Mode,
            original.BoundPhysicalSourceFingerprint,
            original.SetupPrincipal,
            original.ConnectorPrincipal,
            CdcDeploymentRequest.GetProviderArtifactNames(binding),
            original.ArtifactOutput,
            original.ExpectedSourceInventory,
            original.DmsManagedTableInventory,
            databaseExecutor: original.DatabaseExecutor
        );
        var r = _fixture.Request;
        var request = new CdcDeploymentRequest(
            binding,
            r.DmsSettings,
            setup,
            r.ConnectEndpoint,
            r.WorkerMetricsEndpoint,
            r.ConnectorPolicy,
            r.WorkerPolicy,
            r.ProviderConnectionProperties,
            r.KafkaClientSecurityProperties,
            r.Timing
        );
        (await _fixture.Controllers.Activation.ActivateAsync(request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.BindingMissing);
        (await _fixture.ScalarAsync<string>(LifecycleSql, Token)).Should().Be("Disabled");
    }

    private sealed class CreationBoundary(ICdcManagedDatabaseProvisioner inner, Action afterCreate)
        : ICdcManagedDatabaseProvisioner
    {
        public bool CreateDatabase()
        {
            bool result = inner.CreateDatabase();
            afterCreate();
            return result;
        }

        public void ProvisionSchema(bool databaseWasCreated) => inner.ProvisionSchema(databaseWasCreated);

        public void ValidateSchema() => inner.ValidateSchema();

        public Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken) =>
            inner.ReadSourceFingerprintAsync(cancellationToken);
    }

    [Test]
    public async Task It_does_not_report_history_loss_for_a_healthy_streaming_slot()
    {
        await _fixture.RegisterAsync(Token);
        for (int sample = 0; sample < 30; sample++)
        {
            var result = await _fixture.ProbeContinuityAsync(Token);
            result
                .Observation.Continuity.Should()
                .Be(
                    CoreCdc.CdcSourceHistoryContinuity.Healthy,
                    "live range evidence: {0}",
                    _fixture.ContinuitySamples.LastOrDefault()
                );
            await Task.Delay(TimeSpan.FromMilliseconds(250), Token);
        }
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_resumes_the_same_slot_and_processes_wal_written_while_stopped()
    {
        await _fixture.RegisterAsync(Token);
        var connect = _fixture.Infrastructure.Connect;
        var request = _fixture.Request;
        Observed(await connect.StopAsync(request, Token));
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var status = Observed(await connect.ReadStatusAsync(request, ct));
                return status.Runtime.ConnectorState == CoreCdc.CdcConnectorRuntimeState.Stopped
                    && status.Tasks.Count == 0;
            },
            request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );

        var before = await _fixture.ProbeContinuityAsync(Token);
        before.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Healthy);
        var position = Observed(await connect.ReadOffsetEvidenceAsync(request, Token)).Postgresql.LsnProc;
        position.Should().NotBeNull();
        await _fixture.ExecuteAsync(
            "UPDATE dms.\"CdcHeartbeat\" SET \"HeartbeatSequence\" = \"HeartbeatSequence\" + 1, \"HeartbeatAt\" = now() WHERE \"HeartbeatId\" = 1",
            Token
        );
        string wal = await _fixture.ScalarAsync<string>("SELECT pg_current_wal_lsn()::text", Token);
        var target = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(wal).Position!.Value;
        target.Value.Should().BeGreaterThan(unchecked((ulong)position!.Value));
        Observed(await connect.ResumeAsync(request, Token));
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var offset = Observed(await connect.ReadOffsetEvidenceAsync(request, ct));
                return offset.Postgresql.LsnProc is { } lsn && unchecked((ulong)lsn) >= target.Value;
            },
            request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            Token
        );
        (await _fixture.ProbeContinuityAsync(Token))
            .Observation.Continuity.Should()
            .Be(CoreCdc.CdcSourceHistoryContinuity.Healthy);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    private const string LifecycleSql = "SELECT \"ProjectionLifecycleState\" FROM dms.\"DocumentCacheState\"";
}
