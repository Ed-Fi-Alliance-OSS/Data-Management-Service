// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

/// <summary>
/// Fixed provisioning evidence plus production offset/runtime mapping and provider barrier observation.
/// No database capture, provisioning workflow, or broker acknowledgement is simulated here.
/// </summary>
internal sealed class MessageContractAdmissionFixture : IDisposable
{
    public const string OperationId = "message-contract-admission";
    public const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";
    public const string OtherFingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Catalog = "contract_datastore";
    public const string CommitLsn = "00000027:00000c78:0003";
    public const string ChangeLsn = "00000027:00000c78:0002";
    public static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset FirstAt = Now.AddSeconds(-8);
    public static readonly DateTimeOffset CaptureAt = Now.AddSeconds(-7);
    public static readonly DateTimeOffset OffsetAt = Now.AddSeconds(-6);
    public static readonly DateTimeOffset BarrierAt = Now.AddSeconds(-5);
    public static readonly DateTimeOffset HistoryAt = Now.AddSeconds(-4);
    public static readonly DateTimeOffset SecondAt = Now.AddSeconds(-3);

    private readonly Clock _clock = new();
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly ICdcConnectorObservationMapper _mapper;
    private readonly ICdcProviderSourcePositionAdapter _positions;

    private readonly string _catalog = Catalog;
    public CdcBinding Binding { get; }
    public CdcArtifactInventory Inventory { get; }
    public CdcTargetIdentity Target => Binding.ToTargetIdentity();
    public CdcObservationContext Context => new(OperationId, Target, Binding.PhysicalSourceFingerprint);
    public string PartitionHash =>
        CdcSourcePartitionHashCalculator.Compute(Binding.Provider, Inventory.ConnectorName, _catalog).Hash!;
    public string ValidOffset =>
        Binding.Provider == CdcProvider.Postgresql
            ? """{"lsn_proc":42,"snapshot":false}"""
            : $$"""{"commit_lsn":"{{CommitLsn}}","change_lsn":"{{ChangeLsn}}","event_serial_no":2,"snapshot":false}""";

