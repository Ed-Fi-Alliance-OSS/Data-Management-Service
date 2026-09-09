// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal abstract class CdcReadinessTestBase(Ddl.CdcProvider provider) : CdcRegistrationTestBase(provider)
{
    protected CdcInitialReadiness _readiness = null!;
    protected ICdcWorkerMetricsTransport _metrics = null!;
    protected ICdcProviderSourcePositionAdapter _positions = null!;
    protected ServiceProvider _positionServices = null!;
    protected int _projectionReads;
    protected int _barriers;
    protected int _disposals;
    protected bool _backlog;
    protected bool _wrongSource;
    protected long _lag;
    protected TelemetryClock _telemetryClock = null!;

    [SetUp]
    public async Task SetupReadiness()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.Clear();
        _projectionReads = _barriers = _disposals = 0;
        _backlog = _wrongSource = false;
        _lag = 1;
        _telemetryClock = new();
        var services = new ServiceCollection().AddLogging();
        if (Provider == Ddl.CdcProvider.Postgresql)
        {
            services.AddPostgresqlDmsCdcControlPlane();
        }
        else
        {
            services.AddMssqlDmsCdcControlPlane();
        }
        _positionServices = services.BuildServiceProvider();
        _positions = _positionServices.GetRequiredService<ICdcProviderSourcePositionAdapter>();
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).Invokes(() => Trace("start"));
        A.CallTo(() => _runtime.DisposeAsync())
            .ReturnsLazily(() =>
            {
                _disposals++;
                Trace("dispose");
                return ValueTask.CompletedTask;
            });
        A.CallTo(() => _runtime.ObserveAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _projectionReads++;
                Trace("projection-" + _projectionReads);
                return Projection();
            });
        A.CallTo(() =>
                _runtime.CaptureBarrierAsync(
                    A<CdcDeploymentRequest>._,
                    A<ICdcProviderSourcePositionAdapter>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                _barriers++;
                Trace("barrier");
                return Provider == Ddl.CdcProvider.Postgresql
                    ? CdcProviderBarrierCaptureResult.PostgresqlSuccess("0/10", DateTimeOffset.UtcNow)
                    : CdcProviderBarrierCaptureResult.SqlServerSuccess(
                        "00000001:00000002:0003",
                        "00000001:00000002:0003",
                        DateTimeOffset.UtcNow
                    );
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("offset");
                var offset = Offsets();
                return Observed(
                    new CdcConnectOffsetEvidence(
                        offset.State,
                        offset.SourcePartitionHash,
                        offset.Postgresql,
                        offset.SqlServer with
                        {
                            EventSerialNo = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo,
                        }
                    )
                    {
                        SourcePartition = offset.SourcePartition,
                    }
                );
            });
        _change = r =>
            r with
            {
                GrantInventory =
                [
                    new(
                        Ddl.CdcPrincipalKind.ConnectorPrincipal,
                        new("connector"),
                        Ddl.CdcProviderArtifactKind.HeartbeatTable,
                        new("CdcHeartbeat"),
                        ["SELECT"],
                        []
                    ),
                ],
                ArtifactInventory =
                [
                    .. r.ArtifactInventory,
                    new(
                        Ddl.CdcProviderArtifactKind.HeartbeatTable,
                        new("CdcHeartbeat"),
                        Ddl.CdcProviderArtifactState.Matched,
                        new Dictionary<string, string>()
                    ),
                ],
                ProviderHistoryObservations = r
                    .ArtifactInventory.Where(a =>
                        a.ArtifactKind
                            is Ddl.CdcProviderArtifactKind.PostgresqlReplicationSlot
                                or Ddl.CdcProviderArtifactKind.SqlServerCaptureInstance
                    )
                    .Select(a => new Ddl.CdcProviderHistoryObservation(
                        a.ArtifactKind,
                        a.SafeArtifactName,
                        new Dictionary<string, string>
                        {
                            ["restart_lsn"] = "0_1",
                            ["confirmed_flush_lsn"] = "0_10",
                            ["current_wal_lsn"] = "0_20",
                            ["wal_status"] = "reserved",
                            ["invalidation_reason"] = "",
                            ["retained_min_lsn"] = "0x00000001000000020001",
                            ["retained_max_lsn"] = "0x00000001000000020004",
                        },
                        Ddl.CdcProviderRetryContinuityClassification.None
                    ))
                    .Concat(
                        Provider == Ddl.CdcProvider.SqlServer
                            ?
                            [
                                new Ddl.CdcProviderHistoryObservation(
                                    Ddl.CdcProviderArtifactKind.ProviderHistory,
                                    new("sqlserver_database_cdc"),
                                    new Dictionary<string, string>
                                    {
                                        ["database_cdc_enabled"] = "True",
                                        ["capture_job_present"] = "True",
                                        ["capture_job_name"] = "capture",
                                        ["capture_job_enabled"] = "True",
                                        ["capture_job_running"] = "True",
                                        ["capture_job_last_run_status"] = "1",
                                        ["cleanup_job_present"] = "True",
                                        ["cleanup_job_name"] = "cleanup",
                                        ["cleanup_job_enabled"] = "True",
                                        ["cleanup_job_running"] = "True",
                                        ["cleanup_job_last_run_status"] = "1",
                                        ["retained_max_lsn"] = "0x00000001000000020004",
                                    },
                                    Ddl.CdcProviderRetryContinuityClassification.None
                                ),
                            ]
                            : []
                    )
                    .ToArray(),
            };
        _metrics = A.Fake<ICdcWorkerMetricsTransport>();
        A.CallTo(() =>
                _metrics.CollectAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcTelemetryObservationPass>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest request, CdcTelemetryObservationPass pass, CancellationToken _) =>
                {
                    Trace("metrics");
                    var now = DateTimeOffset.UtcNow;
                    return Observed(
                        new CdcConnectorTelemetryObservation(
                            pass,
                            _telemetryClock,
                            _telemetryClock.GetTimestamp(),
                            now,
                            now,
                            new(
                                CdcJsonContract.CurrentContractVersion,
                                pass.OperationId,
                                now,
                                request.TargetIdentity,
                                request.Binding.Provider,
                                request.Binding.PhysicalSourceFingerprint,
                                _lag <= pass.ThresholdMilliseconds
                                    ? CdcConnectorLagState.WithinThreshold
                                    : CdcConnectorLagState.Exceeded,
                                _lag,
                                pass.ThresholdMilliseconds,
                                null,
                                null,
                                null,
                                []
                            ),
                            new(null, null, null)
                        )
                    );
                }
            );
        A.CallTo(() => _kafka.InspectSchemaHistoryAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("schema-history");
                return Observed(CdcSqlServerSchemaHistoryState.Valid);
            });
        ResetReadiness();
    }

    [TearDown]
    public async Task TeardownReadiness()
    {
        A.CallTo(() => _runtime.DisposeAsync()).Returns(ValueTask.CompletedTask);
        await _positionServices.DisposeAsync();
    }

    protected void ResetReadiness() =>
        _readiness = new(
            _store,
            _services.GetRequiredService<ICdcBindingLifecycleService>(),
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );

    protected Task<CdcTransportResult<CdcWriterPublicationResult>> ReadyAsync(
        CancellationToken token = default
    ) => _readiness.PreparePublicationAsync(_request, _runtime, 1000, token);

    protected DocumentCacheStatusResponse Projection()
    {
        var now = DateTimeOffset.UtcNow;
        DocumentCacheStatusInventoryComponent valid = new(
            DocumentCacheStatusInventoryStatus.Valid,
            DocumentCacheStatusInventoryReason.None,
            null
        );
        DocumentCacheStatusProviderPrerequisiteComponent prerequisite = new(
            DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            null
        );
        return new(
            now,
            [
                new(
                    new(Target.TenantKey, long.Parse(Target.DataStoreId)),
                    1,
                    now,
                    now,
                    Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "sqlserver",
                    _wrongSource
                        ? "sha256:" + new string('f', 64)
                        : _request.Binding.PhysicalSourceFingerprint,
                    new(
                        DocumentCacheStatusResolutionStatus.Resolved,
                        DocumentCacheStatusResolutionReason.None,
                        now,
                        null
                    ),
                    new(DocumentCacheStatusEligibilityStatus.Eligible, DocumentCacheStatusReason.None, null),
                    new(
                        now,
                        valid,
                        valid,
                        valid,
                        valid,
                        new(
                            DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                            DocumentCacheStatusInventoryReason.None,
                            null
                        )
                    ),
                    new(
                        DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        now,
                        prerequisite,
                        prerequisite
                    ),
                    new(
                        DocumentCacheStatusLifecycleState.Tracking,
                        DocumentCacheStatusAvailability.Available,
                        null
                    ),
                    new(DocumentCacheStatusCacheAheadState.Clear, false, null),
                    new(
                        DocumentCacheOperationalHealthStatus.Operational,
                        DocumentCacheStatusReason.None,
                        null
                    ),
                    new(
                        _backlog
                            ? DocumentCacheCaughtUpStatus.NotCaughtUp
                            : DocumentCacheCaughtUpStatus.CaughtUp,
                        DocumentCacheStatusReason.None,
                        null
                    ),
                    new(
                        _backlog
                            ? DocumentCacheStatusQueuePresence.NotEmpty
                            : DocumentCacheStatusQueuePresence.Empty,
                        null,
                        null,
                        DocumentCacheStatusBacklogEstimate.Unavailable
                    ),
                    new(DocumentCacheStatusExecutionState.WaitingForPoll, now, 0, 0, null, now, null, null),
                    null,
                    null,
                    new(),
                    new(),
                    new(),
                    new(new(1, 100, 1, 1, 1000), new(false, 1), new(1, 5)),
                    new()
                ),
            ]
        );
    }

    protected sealed class TelemetryClock : TimeProvider
    {
        private long _extra;
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + _extra;

        public void Advance(TimeSpan elapsed) => _extra += (long)(elapsed.TotalSeconds * TimestampFrequency);
    }
}
