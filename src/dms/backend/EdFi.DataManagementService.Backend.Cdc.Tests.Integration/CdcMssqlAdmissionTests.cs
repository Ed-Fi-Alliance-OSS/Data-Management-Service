// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category(CdcControllerCategories.Admission)]
[Category("MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_SqlServer_Controller_Admission
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;

    [SetUp]
    public async Task Setup()
    {
        _timeout = new(TimeSpan.FromMinutes(8));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(CdcProvider.SqlServer, Token);
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
            .CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                CoreCdc.CdcSqlServerProviderPosition.HeartbeatAfterImage(
                    CoreCdc
                        .CdcSqlServerProviderPositionParser.ParseLsn(barrier.SqlServerCommitLsn, "$.commit")
                        .Lsn!.Value,
                    CoreCdc
                        .CdcSqlServerProviderPositionParser.ParseLsn(barrier.SqlServerChangeLsn, "$.change")
                        .Lsn!.Value
                ),
                offset.SqlServer
            )
            .AtOrBeyondBarrier.Should()
            .BeTrue();
        barrier.SqlServerEventSerialNo.Should().Be(2);
        var artifacts = CdcConnectorTemplateBindingArtifacts
            .From(_fixture.Request.Binding, nameof(_fixture))
            .ArtifactInventory;
        string commit = barrier.SqlServerCommitLsn!.Replace(":", "");
        string change = barrier.SqlServerChangeLsn!.Replace(":", "");
        (
            await _fixture.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM cdc.[{artifacts.SqlServerCaptureInstanceCdcHeartbeatName}_CT] WHERE [__$operation] = 4 AND [__$start_lsn] = 0x{commit} AND [__$seqval] = 0x{change}",
                Token
            )
        )
            .Should()
            .Be(1);
        var historyTopic = _fixture.PreRegistrationHistoryTopics.Should().ContainSingle().Subject;
        historyTopic.PartitionReplicas.Should().ContainSingle();
        historyTopic.Configuration["cleanup.policy"].Value.Should().Be("delete");
        historyTopic.Configuration["retention.ms"].Value.Should().Be("-1");
        historyTopic.Configuration["retention.bytes"].Value.Should().Be("-1");
        _fixture
            .Preparation.Should()
            .ContainInOrder(
                "nested-triggers-disabled",
                "mvcc-prepared",
                "projection-prerequisites-validated",
                "runtime-initialized",
                "guarded-activation"
            );
        (await _fixture.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dms.Document", Token)).Should().Be(0);
        var expected = _fixture
            .Request.ProviderSetup.ExpectedSourceInventory.Select(t => t.TableName.Name)
            .Order()
            .ToArray();
        var captured = await _fixture.ScalarAsync<string>(
            "SELECT STRING_AGG(OBJECT_NAME(source_object_id), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(source_object_id)) FROM cdc.change_tables",
            Token
        );
        expected.Should().Equal("CdcHeartbeat", "Document", "DocumentCache");
        captured.Split(',').Should().Equal(expected);
        (
            await _fixture.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID()",
                Token
            )
        )
            .Should()
            .Be(2);
        Observed(await _fixture.Kafka.InspectSchemaHistoryAsync(_fixture.Request, Token))
            .Should()
            .Be(CoreCdc.CdcSqlServerSchemaHistoryState.Valid);
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
                "SET IDENTITY_INSERT dms.Document ON; INSERT INTO dms.Document (DocumentId, DocumentUuid, ResourceKeyId) SELECT TOP (1) 42, NEWID(), ResourceKeyId FROM dms.ResourceKey; SET IDENTITY_INSERT dms.Document OFF; DELETE FROM dms.DocumentProjectionWork;",
                Token
            );
        }
        string sql = mutation switch
        {
            "unbound-tracking" =>
                "UPDATE dms.\"DocumentCacheState\" SET \"ProjectionLifecycleState\" = 'Tracking'",
            "rebuilding" =>
                "UPDATE dms.\"DocumentCacheState\" SET \"ProjectionLifecycleState\" = 'Rebuilding'",
            "latch" => "UPDATE dms.\"DocumentCacheState\" SET \"CacheAheadRecoveryRequired\" = 1",
            "source-mismatch" => "UPDATE dms.\"DataStoreIdentity\" SET \"SourceIdentity\" = NEWID()",
            "canonical" =>
                "INSERT INTO dms.\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") SELECT TOP (1) NEWID(), \"ResourceKeyId\" FROM dms.\"ResourceKey\"",
            "cache" =>
                "INSERT INTO dms.\"DocumentCache\" (\"DocumentId\", \"DocumentUuid\", \"ProjectName\", \"ResourceName\", \"ResourceVersion\", \"ContentVersion\", \"StreamEtag\", \"LastModifiedAt\", \"DocumentJson\") SELECT 42, \"DocumentUuid\", 'Test', 'School', '1', 1, 'test', SYSUTCDATETIME(), '{}' FROM dms.\"Document\" WHERE \"DocumentId\" = 42",
            "work" =>
                "INSERT INTO dms.\"DocumentProjectionWork\" VALUES (42, 1, SYSUTCDATETIME(), SYSUTCDATETIME())",
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
        (
            await _fixture.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM sys.tables WHERE is_tracked_by_cdc = 1",
                Token
            )
        )
            .Should()
            .Be(0);
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
        (
            await _fixture.ScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM sys.tables WHERE is_tracked_by_cdc = 1",
                Token
            )
        )
            .Should()
            .Be(0);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_reconciles_exact_capture_after_lost_provider_evidence_without_repair()
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
                throw new IOException("lost provider evidence reply");
            }
        };
        (await _fixture.Controllers.ProviderSetup.SetupAsync(_fixture.Request, _fixture.Runtime, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        interrupted.Should().BeTrue();
        string before = await _fixture.ScalarAsync<string>(CaptureIdentitySql, Token);
        _fixture.Hooks.OnBoundary = _ => { };
        await _fixture.ReopenRuntimeAsync(Token);
        await _fixture.RegisterAsync(Token, resumeProvider: true);
        (await _fixture.ScalarAsync<string>(CaptureIdentitySql, Token)).Should().Be(before);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        (await _fixture.ScalarAsync<string>(CaptureIdentitySql, Token)).Should().Be(before);
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

    [TestCase("heartbeat-capture")]
    [TestCase("queue")]
    [TestCase("barrier")]
    [TestCase("second-observation")]
    [TestCase("handoff")]
    public async Task It_repeats_fresh_readiness_after_an_interrupted_offline_wait(string boundary)
    {
        await _fixture.RegisterAsync(Token);
        bool interrupted = false;
        _fixture.BeforeRuntimeCall = name =>
        {
            if (
                !interrupted
                && boundary == "heartbeat-capture"
                && name == nameof(ICdcProjectionRuntime.CaptureBarrierAsync)
            )
            {
                interrupted = true;
                throw new IOException("interrupted heartbeat capture entry");
            }
        };
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
        _fixture.BeforeRuntimeCall = _ => { };
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
                Token
            );
        await provision.Should().ThrowAsync<Exception>();
        interrupted.Should().BeTrue();
        _fixture.Hooks.OnBoundary = _ => { };
        (
            await _fixture.ScalarAsync<long>(
                $"SELECT COUNT_BIG(*) FROM sys.databases WHERE name = '{database}'",
                Token
            )
        )
            .Should()
            .Be(1);
        // Reopen the production journal and use the same real database. Existence/emptiness is not a receipt.
        Func<Task> retry = () =>
            new CdcManagedDatabaseProvisioning(
                new LocalCdcWorkflowJournalStore(_fixture.Infrastructure.StateRoot)
            ).ProvisionAsync(target, source, Token);
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
        ).ProvisionAsync(target, _fixture.CreateManagedSource(_fixture.Database), Token);
        provisioned.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Reused);
        var artifacts = CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms", "edfi.documents", "reused", 1, CoreCdc.CdcProvider.SqlServer)
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

    [TestCase("inspect-only")]
    [TestCase("denied-server-authority")]
    public async Task It_rejects_unavailable_projection_prerequisites_before_runtime_or_capture(string fault)
    {
        await _fixture.ExecuteAsync("EXEC sys.sp_configure N'nested triggers', 0; RECONFIGURE;", Token);
        string setupUser = "sa";
        if (fault == "denied-server-authority")
        {
            await _fixture.ExecuteAsync(
                "USE master; CREATE LOGIN cdc_setup WITH PASSWORD = 'EdFi_Dms1!', CHECK_POLICY = OFF; GRANT CREATE ANY DATABASE TO cdc_setup;",
                Token
            );
            setupUser = "cdc_setup";
        }
        string database = "prerequisite_" + Guid.NewGuid().ToString("N");
        var target = _fixture.Request.TargetIdentity with { InstanceKey = "prerequisite" };
        int initializations = _fixture.Preparation.Count(p => p == "runtime-initialized");
        Func<Task> provision = () =>
            new CdcManagedDatabaseProvisioning(_fixture.Infrastructure.CreateJournalStore()).ProvisionAsync(
                target,
                _fixture.CreateManagedSource(
                    database,
                    fault == "inspect-only"
                        ? CdcProjectionPrerequisiteMode.Inspect
                        : CdcProjectionPrerequisiteMode.OwnedLocalSqlServer,
                    setupUser
                ),
                Token
            );
        await provision.Should().ThrowAsync<Exception>();
        (
            await _fixture.ScalarAsync<long>(
                $"SELECT COUNT_BIG(*) FROM sys.databases WHERE name = '{database}' AND is_cdc_enabled = 0 AND is_read_committed_snapshot_on = 1",
                Token
            )
        )
            .Should()
            .Be(1);
        (
            await _fixture.ScalarAsync<long>(
                $"SELECT COUNT_BIG(*) FROM [{database}].sys.tables WHERE schema_id = SCHEMA_ID('dms')",
                Token
            )
        )
            .Should()
            .Be(0);
        (
            await _fixture.ScalarAsync<int>(
                "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'",
                Token
            )
        )
            .Should()
            .Be(0);
        _fixture.Preparation.Count(p => p == "runtime-initialized").Should().Be(initializations);
        _fixture
            .Hooks.Trace.Should()
            .NotContain(e =>
                e.Boundary == CdcControllerBoundary.Activation
                || e.Boundary == CdcControllerBoundary.ProviderProof
                || e.Boundary == CdcControllerBoundary.Registration
            );
        await using var session = await _fixture
            .Infrastructure.CreateJournalStore()
            .AcquireAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), Token);
        var journal = await session.ReadAsync(target, Token);
        journal.WriterPublicationAuthorized.Should().BeFalse();
        journal.Operations.Should().ContainSingle(o => o.Effect == CdcWorkflowEffect.CreateDatabase);
    }

    [TestCase("capture")]
    [TestCase("cleanup")]
    [TestCase("failed-capture")]
    public async Task It_blocks_publication_with_a_stopped_job_without_repairing_capture(string job)
    {
        await _fixture.RegisterAsync(Token);
        string before = await _fixture.ScalarAsync<string>(CaptureIdentitySql, Token);
        bool fail = job == "failed-capture";
        job = fail ? "capture" : job;
        // Disabling the actual Agent job makes stopped state durable even for the scheduled cleanup job.
        await _fixture.ExecuteAsync(
            $"DECLARE @id uniqueidentifier = (SELECT job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = '{job}'); EXEC msdb.dbo.sp_update_job @job_id = @id, @enabled = 0;",
            Token
        );
        if (job == "capture")
        {
            await _fixture.ExecuteAsync("EXEC sys.sp_cdc_stop_job @job_type = N'capture';", Token);
        }
        if (fail)
        {
            await CdcControllerFixture.WaitAsync(
                async ct =>
                    await _fixture.ScalarAsync<int>(
                        "SELECT COUNT(*) FROM msdb.dbo.sysjobactivity WHERE job_id = (SELECT job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = 'capture') AND session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions) AND start_execution_date IS NOT NULL AND stop_execution_date IS NULL",
                        ct
                    ) == 0,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250),
                Token
            );
            await _fixture.ExecuteAsync(
                "DECLARE @id uniqueidentifier = (SELECT job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = 'capture'); EXEC msdb.dbo.sp_update_jobstep @job_id = @id, @step_id = 1, @command = N'THROW 50000, ''injected capture job failure'', 1;', @retry_attempts = 0, @on_fail_action = 2; EXEC msdb.dbo.sp_update_job @job_id = @id, @enabled = 1; EXEC msdb.dbo.sp_start_job @job_id = @id;",
                Token
            );
            await CdcControllerFixture.WaitAsync(
                async ct =>
                    await _fixture.ScalarAsync<int>(
                        "SELECT COUNT(*) FROM msdb.dbo.sysjobhistory WHERE job_id = (SELECT job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = 'capture') AND step_id = 0 AND run_status = 0",
                        ct
                    ) > 0,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250),
                Token
            );
        }
        var observedProvider = await _fixture.InspectProviderAsync(Token);
        var mapped = CdcProviderSetupResultMapper.MapValidateOnlyResult(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            _fixture.Request.Binding,
            observedProvider
        );
        var jobs = mapped.ProviderHistory!.SqlServerJobs!;
        jobs.HasStoppedOrFailedJob.Should().BeTrue();
        jobs.HasMissingJob.Should().BeFalse();
        // The existing mapper gives a non-running capture job Stopped precedence over its failed last run.
        (job == "capture" ? jobs.CaptureJobState : jobs.CleanupJobState)
            .Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Stopped);
        if (fail)
        {
            observedProvider
                .Diagnostics.Should()
                .Contain(d => d.Code == "CDC_SQLSERVER_CDC_JOB_LAST_RUN_FAILED");
        }
        var request = _fixture.WithTiming(
            new(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(20)
            )
        );
        (
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                request,
                _fixture.Runtime,
                60_000,
                Token
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
        (await _fixture.ScalarAsync<string>(CaptureIdentitySql, Token)).Should().Be(before);
        (
            await _fixture.ScalarAsync<int>(
                $"SELECT CONVERT(int, enabled) FROM msdb.dbo.sysjobs WHERE job_id = (SELECT job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = '{job}')",
                Token
            )
        )
            .Should()
            .Be(fail ? 1 : 0);
    }

    [Test]
    public async Task It_retains_terminal_schema_history_loss_after_the_topic_is_healthy_again()
    {
        await _fixture.RegisterAsync(Token);
        var artifacts = CdcConnectorTemplateBindingArtifacts
            .From(_fixture.Request.Binding, nameof(_fixture))
            .ArtifactInventory;
        using var admin = new Confluent.Kafka.AdminClientBuilder(
            new Confluent.Kafka.AdminClientConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            }
        ).Build();
        var partition = new Confluent.Kafka.TopicPartition(artifacts.SchemaHistoryTopicName!, 0);
        // Keep genuine records so restoring a nonempty topic below does not fabricate provider/schema evidence.
        using var consumer = new Confluent.Kafka.ConsumerBuilder<byte[], byte[]>(
            new Confluent.Kafka.ConsumerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
                GroupId = "history-probe-" + Guid.NewGuid().ToString("N"),
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
            }
        ).Build();
        var watermark = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10));
        watermark.High.Value.Should().BeGreaterThan(0);
        consumer.Assign(new Confluent.Kafka.TopicPartitionOffset(partition, watermark.Low));
        List<Confluent.Kafka.Message<byte[], byte[]>> retained = [];
        for (long index = watermark.Low.Value; index < watermark.High.Value; index++)
        {
            var record = consumer.Consume(TimeSpan.FromSeconds(10));
            record.Should().NotBeNull();
            retained.Add(record!.Message);
        }
        consumer.Unassign();
        await admin.DeleteRecordsAsync(
            [new(partition, watermark.High)],
            new() { OperationTimeout = TimeSpan.FromSeconds(15) }
        );
        Observed(await _fixture.Kafka.InspectSchemaHistoryAsync(_fixture.Request, Token))
            .Should()
            .Be(CoreCdc.CdcSqlServerSchemaHistoryState.RequiredRecordLost);
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
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
        await _fixture.ReopenRuntimeAsync(Token);
        var target = new CdcControllerStatusTarget(_fixture.Request, _fixture.Runtime, 60_000);
        var lost = (await _fixture.Controllers.Status.StatusAsync([target], Token)).Targets.Single();
        lost.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        lost.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        // Deliberate test-only repair cannot erase the already latched incident.
        await admin.DeleteTopicsAsync(
            [partition.Topic],
            new() { OperationTimeout = TimeSpan.FromSeconds(15) }
        );
        await CdcControllerFixture.WaitAsync(
            async ct =>
                (await _fixture.Kafka.InspectTopicAsync(_fixture.Request, partition.Topic, ct)).State
                == CdcTransportEvidenceState.Absent,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(250),
            Token
        );
        await admin.CreateTopicsAsync([
            new Confluent.Kafka.Admin.TopicSpecification
            {
                Name = partition.Topic,
                NumPartitions = 1,
                ReplicationFactor = 1,
                Configs = new()
                {
                    ["cleanup.policy"] = "delete",
                    ["retention.ms"] = "-1",
                    ["retention.bytes"] = "-1",
                    ["max.message.bytes"] = "1000000",
                    ["min.insync.replicas"] = "1",
                },
            },
        ]);
        using var producer = new Confluent.Kafka.ProducerBuilder<byte[], byte[]>(
            new Confluent.Kafka.ProducerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            }
        ).Build();
        foreach (var message in retained)
        {
            await producer.ProduceAsync(partition, message, Token);
        }
        Observed(await _fixture.Kafka.InspectSchemaHistoryAsync(_fixture.Request, Token))
            .Should()
            .Be(CoreCdc.CdcSqlServerSchemaHistoryState.Valid);
        var later = (await _fixture.Controllers.Status.StatusAsync([target], Token)).Targets.Single();
        later
            .Details.IncidentFailureCategory.Should()
            .Be(lost.Details.IncidentFailureCategory)
            .And.NotBeNull();
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
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_cannot_publish_when_schema_history_inspection_is_unavailable()
    {
        await _fixture.RegisterAsync(Token);
        int unavailable = 0;
        var kafka = _fixture.Hooks.Decorate<ICdcKafkaAdminAdapter>(
            _fixture.Kafka,
            name =>
            {
                if (name == nameof(ICdcKafkaAdminAdapter.InspectSchemaHistoryAsync))
                {
                    unavailable++;
                    throw new IOException("injected unavailable schema history observation");
                }
                return CdcControllerBoundary.Observation;
            }
        );
        (
            await _fixture
                .AdmissionWithKafka(kafka)
                .PreparePublicationAsync(_fixture.Request, _fixture.Runtime, 60_000, Token)
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        unavailable.Should().BeGreaterThan(0);
        _fixture.Barriers.Should().NotBeEmpty();
        (await _fixture.JournalAsync(Token)).WriterPublicationAuthorized.Should().BeFalse();
    }

    private const string LifecycleSql = "SELECT ProjectionLifecycleState FROM dms.DocumentCacheState";
    private const string CaptureIdentitySql =
        "SELECT STRING_AGG(CONCAT(capture_instance, ':', object_id, ':', CONVERT(varchar(40), create_date, 126)), ',') WITHIN GROUP (ORDER BY capture_instance) FROM cdc.change_tables";
}