    public MessageContractAdmissionFixture(CdcProvider provider)
    {
        Inventory = CdcArtifactNameGenerator
            .Render(new("dms-local", "edfi.dms", "contract-store", 1, provider))
            .Inventory!;
        Binding = new(
            1,
            "dms-local",
            "default",
            "1",
            "contract-store",
            1,
            provider,
            SourceFingerprint,
            Inventory.ConnectorName,
            Inventory.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            1
        );
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = provider == CdcProvider.Postgresql ? "postgresql" : "mssql",
                }
            )
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddDmsCdcControl(configuration);
        _services = services.BuildServiceProvider();
        _scope = _services.CreateScope();
        _mapper = _scope.ServiceProvider.GetRequiredService<ICdcConnectorObservationMapper>();
        _positions = _scope.ServiceProvider.GetRequiredService<ICdcProviderSourcePositionAdapter>();
    }

    // Live broker fixtures reuse the same focused prerequisites, with the actual binding and partition identity.
    public MessageContractAdmissionFixture(
        CdcProvider provider,
        CdcBinding binding,
        CdcArtifactInventory inventory,
        string catalog
    )
        : this(provider)
    {
        Binding = binding;
        Inventory = inventory;
        _catalog = catalog;
    }

    public CdcProjectionCorrelationObservation ObserveFocusedProjection() =>
        Projection(DateTimeOffset.UtcNow);

    // The production mapper/adapter consume real offset and task observations. Other admission
    // prerequisites stay focused test inputs; this is not a provider-history or projector workflow.
    public CdcInitialAdmissionEvaluationInput ObserveLiveProgress(
        CdcConnectorOffsetEntry entry,
        string connectorState,
        IReadOnlyList<string> taskStates,
        CdcProviderBarrierCaptureResult capture,
        DateTimeOffset firstProjectionAt
    )
    {
        CdcInitialAdmissionEvaluationInput input = ValidInput();
        _clock.UtcNow = DateTimeOffset.UtcNow;
        var offsets = new CdcConnectResult<CdcConnectorOffsets>(
            CdcConnectOutcome.Succeeded,
            new([entry]),
            null
        );
        CdcConnectorOffsetObservation offset = _mapper.MapOffset(Context, Binding, _catalog, offsets);
        CdcConnectorRuntimeObservation runtime = _mapper.MapRuntime(
            Context,
            Binding,
            new(
                CdcConnectOutcome.Succeeded,
                new(
                    connectorState,
                    taskStates.Select((state, id) => new CdcConnectorTaskStatus(id, state, null)).ToArray()
                ),
                null
            ),
            offsets
        );
        _clock.UtcNow = DateTimeOffset.UtcNow;
        CdcProviderBarrierObservation barrier = _positions.ObserveProviderBarrier(
            new(OperationId, Binding, firstProjectionAt, capture, offset, PartitionHash)
        );
        DateTimeOffset historyAt = DateTimeOffset.UtcNow;
        DateTimeOffset secondAt = historyAt.AddTicks(1);
        DateTimeOffset now = secondAt.AddTicks(1);
        return input with
        {
            ObservedAt = now,
            NowUtc = now,
            FirstProjectionCaughtUp = Projection(firstProjectionAt),
            ConnectorRuntime = runtime,
            ProviderBarrier = barrier,
            SourceHistory = input.SourceHistory! with { ObservedAt = historyAt },
            SecondProjectionCaughtUp = Projection(secondAt),
            Lag = input.Lag! with { ObservedAt = secondAt },
        };
    }

    public CdcConnectorOffsetEntry Entry(string offset, string server = "", string database = "")
    {
        Dictionary<string, string> partition = new()
        {
            ["server"] = server.Length == 0 ? Inventory.ConnectorName : server,
        };
        if (Binding.Provider == CdcProvider.SqlServer)
        {
            partition["database"] = database.Length == 0 ? _catalog : database;
        }
        using JsonDocument document = JsonDocument.Parse(offset);
        return new(JsonSerializer.SerializeToElement(partition), document.RootElement.Clone());
    }

    public CdcConnectorOffsetObservation MapOffset(params CdcConnectorOffsetEntry[] entries)
    {
        _clock.UtcNow = OffsetAt;
        return _mapper.MapOffset(
            Context,
            Binding,
            _catalog,
            new(CdcConnectOutcome.Succeeded, new(entries), null)
        );
    }

    public CdcConnectorRuntimeObservation Runtime(string taskState = "RUNNING")
    {
        _clock.UtcNow = OffsetAt;
        return _mapper.MapRuntime(
            Context,
            Binding,
            new(
                CdcConnectOutcome.Succeeded,
                new("RUNNING", [new(0, taskState, taskState == "FAILED" ? "RecordTooLargeException" : null)]),
                null
            ),
            new(CdcConnectOutcome.Succeeded, new([Entry(ValidOffset)]), null)
        );
    }

    public CdcProviderBarrierObservation Barrier(CdcConnectorOffsetObservation offset, string wal = "0/2A")
    {
        _clock.UtcNow = BarrierAt;
        CdcProviderBarrierCaptureResult capture =
            Binding.Provider == CdcProvider.Postgresql
                ? CdcProviderBarrierCaptureResult.PostgresqlSuccess(wal, CaptureAt)
                : CdcProviderBarrierCaptureResult.SqlServerSuccess(CommitLsn, ChangeLsn, CaptureAt);
        return _positions.ObserveProviderBarrier(
            new(OperationId, Binding, FirstAt, capture, offset, PartitionHash)
        );
    }

    public CdcInitialAdmissionEvaluationInput ValidInput()
    {
        bool sql = Binding.Provider == CdcProvider.SqlServer;
        CdcProviderBarrierObservation barrier = Barrier(MapOffset(Entry(ValidOffset)));
        return new(
            OperationId,
            Now,
            Now,
            Target,
            Binding.PhysicalSourceFingerprint,
            new(
                1,
                "proof-1",
                OperationId,
                Target,
                Binding.Provider,
                "setup-1",
                CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
                CdcWriteAdmissionState.ClosedNeverOpened,
                Now.AddMinutes(-2)
            ),
            new(
                1,
                OperationId,
                Now.AddSeconds(-58),
                Now.AddSeconds(-59),
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                "setup-1",
                "proof-1",
                CdcConsistencyScope.SingleProviderTransaction,
                CdcLifecycleState.Tracking,
                CdcCacheAheadState.Clear,
                false,
                false,
                false,
                "fixed eligibility evidence",
                []
            ),
            new(1, Now.AddSeconds(-30), CdcBindingState.BindingPresent, Binding, null)
        )
        {
            ProviderSetup = new(
                1,
                OperationId,
                FirstAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                CdcProviderSetupMode.ValidateOnly,
                CdcProviderSetupOutcome.Satisfied,
                CdcProviderSetupState.Matched,
                CdcProviderSetupState.Matched,
                CdcProviderSetupState.Matched,
                CdcProviderSetupState.Matched,
                []
            ),
            KafkaPolicy = new(
                1,
                OperationId,
                FirstAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                CdcKafkaPolicyState.Satisfied,
                "single-node",
                new(Inventory.TopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
                new(Inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
                sql
                    ? new(
                        Inventory.SchemaHistoryTopicName!,
                        CdcKafkaPolicyItemState.Satisfied,
                        1,
                        "delete",
                        1,
                        1
                    )
                    : null,
                new(Inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
                new(Inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
                sql ? new(Inventory.SchemaHistoryTopicName!, CdcKafkaPolicyItemState.Satisfied) : null,
                new(CdcKafkaPolicyItemState.Satisfied, 1_000_000, 2_000_000),
                []
            ),
            ConnectOffsetStore = new(
                1,
                OperationId,
                FirstAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                "worker-1",
                "connect-offsets",
                CdcConnectOffsetStorePolicyState.Satisfied,
                "compact",
                1,
                1,
                CdcConnectOffsetStoreItemState.Satisfied,
                []
            ),
            ConnectorConfig = new(
                1,
                OperationId,
                FirstAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                Inventory.ConnectorName,
                CdcConnectorConfigurationState.Matched,
                Inventory.TopicPrefix,
                1,
                CdcConnectorConfigurationItemState.Matched,
                CdcConnectorConfigurationItemState.Matched,
                CdcConnectorConfigurationItemState.Matched,
                CdcConnectorConfigurationItemState.Matched,
                CdcConnectorConfigurationItemState.Matched,
                CdcConnectorConfigurationItemState.Matched,
                sql
                    ? CdcConnectorConfigurationItemState.Matched
                    : CdcConnectorConfigurationItemState.NotApplicable,
                []
            ),
            ConnectorRuntime = Runtime(),
            FirstProjectionCaughtUp = Projection(FirstAt),
            ProviderBarrier = barrier,
            SourceHistory = new(
                1,
                OperationId,
                HistoryAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                CdcSourceHistoryContinuity.Healthy,
                false,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset,
                new(
                    Inventory.ConnectorName,
                    Inventory.TopicName,
                    Inventory.ProgressTopicName,
                    Inventory.SchemaHistoryTopicName,
                    Inventory.PostgresqlLogicalSlotName,
                    PartitionHash,
                    sql ? null : "0/2A",
                    sql ? CommitLsn : null,
                    sql ? ChangeLsn : null,
                    sql ? 2 : null,
                    sql ? ChangeLsn : "0/29",
                    sql ? CommitLsn : "0/2B",
                    []
                ),
                null,
                sql ? CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission : null,
                sql ? CdcSqlServerSchemaHistoryState.Valid : CdcSqlServerSchemaHistoryState.NotApplicable,
                []
            )
            {
                SqlServerJobs = sql ? CdcSqlServerCdcJobEvidence.Healthy : null,
            },
            SecondProjectionCaughtUp = Projection(SecondAt),
            Lag = new(
                1,
                OperationId,
                SecondAt,
                Target,
                Binding.Provider,
                Binding.PhysicalSourceFingerprint,
                CdcConnectorLagState.WithinThreshold,
                250,
                1000,
                100,
                200,
                400,
                []
            ),
        };
    }

    private CdcProjectionCorrelationObservation Projection(DateTimeOffset at) =>
        new(
            1,
            OperationId,
            at,
            Target,
            Binding.Provider,
            Binding.PhysicalSourceFingerprint,
            at,
            new DocumentCacheStatusTargetKey("", 1),
            CdcProjectionCorrelationState.Matched,
            DocumentCacheOperationalHealthStatus.Operational,
            DocumentCacheStatusReason.None,
            DocumentCacheCaughtUpStatus.CaughtUp,
            DocumentCacheStatusReason.None,
            DocumentCacheStatusQueuePresence.Empty,
            [],
            []
        );

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
