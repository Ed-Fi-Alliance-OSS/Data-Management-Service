// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Kind = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcGovernedArtifactKind;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class CdcArtifactCleanupTestData
{
    internal const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    internal static CdcArtifactCleanupScope Scope(CdcDeploymentRequest request) =>
        new(
            request,
            request.Binding.ToCompleteBindingIdentity(),
            CoreCdc
                .CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(
                    request.Binding.ToCompleteBindingIdentity()
                )
                .Inventory!.GovernedArtifacts
        );

    internal static CdcDeploymentRequest WithTiming(CdcDeploymentRequest request) =>
        new(
            request.Binding,
            request.DmsSettings,
            request.ProviderSetup,
            request.ConnectEndpoint,
            request.WorkerMetricsEndpoint,
            request.ConnectorPolicy,
            request.WorkerPolicy,
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1))
        );

    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> Row(
        params (string Key, string Value)[] values
    ) => [values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)];
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcArtifactCleanupScope(CdcProvider provider)
{
    private CdcDeploymentRequest _request = null!;
    private CdcArtifactCleanupScope _scope = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(provider);
        _scope = CdcArtifactCleanupTestData.Scope(_request);
    }

    [Test]
    public void It_recovers_the_exact_provider_inventory() =>
        _scope.Inventory.Should().HaveCount(provider == CdcProvider.Postgresql ? 8 : 12);

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("shared")]
    [TestCase("peer")]
    [TestCase("provider")]
    public void It_rejects_modified_inventories(string change)
    {
        var items = _scope.Inventory.ToList();
        switch (change)
        {
            case "missing":
                items.RemoveAt(0);
                break;
            case "duplicate":
                items.Add(items[0]);
                break;
            case "shared":
                items[2] = items[2] with { Name = _request.WorkerPolicy.OffsetStorageTopic.Value };
                break;
            case "peer":
                items[2] = items[2] with { Name = "peer-topic" };
                break;
            case "provider":
                items.Add(new(Kind.PostgresqlLogicalSlot, "foreign-slot"));
                break;
        }
        Action act = () =>
            new CdcArtifactCleanupScope(_request, _request.Binding.ToCompleteBindingIdentity(), items);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_another_generation()
    {
        Action act = () =>
            new CdcArtifactCleanupScope(
                _request,
                (
                    _request.Binding with
                    {
                        Generation = _request.Binding.Generation + 1,
                    }
                ).ToCompleteBindingIdentity(),
                _scope.Inventory
            );
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_does_not_serialize_runtime_secrets() => JsonSerializer.Serialize(_scope).Should().Be("{}");

    [Test]
    public void It_does_not_retain_mutable_caller_inventory()
    {
        var items = _scope.Inventory.ToList();
        var scope = new CdcArtifactCleanupScope(
            _request,
            _request.Binding.ToCompleteBindingIdentity(),
            items
        );
        items.Clear();
        scope.Inventory.Should().NotBeEmpty();
    }
}

[TestFixture(CdcProvider.Postgresql, Kind.PostgresqlLogicalSlot)]
[TestFixture(CdcProvider.Postgresql, Kind.PostgresqlPublication)]
[TestFixture(CdcProvider.SqlServer, Kind.SqlServerCaptureInstanceDocument)]
[TestFixture(CdcProvider.SqlServer, Kind.SqlServerCaptureInstanceDocumentCache)]
[TestFixture(CdcProvider.SqlServer, Kind.SqlServerCaptureInstanceCdcHeartbeat)]
[TestFixture(CdcProvider.SqlServer, Kind.SqlServerCdcGatingRole)]
public class Given_CdcArtifactCleanupProvider(CdcProvider provider, Kind kind)
{
    private ICdcProviderDatabaseExecutor _database = null!;
    private CdcProviderArtifactCleanupAdapter _adapter = null!;
    private CdcArtifactCleanupScope _scope = null!;
    private bool _exists;
    private string _safe = "1";
    private string _source = "";
    private string _authority = "1";
    private List<string> _trace = null!;
    private List<string> _sql = null!;

    [SetUp]
    public void Setup()
    {
        _scope = CdcArtifactCleanupTestData.Scope(
            CdcArtifactCleanupTestData.WithTiming(CdcDeploymentRequestTestData.Request(provider))
        );
        _database = A.Fake<ICdcProviderDatabaseExecutor>(options => options.Strict());
        _adapter = new(_database);
        _exists = true;
        _safe = "1";
        _authority = "1";
        _source = CdcArtifactCleanupTestData.SourceIdentity;
        _trace = [];
        _sql = [];
        A.CallTo(() => _database.QueryAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (string sql, CancellationToken _) =>
                {
                    _sql.Add(sql);
                    if (sql.Contains("cdc:cleanup:source", StringComparison.Ordinal))
                    {
                        _trace.Add("source");
                        return CdcArtifactCleanupTestData.Row(
                            ("source_identity", _source),
                            ("metadata_authority", _authority)
                        );
                    }
                    _trace.Add("inspect");
                    return _exists ? CdcArtifactCleanupTestData.Row(("safe_to_delete", _safe)) : [];
                }
            );
        A.CallTo(() => _database.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .Invokes(
                (string sql, CancellationToken _) =>
                {
                    _trace.Add("delete");
                    _sql.Add(sql);
                    _exists = false;
                }
            )
            .Returns(Task.CompletedTask);
    }

    private Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> Delete(CancellationToken token = default) =>
        _adapter.DeleteAsync(_scope, kind, token);

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_CdcBindingRetirement_inspects_never_reserved_provider_artifacts_without_deletion(
        bool exists
    )
    {
        _exists = exists;
        _scope = new(
            _scope.Request,
            _scope.Request.Binding.ToCompleteBindingIdentity(),
            _scope.Inventory,
            requireAbsence: true
        );
        (await Delete())
            .State.Should()
            .Be(exists ? CdcTransportEvidenceState.Unavailable : CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("source", "inspect");
        _exists.Should().Be(exists);
    }

    [Test]
    public async Task It_reobserves_source_and_absence_after_deletion()
    {
        var result = await Delete();
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("source", "inspect", "delete", "source", "inspect");
    }

    [Test]
    public async Task It_returns_the_exact_typed_artifact()
    {
        var result = (CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Observed)await Delete();
        result.Value.ArtifactName.Should().Be(_scope.Inventory.Single(item => item.Kind == kind).Name);
        result.Value.ArtifactKind.Should().Be(kind);
        result.Value.CleanupState.Should().Be(CoreCdc.CdcCleanupState.Deleted);
    }

    [Test]
    public async Task It_reconciles_already_absent_artifacts_without_deletion()
    {
        _exists = false;
        var result = (CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Observed)await Delete();
        result.Value.CleanupState.Should().Be(CoreCdc.CdcCleanupState.NotFound);
        _trace.Should().Equal("source", "inspect");
    }

    [TestCase("0")]
    [TestCase("")]
    [TestCase("unknown")]
    public async Task It_rejects_shared_active_or_unproven_artifacts(string safe)
    {
        _safe = safe;
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("delete");
    }

    [TestCase("86a7cc04-64cf-4b34-b66f-a7b9b4f6b6fd", "1")]
    [TestCase("invalid-secret", "1")]
    [TestCase(CdcArtifactCleanupTestData.SourceIdentity, "0")]
    public async Task It_never_accepts_absence_from_wrong_source_or_filtered_catalogs(
        string source,
        string authority
    )
    {
        _source = source;
        _authority = authority;
        _exists = false;
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("source");
    }

    [Test]
    public async Task It_reconciles_timeout_after_commit()
    {
        A.CallTo(() => _database.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                _exists = false;
                _trace.Add("delete");
            })
            .ThrowsAsync(new TimeoutException("secret"));
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("source", "inspect", "delete", "source", "inspect");
    }

    [Test]
    public async Task It_keeps_failed_deletion_resumable()
    {
        A.CallTo(() => _database.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("secret"));
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _exists.Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_lost_readback_without_leaking_exception_text()
    {
        A.CallTo(() => _database.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .Invokes(() =>
                A.CallTo(() => _database.QueryAsync(A<string>._, A<CancellationToken>._))
                    .ThrowsAsync(new UnauthorizedAccessException("secret-private-source"))
            )
            .Returns(Task.CompletedTask);
        var result = await Delete();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.AuthenticationFailed);
        JsonSerializer.Serialize(result).Should().NotContain("secret-private-source");
    }

    [Test]
    public async Task It_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => Delete(cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_bounds_a_stalled_catalog_call()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<IReadOnlyDictionary<string, string?>>>();
        A.CallTo(() => _database.QueryAsync(A<string>._, A<CancellationToken>._)).Returns(pending.Task);
        try
        {
            (await Delete())
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Failure.Should()
                .Be(CdcDeploymentFailure.Timeout);
        }
        finally
        {
            pending.SetResult([]);
        }
    }

    [Test]
    public async Task It_rejects_a_nonprovider_artifact_without_calls()
    {
        (await _adapter.DeleteAsync(_scope, Kind.PublicTopic, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_ambient_transactions_before_opening_a_production_connection()
    {
        var factory = A.Fake<System.Data.Common.DbProviderFactory>(options => options.Strict());
        _adapter = new(factory, "private-source-secret");
        using var transaction = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled
        );
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() => factory.CreateConnection()).MustNotHaveHappened();
    }

    [Test]
    public async Task It_preserves_cancellation_during_inspection()
    {
        var entered = new TaskCompletionSource();
        var pending = new TaskCompletionSource<IReadOnlyList<IReadOnlyDictionary<string, string?>>>();
        A.CallTo(() => _database.QueryAsync(A<string>._, A<CancellationToken>._))
            .Invokes(() => entered.SetResult())
            .Returns(pending.Task);
        using var cancellation = new CancellationTokenSource();
        Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> running = Delete(cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        try
        {
            Func<Task> act = () => running;
            (await act.Should().ThrowAsync<OperationCanceledException>())
                .Which.CancellationToken.Should()
                .Be(cancellation.Token);
        }
        finally
        {
            pending.SetResult([]);
        }
    }

    [Test]
    public async Task It_keeps_source_and_shared_state_out_of_drop_statements()
    {
        await Delete();
        _sql.Should()
            .NotContain(sql =>
                sql.Contains("sp_cdc_disable_db", StringComparison.Ordinal)
                || sql.Contains("DROP USER", StringComparison.Ordinal)
                || sql.Contains("CASCADE", StringComparison.Ordinal)
                || sql.Contains("@capture_instance=N'all'", StringComparison.Ordinal)
                || sql.Contains("pg_terminate_backend", StringComparison.Ordinal)
            );
    }
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcArtifactCleanupConnect(CdcProvider provider)
{
    private ICdcConnectTransport _connect = null!;
    private CdcConnectArtifactCleanupAdapter _adapter = null!;
    private CdcArtifactCleanupScope _scope = null!;
    private List<string> _trace = null!;
    private bool _exists;
    private bool _stopped;
    private bool _empty;

    [SetUp]
    public void Setup()
    {
        _scope = CdcArtifactCleanupTestData.Scope(
            CdcArtifactCleanupTestData.WithTiming(CdcDeploymentRequestTestData.Request(provider))
        );
        _connect = A.Fake<ICdcConnectTransport>(options => options.Strict());
        _adapter = new(_connect);
        _trace = [];
        _exists = true;
        _stopped = false;
        _empty = false;
        A.CallTo(() => _connect.ReadConfigurationAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("config");
                return _exists
                    ? (CdcTransportResult<IReadOnlyDictionary<string, string>>)
                        new CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed(
                            new Dictionary<string, string>()
                        )
                    : new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent();
            });
        A.CallTo(() => _connect.StopAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("stop");
                _stopped = true;
                return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
            });
        A.CallTo(() => _connect.ReadStatusAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("status");
                return new CdcTransportResult<CdcConnectStatus>.Observed(Status());
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("offsets");
                using var json = JsonDocument.Parse(_empty ? "{\"offsets\":[]}" : "{\"offsets\":[{}]}");
                return new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(
                    CdcConnectOffsetEvidence.Parse(_scope.Request, json.RootElement)
                );
            });
        A.CallTo(() => _connect.DeleteOffsetsAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("reset");
                _empty = true;
                return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
            });
        A.CallTo(() => _connect.DeleteAsync(_scope.Request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _trace.Add("delete");
                _exists = false;
                return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
            });
    }

    private CdcConnectStatus Status() =>
        new(
            new(
                1,
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow,
                _scope.Request.TargetIdentity,
                _scope.Request.Binding.Provider,
                _scope.Request.Binding.PhysicalSourceFingerprint,
                _scope.Request.Binding.ConnectorName,
                _stopped
                    ? CoreCdc.CdcConnectorRuntimeState.Stopped
                    : CoreCdc.CdcConnectorRuntimeState.Running,
                0,
                0,
                CoreCdc.CdcConnectorRuntimeState.Stopped,
                CoreCdc.CdcConnectorSnapshotState.NotApplicable,
                null,
                null,
                []
            ),
            "private-worker",
            []
        );

    private Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> Delete(Kind kind) =>
        _adapter.DeleteAsync(_scope, kind, CancellationToken.None);

    [Test]
    public async Task It_verifies_stop_then_removes_and_reobserves_offsets()
    {
        (await Delete(Kind.ConnectSourceOffsets)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("config", "stop", "status", "reset", "offsets", "status");
        _exists.Should().BeTrue();
    }

    [Test]
    public async Task It_leaves_a_durable_checkpoint_opportunity_between_offsets_and_connector()
    {
        await Delete(Kind.ConnectSourceOffsets);
        _trace.Clear();
        (await Delete(Kind.KafkaConnectConnector)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("config", "stop", "status", "offsets", "status", "delete", "config");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_CdcBindingRetirement_inspects_never_reserved_connectors_without_mutation(bool exists)
    {
        _exists = exists;
        _scope = new(
            _scope.Request,
            _scope.Request.Binding.ToCompleteBindingIdentity(),
            _scope.Inventory,
            requireAbsence: true
        );
        (await Delete(Kind.KafkaConnectConnector))
            .State.Should()
            .Be(exists ? CdcTransportEvidenceState.Unavailable : CdcTransportEvidenceState.Observed);
        _trace.Should().Equal("config");
        _exists.Should().Be(exists);
    }

    [Test]
    public async Task It_never_deletes_a_connector_with_unremoved_offsets()
    {
        (await Delete(Kind.KafkaConnectConnector)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("delete").And.NotContain("reset");
    }

    [Test]
    public async Task It_never_infers_offset_absence_from_missing_connector()
    {
        _exists = false;
        (await Delete(Kind.ConnectSourceOffsets)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().Equal("config");
    }

    [Test]
    public async Task It_reports_absent_connector_only_as_connector_evidence()
    {
        _exists = false;
        var result = (CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Observed)
            await Delete(Kind.KafkaConnectConnector);
        result.Value.ArtifactKind.Should().Be(Kind.KafkaConnectConnector);
        result.Value.CleanupState.Should().Be(CoreCdc.CdcCleanupState.NotFound);
    }

    [TestCase(Kind.ConnectSourceOffsets)]
    [TestCase(Kind.KafkaConnectConnector)]
    public async Task It_rejects_acknowledged_but_unverified_shutdown(Kind kind)
    {
        A.CallTo(() => _connect.StopAsync(_scope.Request, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new()));
        (await Delete(kind)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("delete").And.NotContain("reset");
    }

    [Test]
    public async Task It_reconciles_reset_timeout_after_commit()
    {
        A.CallTo(() => _connect.DeleteOffsetsAsync(_scope.Request, A<CancellationToken>._))
            .Invokes(() => _empty = true)
            .ThrowsAsync(new TimeoutException("secret"));
        (await Delete(Kind.ConnectSourceOffsets)).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [Test]
    public async Task It_reconciles_delete_timeout_after_commit()
    {
        _empty = true;
        A.CallTo(() => _connect.DeleteAsync(_scope.Request, A<CancellationToken>._))
            .Invokes(() => _exists = false)
            .ThrowsAsync(new TimeoutException("secret"));
        (await Delete(Kind.KafkaConnectConnector)).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [Test]
    public async Task It_rejects_task_restart_after_reset()
    {
        A.CallTo(() => _connect.DeleteOffsetsAsync(_scope.Request, A<CancellationToken>._))
            .Invokes(() =>
            {
                _empty = true;
                _stopped = false;
            })
            .Returns(new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new()));
        (await Delete(Kind.ConnectSourceOffsets)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_rejects_offset_lookup_authentication_failure()
    {
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_scope.Request, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.AuthenticationFailed)
                )
            );
        (await Delete(Kind.ConnectSourceOffsets)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("delete");
    }

    [Test]
    public async Task It_rejects_foreign_artifact_kinds_without_transport_effects()
    {
        (await Delete(Kind.PublicTopic)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }
}

[TestFixture]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix permissions.")]
public class Given_CdcArtifactCleanupDatabaseJobs
{
    private string _root = "";
    private LocalCdcWorkflowJournalStore _store = null!;
    private CdcArtifactCleanupScope _scope = null!;
    private CdcProviderArtifactCleanupAdapter _adapter = null!;
    private ICdcProviderDatabaseExecutor _database = null!;
    private Guid _workflow;
    private List<string> _jobs = null!;
    private List<string> _deleted = null!;
    private bool _loseResponse;
    private bool _rejectCleanup;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-cleanup-jobs-" + Guid.NewGuid().ToString("N"));
        _scope = CdcArtifactCleanupTestData.Scope(
            CdcArtifactCleanupTestData.WithTiming(CdcDeploymentRequestTestData.Request(CdcProvider.SqlServer))
        );
        _store = new(_root);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_scope.Request.Binding.PhysicalSourceFingerprint);
        _workflow = (
            await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                _scope.Request.TargetIdentity,
                provisioner,
                purpose: CdcWorkflowPurpose.InitialCdcProvisioning
            )
        ).WorkflowId;
        _database = A.Fake<ICdcProviderDatabaseExecutor>(options => options.Strict());
        _adapter = new(_database);
        _jobs = ["capture", "cleanup"];
        _deleted = [];
        _loseResponse = false;
        _rejectCleanup = false;
        A.CallTo(() => _database.QueryAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (string sql, CancellationToken _) =>
                    sql.Contains("cdc:cleanup:source", StringComparison.Ordinal)
                        ? CdcArtifactCleanupTestData.Row(
                            ("source_identity", CdcArtifactCleanupTestData.SourceIdentity),
                            ("metadata_authority", "1")
                        )
                        : _jobs
                            .Select(job =>
                                (IReadOnlyDictionary<string, string?>)
                                    new Dictionary<string, string?>
                                    {
                                        ["job_type"] = job,
                                        ["job_id"] =
                                            job == "capture"
                                                ? "11111111-1111-1111-1111-111111111111"
                                                : "22222222-2222-2222-2222-222222222222",
                                    }
                            )
                            .ToArray()
            );
        A.CallTo(() => _database.ExecuteNonQueryAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (string sql, CancellationToken _) =>
                {
                    string job = sql.Contains("N'capture'", StringComparison.Ordinal) ? "capture" : "cleanup";
                    _deleted.Add(job);
                    if (!_rejectCleanup || job != "cleanup")
                    {
                        _jobs.Remove(job);
                    }
                    return _loseResponse
                        ? Task.FromException(new TimeoutException("secret"))
                        : Task.CompletedTask;
                }
            );
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_CdcBindingRetirement_inspects_never_reserved_jobs_without_deletion(bool exists)
    {
        if (!exists)
        {
            _jobs.Clear();
        }
        _scope = new(
            _scope.Request,
            _scope.Request.Binding.ToCompleteBindingIdentity(),
            _scope.Inventory,
            requireAbsence: true
        );
        (await Delete())
            .State.Should()
            .Be(exists ? CdcTransportEvidenceState.Unavailable : CdcTransportEvidenceState.Observed);
        _deleted.Should().BeEmpty();
    }

    private async Task<CdcTransportResult<CdcTransportAcknowledgement>> Delete(bool retire = true)
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        if (retire)
        {
            await session.RecordIntentAsync(
                _scope.Request.TargetIdentity,
                _workflow,
                Guid.NewGuid(),
                CdcWorkflowEffect.Retire,
                [],
                CancellationToken.None
            );
        }
        return await _adapter.DeleteOwnedSqlServerJobsAsync(_scope, session, CancellationToken.None);
    }

    [Test]
    public async Task It_requires_durable_retirement_intent_before_database_wide_effects()
    {
        (await Delete(false)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _deleted.Should().BeEmpty();
    }

    [Test]
    public async Task It_deletes_only_the_two_current_database_jobs()
    {
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _deleted.Should().Equal("capture", "cleanup");
    }

    [Test]
    public async Task It_reconciles_lost_responses_independently()
    {
        _loseResponse = true;
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _jobs.Should().BeEmpty();
    }

    [Test]
    public async Task It_keeps_partial_cleanup_resumable()
    {
        _rejectCleanup = true;
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _jobs.Should().Equal("cleanup");
        _deleted.Clear();
        _rejectCleanup = false;
        (await Delete(false)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _deleted.Should().Equal("cleanup");
    }

    [Test]
    public async Task It_rejects_unknown_database_jobs_without_mutation()
    {
        _jobs.Add("peer");
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _deleted.Should().BeEmpty();
    }

    [Test]
    public async Task It_never_counts_capture_catalog_failure_as_absence()
    {
        A.CallTo(() =>
                _database.QueryAsync(
                    A<string>.That.Contains("cdc:cleanup:database-jobs"),
                    A<CancellationToken>._
                )
            )
            .ThrowsAsync(new UnauthorizedAccessException("secret"));
        (await Delete()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _deleted.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_missing_ownership_history()
    {
        Directory.Delete(_root, true);
        (await Delete(false)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _deleted.Should().BeEmpty();
    }
}
