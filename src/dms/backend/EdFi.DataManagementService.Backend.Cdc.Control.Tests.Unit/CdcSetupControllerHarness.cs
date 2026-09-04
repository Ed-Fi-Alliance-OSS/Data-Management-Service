// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Drives <see cref="CdcSetupController"/> against faked collaborators. Every default is the state of
/// a first enablement on a freshly created, empty, disabled database whose target the projector is
/// configured for, and whose connector, broker, and provider all answer as the sequence requires; each
/// test changes exactly the one fact it is about.
/// </summary>
/// <remarks>
/// The clock advances on every read, because the enablement sequence's own ordering rules are about
/// evidence observed in order: the barrier is captured after the first caught-up observation, the
/// connector commits past it, and continuity and the second caught-up observation follow. A frozen
/// clock could not express that sequence, so the collaborators that stamp evidence read this clock too.
/// </remarks>
internal sealed class CdcSetupControllerHarness
{
    public const string OperationId = "operation-1";
    public const string SetupControllerRunId = "run-1";
    public const string ConnectionString = "instance-database";
    public const string TenantKey = "";
    public const long DataStoreId = 1;
    public const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    /// <summary>The provider position captured as the barrier: the start of the slot's retained range.</summary>
    public const string BarrierLsn = "0/16B6C50";

    /// <summary>The connector's committed position: past the barrier and inside the retained range.</summary>
    public const long CommittedLsnProc = 0x16B6C60;

    /// <summary>The SQL Server barrier: the heartbeat after-image the capture pass wrote.</summary>
    public const string SqlServerBarrierCommitLsn = "00000027:00000c78:0002";

    public const string SqlServerBarrierChangeLsn = "00000027:00000c78:0001";

    /// <summary>The connector's committed SQL Server position: past the captured barrier.</summary>
    public const string SqlServerCommittedCommitLsn = "00000027:00000c78:0003";

    public const string SqlServerCommittedChangeLsn = "00000027:00000c78:0002";

    /// <summary>The catalog the SQL Server connector reads, which its Connect source partition includes.</summary>
    public const string SqlServerCatalogName = "edfi_datastore";

    public static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The connector-template service is the real one: the enablement renders the connector, validates it
    /// before registration, and validates the live read-back through it, and a fake would only restate
    /// the rules it owns.
    /// </summary>
    private static readonly ServiceProvider _templateServices = new ServiceCollection()
        .AddCdcConnectorTemplates()
        .BuildServiceProvider();

    private readonly AdvancingTimeProvider _clock = new(Now, TimeSpan.FromSeconds(1));

    private readonly CoreCdc.CdcProvider _provider;

    private readonly Ddl.CdcProvider _ddlProvider;

    private bool _barrierReached;

    /// <param name="provider">
    /// The provider the enablement runs against. It selects the artifact names, the provider position
    /// shapes, and the schema-history evidence SQL Server alone carries; every other default is the same.
    /// </param>
    public CdcSetupControllerHarness(CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql)
    {
        _provider = provider;
        _ddlProvider = ToDdlProvider(provider);

        ProjectionStatus = ProjectingTheTarget(provider: provider);
        ConnectorConfigReadBack = RenderedConnectorConfig(provider: provider);
        CommittedOffsets = CommittedStreamingOffsets(provider);

        A.CallTo(() => Probe.Provider).Returns(provider);
        // Stamped from the advancing clock, not from the frozen `Now`: the production probe stamps its
        // observation when the database answered, which is always later than the instant the operation
        // started. Freezing it here would let a caller classify against its own entry clock and still
        // pass, while the same code refused every real enablement as future-dated.
        A.CallTo(() => Probe.ProbeAsync(A<CdcEligibilityProbeRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcEligibilityProbeRequest request, CancellationToken _) =>
                    Task.FromResult(
                        CdcEligibilityObservationMapper.Map(
                            request.Context,
                            request.Proof,
                            ActivationCompleted ? PostActivationEligibility : Eligibility,
                            _clock.GetUtcNow()
                        )
                    )
            );

        A.CallTo(() => Projection.CollectAsync(A<CdcObservationContext>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcObservationContext context, CancellationToken _) =>
                    Task.FromResult(
                        CdcProjectionCorrelationObservationMapper.Map(
                            context,
                            CurrentProjectionStatus(),
                            _clock.GetUtcNow()
                        )
                    )
            );

        A.CallTo(() => Bindings.ReadBindingAsync(A<CdcBindingIdentity>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBindingIdentity identity, CancellationToken _) =>
                    // A read for any generation other than this target's own is a read of the generation
                    // a source replacement replaces.
                    Task.FromResult(
                        identity.Generation == CdcControlTemplateTestData.BindingGeneration
                            ? BindingRead
                            : PreviousGenerationRead ?? Missing()
                    )
            );
        A.CallTo(() => Bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken _) => Task.FromResult(BindingWrite ?? Present(binding))
            );
        A.CallTo(() => Bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken _) => Task.FromResult(BindingWrite ?? Present(binding))
            );

        A.CallTo(() =>
                Activation.ExecuteAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                // The guarded command leaves the database tracking, which is what the sequence's own
                // re-read of the instance database then observes.
                ActivationCompleted =
                    ActivationResult.Status == DocumentCacheAdministrativeCommandStatus.Completed;

                return Task.FromResult(ActivationResult);
            });

        A.CallTo(() => ProviderSetup.SetupAsync(A<CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcProviderSetupRequest request, CancellationToken _) =>
                    Task.FromResult(ProviderSetupResult(request))
            );

        A.CallTo(() => Connections.Create(A<CoreCdc.CdcProvider>._, A<string>._)).Returns(Connection);
        A.CallTo(() => Connection.OpenAsync(A<CancellationToken>._)).Returns(Task.CompletedTask);

        A.CallTo(() =>
                Kafka.EnsureConnectOffsetStoreAsync(A<CdcObservationContext>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcObservationContext context, CancellationToken _) =>
                    Task.FromResult(OffsetStorePolicy ?? ObservedOffsetStore(context, _clock.GetUtcNow()))
            );
        A.CallTo(() =>
                Kafka.DescribeConnectOffsetStoreAsync(A<CdcObservationContext>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcObservationContext context, CancellationToken _) =>
                    Task.FromResult(OffsetStorePolicy ?? ObservedOffsetStore(context, _clock.GetUtcNow()))
            );
        A.CallTo(() =>
                Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcObservationContext context, CdcArtifactInventory inventory, CancellationToken _) =>
                {
                    ProvisionedInventory = inventory;

                    return Task.FromResult(
                        KafkaPolicy ?? ObservedKafkaPolicy(context, inventory, _clock.GetUtcNow())
                    );
                }
            );
        A.CallTo(() =>
                Kafka.DescribeBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcObservationContext context, CdcArtifactInventory inventory, CancellationToken _) =>
                    Task.FromResult(
                        KafkaPolicy ?? ObservedKafkaPolicy(context, inventory, _clock.GetUtcNow())
                    )
            );
        A.CallTo(() =>
                Kafka.FindExistingGovernedTopicsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._)
            )
            .ReturnsLazily(() => Task.FromResult(GovernedTopicPresence ?? NoGovernedTopics));
        A.CallTo(() =>
                Kafka.ReadSqlServerSchemaHistoryAsync(
                    A<CdcArtifactInventory>._,
                    A<CdcSqlServerSchemaHistoryEnablementPhase>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    CdcArtifactInventory inventory,
                    CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
                    bool _,
                    CancellationToken _
                ) =>
                    Task.FromResult<CdcSqlServerSchemaHistoryEvidence?>(
                        inventory.Provider == CoreCdc.CdcProvider.SqlServer
                            ? new(enablementPhase, SchemaHistoryState)
                            : null
                    )
            );

        A.CallTo(() =>
                Connect.ValidateConnectorPluginConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromResult(PluginValidation));
        A.CallTo(() =>
                Connect.PutConnectorConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                // The worker holds the connector from here on, which is what the read-back below then
                // finds - and what an enablement asking whether these names are already taken must
                // not find before this point.
                ConnectorRegistered =
                    ConnectorRegistered || Registration.Outcome == CdcConnectOutcome.Succeeded;

                return Task.FromResult(Registration);
            });
        A.CallTo(() => Connect.GetConnectorConfigAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Task.FromResult(ConnectorHeldByWorker() ? ConnectorConfigReadBack : NoSuchConnector)
            );
        A.CallTo(() => Connect.GetConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(CommittedOffsets));
        A.CallTo(() => Connect.GetConnectorStatusAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(ConnectorStatus));
        A.CallTo(() => Connect.RestartConnectorAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(Restart));
        A.CallTo(() => Connect.StopConnectorAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(Stop));
        A.CallTo(() => Connect.ResumeConnectorAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(Resume));

        A.CallTo(() => Bindings.ListBindingsAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(BindingListing));

        A.CallTo(() => Bindings.ImportVerifiedBindingAsync(A<CdcAdoptionProof>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcAdoptionProof proof, CancellationToken _) =>
                {
                    ImportedProof = proof;
                    CdcBindingLifecycleResult result = ImportResult ?? Present(proof.Binding);
                    if (result.Status == CdcControlPlaneOperationStatus.Succeeded)
                    {
                        BindingRead = result;
                    }

                    return Task.FromResult(result);
                }
            );

        A.CallTo(() => Bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcIncident incident, CancellationToken _) =>
                {
                    if (LatchResult is { } refusedLatch)
                    {
                        // A latch the store did not accept leaves the binding state exactly as it was
                        // read: nothing about the incident survives this poll.
                        return Task.FromResult(refusedLatch);
                    }

                    // The latch is durable, so every later read of the binding state reports it — which
                    // is what keeps a proved loss latched across polls.
                    LatchedIncident ??= incident;
                    BindingRead = IncidentLatched(
                        BindingRead.State?.Binding ?? Binding(_provider),
                        LatchedIncident
                    );

                    return Task.FromResult(BindingRead);
                }
            );

        A.CallTo(() => SourcePositions.Provider).Returns(provider);
        A.CallTo(() =>
                SourcePositions.CaptureBarrierAsync(
                    A<CdcProviderBarrierCaptureRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() => Task.FromResult(CapturedBarrier ?? CaptureBarrier()));
        A.CallTo(() => SourcePositions.ObserveProviderBarrier(A<CdcProviderBarrierObservationRequest>._))
            .ReturnsLazily((CdcProviderBarrierObservationRequest request) => ObserveBarrier(request));
        A.CallTo(() =>
                SourcePositions.ObserveSourceHistoryAsync(
                    A<CdcSourceHistoryObservationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcSourceHistoryObservationRequest request, CancellationToken _) =>
                    Task.FromResult(ClassifySourceHistory(request))
            );

        A.CallTo(() => Lag.ReadAsync(A<CoreCdc.CdcProvider>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(LagReading));

        A.CallTo(() => Connect.DeleteConnectorOffsetsAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(DeleteOffsets));
        A.CallTo(() => Connect.DeleteConnectorAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(DeleteConnector));

        A.CallTo(() => Kafka.DeleteBindingArtifactsAsync(A<CdcArtifactInventory>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcArtifactInventory inventory, CancellationToken _) =>
                    Task.FromResult(DeletedKafkaArtifacts ?? RemovedKafkaArtifacts(inventory))
            );

        A.CallTo(() => ProviderTeardown.Provider).Returns(provider);
        A.CallTo(() =>
                ProviderTeardown.DeleteAsync(A<CdcProviderArtifactTeardownRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcProviderArtifactTeardownRequest request, CancellationToken _) =>
                    Task.FromResult(DeletedProviderArtifacts ?? RemovedProviderArtifacts(request.Inventory))
            );

        A.CallTo(() =>
                Bindings.DeleteStateAfterVerifiedCleanupAsync(A<CdcCleanupProof>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcCleanupProof proof, CancellationToken _) =>
                {
                    CleanupProof = proof;
                    CdcBindingLifecycleResult result = DeleteStateResult ?? Deleted();
                    if (result.Status == CdcControlPlaneOperationStatus.Succeeded)
                    {
                        BindingRead = result;
                    }

                    return Task.FromResult(result);
                }
            );
    }

    public ICdcEligibilityProbe Probe { get; } = A.Fake<ICdcEligibilityProbe>();

    public ICdcProjectionCorrelationCollector Projection { get; } =
        A.Fake<ICdcProjectionCorrelationCollector>();

    public ICdcBindingLifecycleService Bindings { get; } = A.Fake<ICdcBindingLifecycleService>();

    public IDocumentCacheGuardedNewEmptyActivationCommand Activation { get; } =
        A.Fake<IDocumentCacheGuardedNewEmptyActivationCommand>();

    public ICdcProviderSetupService ProviderSetup { get; } = A.Fake<ICdcProviderSetupService>();

    public ICdcKafkaAdmin Kafka { get; } = A.Fake<ICdcKafkaAdmin>();

    public ICdcConnectClient Connect { get; } = A.Fake<ICdcConnectClient>();

    public ICdcProviderSourcePositionAdapter SourcePositions { get; } =
        A.Fake<ICdcProviderSourcePositionAdapter>();

    public ICdcConnectorLagReader Lag { get; } = A.Fake<ICdcConnectorLagReader>();

    public ICdcProviderArtifactTeardown ProviderTeardown { get; } = A.Fake<ICdcProviderArtifactTeardown>();

    public ICdcInstanceDatabaseConnectionFactory Connections { get; } =
        A.Fake<ICdcInstanceDatabaseConnectionFactory>();

    public DbConnection Connection { get; } = A.Fake<DbConnection>();

    /// <summary>What the pre-binding eligibility read observed.</summary>
    public CdcEligibilityReadResult Eligibility { get; set; } = Reading();

    /// <summary>What the eligibility read observes once the guarded activation has completed.</summary>
    public CdcEligibilityReadResult PostActivationEligibility { get; set; } =
        Reading(lifecycleStateToken: "Tracking");

    /// <summary>Whether the guarded activation has run and completed.</summary>
    public bool ActivationCompleted { get; private set; }

    /// <summary>
    /// Whether the Kafka Connect worker is holding this binding's connector. Set by a successful
    /// registration, and settable up front to model a connector that outlived its binding record.
    /// </summary>
    public bool ConnectorRegistered { get; set; }

    /// <summary>
    /// Which of the binding's governed topics the broker already holds. Null leaves the broker
    /// answering that it holds none of them, which is what a first enablement finds.
    /// </summary>
    public CdcKafkaGovernedTopicPresence? GovernedTopicPresence { get; set; }

    /// <summary>What the running DMS reported about its projection of the target.</summary>
    public CdcProjectionStatusReadResult ProjectionStatus { get; set; }

    /// <summary>
    /// What the binding's SQL Server schema-history topic holds. It is never read for a PostgreSQL
    /// binding, which carries no schema-history evidence at all.
    /// </summary>
    public CdcSqlServerSchemaHistoryState SchemaHistoryState { get; set; } =
        CdcSqlServerSchemaHistoryState.Valid;

    /// <summary>The evidence the controller assembled for the source-history continuity check.</summary>
    public CdcSourceHistoryObservationRequest? SourceHistoryRequest { get; private set; }

    /// <summary>
    /// The classifier's verdict on that evidence, including whether it raised an incident candidate.
    /// </summary>
    public CdcSourceHistoryClassificationResult? SourceHistoryClassification { get; private set; }

    /// <summary>
    /// What the running DMS reports once the connector has committed past the provider barrier, when
    /// the projector's state changes between the two caught-up observations.
    /// </summary>
    public CdcProjectionStatusReadResult? ProjectionStatusAfterBarrier { get; set; }

    /// <summary>The durable binding state the enablement starts from.</summary>
    public CdcBindingLifecycleResult BindingRead { get; set; } = Missing();

    /// <summary>Overrides the result of creating or exact-matching the binding.</summary>
    public CdcBindingLifecycleResult? BindingWrite { get; set; }

    /// <summary>
    /// What the deployment-wide listing holds, which the enablement and the adoption both read to prove
    /// no other logical target already binds the physical source. Empty by default: the deployment has
    /// bound nothing else.
    /// </summary>
    public CdcBindingLifecycleListResult BindingListing { get; set; } = ListedBindings();

    /// <summary>
    /// The durable state of the generation a source replacement replaces. It is absent by default: the
    /// target has only ever had the one generation.
    /// </summary>
    public CdcBindingLifecycleResult? PreviousGenerationRead { get; set; }

    /// <summary>The artifact inventory the enablement provisioned the binding's Kafka artifacts for.</summary>
    public CdcArtifactInventory? ProvisionedInventory { get; private set; }

    public DocumentCacheAdministrativeCommandResult ActivationResult { get; set; } = Activated();

    /// <summary>The outcome of the create-or-exact-match provider setup pass.</summary>
    public Ddl.CdcProviderSetupOutcome ProviderSetupOutcome { get; set; } =
        Ddl.CdcProviderSetupOutcome.CreatedOrMatched;

    /// <summary>
    /// The outcome of the validate-only provider setup pass, which is the evidence every later status
    /// check reads the provider artifacts through. Exact-match by default, because that is what a
    /// conforming read-back of artifacts just created reports.
    /// </summary>
    public Ddl.CdcProviderSetupOutcome ValidateOnlyProviderSetupOutcome { get; set; } =
        Ddl.CdcProviderSetupOutcome.ExactMatch;

    /// <summary>Overrides the shared Connect offset-store evidence.</summary>
    public CdcConnectOffsetStorePolicyObservation? OffsetStorePolicy { get; set; }

    /// <summary>The state the shared Connect offset store's own policy is observed in.</summary>
    public CdcConnectOffsetStorePolicyState OffsetStoreState { get; set; } =
        CdcConnectOffsetStorePolicyState.Satisfied;

    /// <summary>The state the shared Connect offset store's worker-only grants are observed in.</summary>
    public CdcConnectOffsetStoreItemState OffsetStoreAclState { get; set; } =
        CdcConnectOffsetStoreItemState.Satisfied;

    /// <summary>Overrides the binding's Kafka topic, ACL, and record-size evidence.</summary>
    public CdcKafkaPolicyObservation? KafkaPolicy { get; set; }

    /// <summary>The state the binding's composed Kafka policy is observed in.</summary>
    public CdcKafkaPolicyState KafkaPolicyState { get; set; } = CdcKafkaPolicyState.Satisfied;

    /// <summary>
    /// The state the binding's record-size budget is observed in: one item of the composed policy, so
    /// it is what proves an item-level nonconformance is gated as the composed state is.
    /// </summary>
    public CdcKafkaPolicyItemState KafkaRecordSizeState { get; set; } = CdcKafkaPolicyItemState.Satisfied;

    public CdcConnectResult<CdcConnectConfigValidation> PluginValidation { get; set; } =
        new(CdcConnectOutcome.Succeeded, new(0, []), null);

    public CdcConnectResult Registration { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>How the worker answers a connector restart.</summary>
    public CdcConnectResult Restart { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>How the worker answers a connector stop.</summary>
    public CdcConnectResult Stop { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>How the worker answers a connector resume.</summary>
    public CdcConnectResult Resume { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>The source-history loss the control plane latched, if it latched one.</summary>
    public CdcIncident? LatchedIncident { get; private set; }

    /// <summary>The adoption proof the control plane asked the state store to import, if it issued one.</summary>
    public CdcAdoptionProof? ImportedProof { get; private set; }

    /// <summary>Overrides how the state store answers a verified-binding import.</summary>
    public CdcBindingLifecycleResult? ImportResult { get; set; }

    /// <summary>Overrides how the state store answers a source-history loss latch.</summary>
    public CdcBindingLifecycleResult? LatchResult { get; set; }

    /// <summary>The cleanup proof the control plane presented to delete the binding record.</summary>
    public CdcCleanupProof? CleanupProof { get; private set; }

    /// <summary>Overrides how the state store answers a verified-cleanup deletion.</summary>
    public CdcBindingLifecycleResult? DeleteStateResult { get; set; }

    /// <summary>How the worker answers a deletion of the connector's committed source offsets.</summary>
    public CdcConnectResult DeleteOffsets { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>The operator's assertion that the connector this retirement names is already gone.</summary>
    public bool ConnectorAlreadyAbsent { get; set; }

    /// <summary>How the worker answers a deletion of the connector configuration.</summary>
    public CdcConnectResult DeleteConnector { get; set; } = new(CdcConnectOutcome.Succeeded, null);

    /// <summary>Overrides the governed Kafka artifacts a teardown removed.</summary>
    public IReadOnlyList<CdcGovernedArtifact>? DeletedKafkaArtifacts { get; set; }

    /// <summary>Overrides the governed provider artifacts a teardown removed.</summary>
    public IReadOnlyList<CdcGovernedArtifact>? DeletedProviderArtifacts { get; set; }

    /// <summary>The configuration the Connect worker reports back for the registered connector.</summary>
    public CdcConnectResult<IReadOnlyDictionary<string, string>> ConnectorConfigReadBack { get; set; }

    public CdcConnectResult<CdcConnectorOffsets> CommittedOffsets { get; set; }

    public CdcConnectResult<CdcConnectorStatus> ConnectorStatus { get; set; } = RunningConnector();

    /// <summary>Overrides the captured provider barrier.</summary>
    public CdcProviderBarrierCaptureResult? CapturedBarrier { get; set; }

    public CdcConnectorLagReadResult LagReading { get; set; } =
        new(CdcConnectorLagReadOutcome.Succeeded, new(10, 4, 8, 9), null);

    /// <summary>The projection targets the DMS itself is configured with.</summary>
    public IReadOnlyList<(string TenantKey, long DataStoreId)> ConfiguredProjectionTargets { get; set; } =
    [(TenantKey, DataStoreId)];

    /// <summary>Per-step budgets. The waits are short so an unmet condition ends the test promptly.</summary>
    public CdcControlTimeoutOptions Timeouts { get; } =
        new()
        {
            ProjectionCaughtUp = TimeSpan.FromSeconds(5),
            ProviderBarrier = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(1),
        };

    public Task<CdcAdmission> EnableAsync() =>
        Controller().EnableAsync(Request(_provider), CancellationToken.None);

    public Task<CdcStatus> StatusAsync() =>
        Controller().StatusAsync(TargetRequest(_provider), CancellationToken.None);

    public Task<CdcStatus> RestartAsync() =>
        Controller().RestartAsync(TargetRequest(_provider), CancellationToken.None);

    public Task<CdcContractReadResult<CdcCleanupProof>> RetireAsync() =>
        Controller()
            .RetireAsync(
                TargetRequest(_provider) with
                {
                    ConnectorAlreadyAbsent = ConnectorAlreadyAbsent,
                },
                CancellationToken.None
            );

    /// <param name="previousGeneration">The generation being replaced, which precedes this target's own.</param>
    public Task<CdcAdmission> ReplaceSourceAsync(
        long previousGeneration = CdcControlTemplateTestData.BindingGeneration - 1
    ) =>
        Controller()
            .ReplaceSourceAsync(
                new CdcReplaceSourceRequest(
                    OperationId,
                    TenantKey,
                    DataStoreId,
                    ConnectionString,
                    previousGeneration,
                    Request(_provider).ProvisioningEvidence,
                    Request(_provider).ProviderSetup
                ),
                CancellationToken.None
            );

    /// <param name="binding">
    /// The complete binding record the operator supplies. It defaults to the one this harness's target
    /// was enabled under, which is what every artifact answers as.
    /// </param>
    public Task<CdcContractReadResult<CdcAdoptionProof>> AdoptAsync(CdcBinding? binding = null)
    {
        // Adoption's whole premise is the shape the enablement refuses: a complete governed-artifact
        // set with no binding record naming it. The record is missing here, so the worker must still
        // be holding the connector - otherwise there would be nothing to adopt. A test modelling a
        // connector adoption cannot verify sets ConnectorConfigReadBack, which this does not override.
        ConnectorRegistered = true;

        return Controller()
            .AdoptAsync(
                AdoptRequest(_provider) with
                {
                    Binding = binding ?? Binding(_provider),
                },
                CancellationToken.None
            );
    }

    public ICdcSetupController Controller() =>
        new CdcSetupController(
            Options.Create(ControlOptions()),
            new CdcExplicitProjectionTargetProof(Configuration()),
            Projection,
            Probe,
            Bindings,
            Activation,
            ProviderSetup,
            Connections,
            Kafka,
            TemplateService,
            Connect,
            new CdcConnectorObservationMapper(TemplateService, _clock),
            Lag,
            SourcePositions,
            ProviderTeardown,
            _clock,
            NullLogger<CdcSetupController>.Instance
        );

    public static ICdcConnectorTemplateService TemplateService =>
        _templateServices.GetRequiredService<ICdcConnectorTemplateService>();

    public static CdcEnableRequest Request(CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql) =>
        new(
            OperationId,
            TenantKey,
            DataStoreId,
            ConnectionString,
            new CdcProvisioningProofEvidence(
                SetupControllerRunId,
                CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken,
                CdcProvisioningProofFactory.ClosedNeverOpenedToken
            ),
            new CdcProviderSetupInputs(
                "setup_principal",
                "connector_principal",
                CdcControlTemplateTestData.BuildSourceTableInventory(ToDdlProvider(provider)),
                [
                    new CdcDmsManagedTableInventory(
                        CdcDmsManagedTableKind.Core,
                        new DbTableName(new DbSchemaName("dms"), "Document"),
                        provider == CoreCdc.CdcProvider.Postgresql
                            ? "\"dms\".\"Document\""
                            : "[dms].[Document]"
                    ),
                ]
            )
        );

    /// <summary>
    /// One operator request against the already-enabled target. It carries the same provider-setup
    /// inputs an enablement does, because the validate-only inspection needs them, and nothing about
    /// the binding: that is read from the durable record.
    /// </summary>
    public static CdcTargetOperationRequest TargetRequest(
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    )
    {
        CdcEnableRequest enable = Request(provider);

        return new(OperationId, TenantKey, DataStoreId, ConnectionString, enable.ProviderSetup);
    }

    /// <summary>
    /// One operator request to adopt the artifact set the default target already holds, under the
    /// complete binding record the operator supplies.
    /// </summary>
    public static CdcAdoptRequest AdoptRequest(
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    ) => new(OperationId, Binding(provider), ConnectionString, Request(provider).ProviderSetup);

    /// <summary>The binding the controller composes for the default target.</summary>
    public static CdcBinding Binding(CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql) =>
        CdcControlTemplateTestData.BuildBinding(ToDdlProvider(provider));

    public static CdcArtifactInventory Inventory(
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    ) => CdcControlTemplateTestData.BuildInventory(ToDdlProvider(provider));

    /// <summary>The governed artifact names of a generation other than this target's own.</summary>
    public static CdcArtifactInventory InventoryFor(
        long generation,
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    ) =>
        CdcArtifactNameGenerator
            .Render(
                new(
                    CdcControlTemplateTestData.DeploymentKey,
                    CdcControlTemplateTestData.TopicPrefix,
                    CdcControlTemplateTestData.InstanceKey,
                    generation,
                    provider
                )
            )
            .Inventory!;

    /// <summary>
    /// The binding record of an earlier generation of the same target: the one a source replacement
    /// replaces. Its governed names are that generation's own, so none of them is the current one's,
    /// and it is bound to the physical source being replaced rather than to the replacing one.
    /// </summary>
    /// <param name="physicalSourceFingerprint">
    /// The source the outgoing generation is bound to. It defaults to the replaced source, because a
    /// replacement that still reported the outgoing generation's own source replaced nothing.
    /// </param>
    public static CdcBinding PreviousGenerationBinding(
        long generation = CdcControlTemplateTestData.BindingGeneration - 1,
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql,
        string? physicalSourceFingerprint = null
    )
    {
        CdcArtifactInventory inventory = InventoryFor(generation, provider);

        return Binding(provider) with
        {
            Generation = generation,
            ConnectorName = inventory.ConnectorName,
            TopicName = inventory.TopicName,
            PhysicalSourceFingerprint = physicalSourceFingerprint ?? ReplacedFingerprint(provider),
        };
    }

    public static string Fingerprint(CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql) =>
        CdcControlTemplateTestData.SourceFingerprint(ToDdlProvider(provider)).Value;

    /// <summary>The fingerprint of the physical source a source replacement replaces.</summary>
    public static string ReplacedFingerprint(CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql) =>
        CdcControlTemplateTestData.ReplacedSourceFingerprint(ToDdlProvider(provider)).Value;

    private static Ddl.CdcProvider ToDdlProvider(CoreCdc.CdcProvider provider) =>
        provider == CoreCdc.CdcProvider.Postgresql ? Ddl.CdcProvider.Postgresql : Ddl.CdcProvider.SqlServer;

    public static CdcEligibilityReadResult Reading(
        string? lifecycleStateToken = "Disabled",
        bool? cacheAheadRecoveryRequired = false,
        bool canonicalRowsPresent = false,
        bool cacheRowsPresent = false,
        bool workRowsPresent = false,
        string? sourceIdentity = SourceIdentity
    ) =>
        new(
            CdcEligibilityReadOutcome.Succeeded,
            new(
                Now.AddSeconds(-1),
                "1234:1240:",
                lifecycleStateToken,
                cacheAheadRecoveryRequired,
                canonicalRowsPresent,
                cacheRowsPresent,
                workRowsPresent,
                sourceIdentity
            ),
            null
        );

    /// <summary>A broker that holds none of the binding's governed topics.</summary>
    private static CdcKafkaGovernedTopicPresence NoGovernedTopics { get; } = new(true, []);

    /// <summary>The worker's answer for a connector it does not hold.</summary>
    private static CdcConnectResult<IReadOnlyDictionary<string, string>> NoSuchConnector { get; } =
        new(CdcConnectOutcome.NotFound, null, null);

    /// <summary>
    /// Whether the worker would answer a configuration read for this binding's connector.
    /// </summary>
    /// <remarks>
    /// A fixture whose durable binding record is missing is one where nothing was ever provisioned
    /// under these names, so the worker holds no connector until this run registers one. A fixture that
    /// starts from a present record models an already-enabled target, whose connector exists from the
    /// outset. Without the distinction the read-back stub would answer the same way before and after
    /// registration, and an enablement asking whether these names are already taken would always be
    /// told they are.
    /// </remarks>
    private bool ConnectorHeldByWorker() =>
        ConnectorRegistered || BindingRead.Status != CdcControlPlaneOperationStatus.BindingMissing;

    public static CdcEligibilityReadResult UnreadableDatabase() =>
        new(CdcEligibilityReadOutcome.Unreadable, null, "unreadable");

    /// <summary>A running DMS that reports the enabled target, caught up and operational.</summary>
    public static CdcProjectionStatusReadResult ProjectingTheTarget(
        DocumentCacheOperationalHealthStatus operationalHealth =
            DocumentCacheOperationalHealthStatus.Operational,
        DocumentCacheCaughtUpStatus caughtUp = DocumentCacheCaughtUpStatus.CaughtUp,
        DocumentCacheStatusQueuePresence queuePresence = DocumentCacheStatusQueuePresence.Empty,
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    ) =>
        new(
            CdcProjectionStatusReadOutcome.Succeeded,
            new(
                Now,
                [
                    new CdcProjectionTargetReading(
                        DocumentCacheStatusTargetKey.FromTargetKey(
                            DocumentCacheTargetKey.Create(TenantKey, DataStoreId)
                        ),
                        Now,
                        provider == CoreCdc.CdcProvider.Postgresql
                            ? RelationalProviderToken.PostgresqlValue
                            : RelationalProviderToken.SqlServerValue,
                        Fingerprint(provider),
                        operationalHealth,
                        DocumentCacheStatusReason.None,
                        caughtUp,
                        DocumentCacheStatusReason.None,
                        queuePresence,
                        []
                    ),
                ]
            ),
            null
        );

    public static CdcProjectionStatusReadResult StatusEndpointFailure(
        CdcProjectionStatusReadOutcome outcome
    ) => new(outcome, null, "unavailable");

    public static CdcBindingLifecycleResult Missing() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.BindingMissing,
            new(CdcJsonContract.CurrentContractVersion, Now, CdcBindingState.BindingMissing, null, null),
            []
        );

    /// <summary>A readable deployment-wide listing holding exactly the given bindings.</summary>
    public static CdcBindingLifecycleListResult ListedBindings(params CdcBinding[] bindings) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.Succeeded,
            [
                .. bindings.Select(binding => new CdcBindingStateContract(
                    CdcJsonContract.CurrentContractVersion,
                    Now,
                    CdcBindingState.BindingPresent,
                    binding,
                    null
                )),
            ],
            []
        );

    /// <summary>A deployment-wide listing the state store could not produce.</summary>
    public static CdcBindingLifecycleListResult UnreadableBindingListing() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.StateStoreUnavailable,
            [],
            []
        );

    public static CdcBindingLifecycleResult Present(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.Succeeded,
            new(CdcJsonContract.CurrentContractVersion, Now, CdcBindingState.BindingPresent, binding, null),
            []
        );

    /// <summary>The durable state of a binding whose source-history loss has been latched.</summary>
    public static CdcBindingLifecycleResult IncidentLatched(CdcBinding binding, CdcIncident incident) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.Succeeded,
            new(
                CdcJsonContract.CurrentContractVersion,
                Now,
                CdcBindingState.IncidentLatched,
                binding,
                incident
            ),
            []
        );

    /// <summary>The state store's answer once a verified cleanup has removed the binding record.</summary>
    public static CdcBindingLifecycleResult Deleted() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.Succeeded,
            new(CdcJsonContract.CurrentContractVersion, Now, CdcBindingState.BindingMissing, null, null),
            []
        );

    /// <summary>The governed Kafka artifacts a binding teardown reports, named by the binding's own inventory.</summary>
    public static IReadOnlyList<CdcGovernedArtifact> RemovedKafkaArtifacts(
        CdcArtifactInventory inventory,
        CdcCleanupState cleanupState = CdcCleanupState.Deleted
    ) =>
        RemovedArtifacts(
            inventory,
            cleanupState,
            [
                CdcGovernedArtifactKind.PublicTopic,
                CdcGovernedArtifactKind.PublicTopicAcls,
                CdcGovernedArtifactKind.ProgressTopic,
                CdcGovernedArtifactKind.ProgressTopicAcls,
                CdcGovernedArtifactKind.SchemaHistoryTopic,
                CdcGovernedArtifactKind.SchemaHistoryTopicAcls,
            ]
        );

    /// <summary>The governed provider artifacts a binding teardown reports, for either provider.</summary>
    public static IReadOnlyList<CdcGovernedArtifact> RemovedProviderArtifacts(
        CdcArtifactInventory inventory,
        CdcCleanupState cleanupState = CdcCleanupState.Deleted
    ) =>
        RemovedArtifacts(
            inventory,
            cleanupState,
            [
                CdcGovernedArtifactKind.PostgresqlPublication,
                CdcGovernedArtifactKind.PostgresqlLogicalSlot,
                CdcGovernedArtifactKind.SqlServerCdcGatingRole,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache,
                CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat,
            ]
        );

    /// <summary>
    /// The artifacts of the named kinds the binding actually governs. Selecting from the inventory is
    /// what keeps every reported name the deterministic one the cleanup proof is validated against.
    /// </summary>
    private static IReadOnlyList<CdcGovernedArtifact> RemovedArtifacts(
        CdcArtifactInventory inventory,
        CdcCleanupState cleanupState,
        IReadOnlyList<CdcGovernedArtifactKind> kinds
    ) =>
        [
            .. inventory
                .GovernedArtifacts.Where(artifact => kinds.Contains(artifact.Kind))
                .Select(artifact => new CdcGovernedArtifact(
                    artifact.Kind,
                    artifact.Name,
                    cleanupState,
                    cleanupState == CdcCleanupState.Deleted
                        ? "the governed artifact was removed"
                        : "no such governed artifact"
                )),
        ];

    public static CdcBindingLifecycleResult Mismatched() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.BindingMismatch,
            new(
                CdcJsonContract.CurrentContractVersion,
                Now,
                CdcBindingState.BindingMismatch,
                Binding() with
                {
                    Generation = 6,
                },
                null
            ),
            []
        );

    public static CdcBindingLifecycleResult StateStoreUnavailable() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            Now,
            CdcControlPlaneOperationStatus.StateStoreUnavailable,
            null,
            [new(CdcDiagnosticCategory.LocalStateUnavailable, Now, "$", "binding state store unavailable")]
        );

    public static DocumentCacheAdministrativeCommandResult Activated(
        DocumentCacheAdministrativeCommandStatus status = DocumentCacheAdministrativeCommandStatus.Completed,
        DocumentCacheAdministrativeCommandClassification classification =
            DocumentCacheAdministrativeCommandClassification.Succeeded
    ) =>
        new(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            new DocumentCacheAdministrativeTargetKey(TenantKey, DataStoreId),
            status,
            classification,
            mutated: status == DocumentCacheAdministrativeCommandStatus.Completed
        );

    /// <summary>The configuration a conforming Connect worker reports for the rendered connector.</summary>
    public static CdcConnectResult<IReadOnlyDictionary<string, string>> RenderedConnectorConfig(
        Action<Dictionary<string, string>>? drift = null,
        CoreCdc.CdcProvider provider = CoreCdc.CdcProvider.Postgresql
    )
    {
        Dictionary<string, string> config = new(
            TemplateService
                .Render(CdcControlTemplateTestData.BuildTemplateRequest(ToDdlProvider(provider)))
                .Config,
            StringComparer.Ordinal
        );
        drift?.Invoke(config);

        return new(CdcConnectOutcome.Succeeded, config, null);
    }

    /// <summary>One committed streaming offset under the binding's own Connect source partition.</summary>
    public static CdcConnectResult<CdcConnectorOffsets> StreamingOffsets(long lsnProc) =>
        new(
            CdcConnectOutcome.Succeeded,
            new([
                new CdcConnectorOffsetEntry(
                    Json($$"""{"server":"{{Inventory().ConnectorName}}"}"""),
                    Json(
                        $$"""{"lsn_proc":{{lsnProc.ToString(CultureInfo.InvariantCulture)}},"snapshot":false}"""
                    )
                ),
            ]),
            null
        );

    /// <summary>
    /// One committed streaming offset under the SQL Server binding's own Connect source partition, which
    /// includes the catalog the connector reads alongside its topic prefix.
    /// </summary>
    public static CdcConnectResult<CdcConnectorOffsets> SqlServerStreamingOffsets(
        string commitLsn = SqlServerCommittedCommitLsn,
        string changeLsn = SqlServerCommittedChangeLsn,
        long eventSerialNo = CdcSqlServerProviderPosition.HeartbeatAfterImageEventSerialNo
    ) =>
        new(
            CdcConnectOutcome.Succeeded,
            new([
                new CdcConnectorOffsetEntry(
                    Json(
                        $$"""
                        {"database":"{{SqlServerCatalogName}}","server":"{{Inventory(
                            CoreCdc.CdcProvider.SqlServer
                        ).ConnectorName}}"}
                        """
                    ),
                    Json(
                        $$"""
                        {"commit_lsn":"{{commitLsn}}","change_lsn":"{{changeLsn}}","event_serial_no":{{eventSerialNo.ToString(
                            CultureInfo.InvariantCulture
                        )}},"snapshot":false}
                        """
                    )
                ),
            ]),
            null
        );

    private static CdcConnectResult<CdcConnectorOffsets> CommittedStreamingOffsets(
        CoreCdc.CdcProvider provider
    ) =>
        provider == CoreCdc.CdcProvider.Postgresql
            ? StreamingOffsets(CommittedLsnProc)
            : SqlServerStreamingOffsets();

    public static CdcConnectResult<CdcConnectorStatus> RunningConnector(
        string connectorState = "RUNNING",
        string taskState = "RUNNING"
    ) => new(CdcConnectOutcome.Succeeded, new(connectorState, [new(0, taskState, null)]), null);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The projector's report, stamped as of this read. Each read reports a later observation than the
    /// last, which is what makes the sequence's ordering — first caught-up, barrier, continuity, second
    /// caught-up — expressible.
    /// </summary>
    private CdcProjectionStatusReadResult CurrentProjectionStatus()
    {
        DateTimeOffset observedAt = _clock.GetUtcNow();
        CdcProjectionStatusReadResult reported =
            (_barrierReached ? ProjectionStatusAfterBarrier : null) ?? ProjectionStatus;

        return reported.Status is { } status
            ? reported with
            {
                Status = new(
                    observedAt,
                    [.. status.Targets.Select(target => target with { ProcessObservedAt = observedAt })]
                ),
            }
            : reported;
    }

    /// <summary>The barrier position the provider's capture pass reports for this harness's provider.</summary>
    private CdcProviderBarrierCaptureResult CaptureBarrier() =>
        _provider == CoreCdc.CdcProvider.Postgresql
            ? CdcProviderBarrierCaptureResult.PostgresqlSuccess(BarrierLsn, _clock.GetUtcNow())
            : CdcProviderBarrierCaptureResult.SqlServerSuccess(
                SqlServerBarrierCommitLsn,
                SqlServerBarrierChangeLsn,
                _clock.GetUtcNow()
            );

    /// <summary>
    /// Mirrors the provider source-position adapters: the barrier is reached only when the connector's
    /// committed streaming position is at or past the captured barrier position.
    /// </summary>
    private CdcProviderBarrierObservation ObserveBarrier(CdcProviderBarrierObservationRequest request)
    {
        bool postgresql = _provider == CoreCdc.CdcProvider.Postgresql;

        // A capture that failed is absent evidence, and the adapters report it as an unknown barrier
        // rather than comparing the connector against a position nothing reported.
        if (!request.CapturedBarrier.Succeeded)
        {
            return BarrierObservation(
                request,
                postgresql,
                CdcProviderBarrierState.Unknown,
                null,
                request.CapturedBarrier.Diagnostics
            );
        }

        CdcProviderPositionComparisonResult comparison = postgresql
            ? ComparePostgresqlBarrier(request)
            : CompareSqlServerBarrier(request);
        _barrierReached = _barrierReached || comparison.Succeeded;

        return BarrierObservation(
            request,
            postgresql,
            comparison.Succeeded ? CdcProviderBarrierState.Reached : CdcProviderBarrierState.NotReached,
            comparison.CommittedPosition,
            []
        );
    }

    private CdcProviderBarrierObservation BarrierObservation(
        CdcProviderBarrierObservationRequest request,
        bool postgresql,
        CdcProviderBarrierState barrierState,
        string? committedPosition,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            request.OperationId,
            _clock.GetUtcNow(),
            request.Binding.ToTargetIdentity(),
            _provider,
            request.Binding.PhysicalSourceFingerprint,
            request.ProjectionCaughtUpObservedAt,
            request.CapturedBarrier.BarrierCapturedAt,
            request.ConnectorOffset.ObservedAt,
            barrierState,
            postgresql ? request.CapturedBarrier.PostgresqlBarrierLsn : null,
            postgresql ? null : request.CapturedBarrier.SqlServerCommitLsn,
            postgresql ? null : request.CapturedBarrier.SqlServerChangeLsn,
            postgresql ? null : request.CapturedBarrier.SqlServerEventSerialNo,
            committedPosition,
            diagnostics
        );

    private static CdcProviderPositionComparisonResult ComparePostgresqlBarrier(
        CdcProviderBarrierObservationRequest request
    )
    {
        CdcPostgresqlWalPositionResult barrier = CdcPostgresqlProviderPosition.ParseWalLsn(
            request.CapturedBarrier.PostgresqlBarrierLsn
        );

        return CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
            barrier.Position!.Value,
            new(
                request.ConnectorOffset.SourcePartitionMatchResult,
                request.ConnectorOffset.IsSnapshot,
                request.ConnectorOffset.IsNull,
                request.ConnectorOffset.LsnProc
            )
        );
    }

    private static CdcProviderPositionComparisonResult CompareSqlServerBarrier(
        CdcProviderBarrierObservationRequest request
    )
    {
        CdcSqlServerLsnResult commitLsn = CdcSqlServerProviderPositionParser.ParseLsn(
            request.CapturedBarrier.SqlServerCommitLsn,
            "$.sqlServerCommitLsn"
        );
        CdcSqlServerLsnResult changeLsn = CdcSqlServerProviderPositionParser.ParseLsn(
            request.CapturedBarrier.SqlServerChangeLsn,
            "$.sqlServerChangeLsn"
        );

        return CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
            CdcSqlServerProviderPosition.HeartbeatAfterImage(commitLsn.Lsn!.Value, changeLsn.Lsn!.Value),
            new(
                request.ConnectorOffset.SourcePartitionMatchResult,
                request.ConnectorOffset.IsSnapshot,
                request.ConnectorOffset.IsNull,
                request.ConnectorOffset.CommitLsn,
                request.ConnectorOffset.ChangeLsn,
                request.ConnectorOffset.EventSerialNo
            )
        );
    }

    /// <summary>
    /// Mirrors the provider source-position adapters: continuity is the shared classifier's verdict on
    /// the evidence the caller collected, never a verdict of the adapter's own.
    /// </summary>
    private CdcSourceHistoryClassificationResult ClassifySourceHistory(
        CdcSourceHistoryObservationRequest request
    )
    {
        SourceHistoryRequest = request;
        SourceHistoryClassification = CdcSourceHistoryContinuityClassifier.Evaluate(
            new(request.OperationId, _clock.GetUtcNow(), _clock.GetUtcNow(), request.Binding)
            {
                ProviderSetup = request.ProviderSetup,
                ConnectorOffset = request.ConnectorOffset,
                ProviderHistory = request.ProviderHistory,
                SqlServerSchemaHistory = request.SqlServerSchemaHistory,
                LatchedIncident = request.LatchedIncident,
                ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
            }
        );

        return SourceHistoryClassification;
    }

    /// <summary>The shared offset store as this harness's deployment holds it.</summary>
    private CdcConnectOffsetStorePolicyObservation ObservedOffsetStore(
        CdcObservationContext context,
        DateTimeOffset observedAt
    ) =>
        SatisfiedOffsetStore(context, observedAt) with
        {
            PolicyState = OffsetStoreState,
            AclState = OffsetStoreAclState,
        };

    /// <summary>The binding's Kafka artifacts as this harness's deployment holds them.</summary>
    private CdcKafkaPolicyObservation ObservedKafkaPolicy(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        DateTimeOffset observedAt
    )
    {
        CdcKafkaPolicyObservation satisfied = SatisfiedKafkaPolicy(context, inventory, observedAt);

        return satisfied with
        {
            PolicyState = KafkaPolicyState,
            RecordSizePolicy = satisfied.RecordSizePolicy with { State = KafkaRecordSizeState },
        };
    }

    private static CdcConnectOffsetStorePolicyObservation SatisfiedOffsetStore(
        CdcObservationContext context,
        DateTimeOffset observedAt
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            "worker-1",
            "connect-offsets",
            CdcConnectOffsetStorePolicyState.Satisfied,
            "compact",
            1,
            1,
            // A local broker has no authorizer, so the worker-only grants are reported as satisfied by
            // the absence of an authorizer rather than by an observed grant.
            CdcConnectOffsetStoreItemState.Satisfied,
            []
        );

    private static CdcKafkaPolicyObservation SatisfiedKafkaPolicy(
        CdcObservationContext context,
        CdcArtifactInventory inventory,
        DateTimeOffset observedAt
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            CdcKafkaPolicyState.Satisfied,
            CdcControlOptions.LocalDurabilityProfile,
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "compact", 1, 1),
            // The schema-history topic is SQL Server-only evidence, and the shared contract requires it
            // to be present for that provider and absent for PostgreSQL.
            inventory.SchemaHistoryTopicName
                is { } schemaHistoryTopicName
                ? new(schemaHistoryTopicName, CdcKafkaPolicyItemState.Satisfied, 1, "delete", 1, 1)
                : null,
            new(inventory.TopicName, CdcKafkaPolicyItemState.NotApplicable),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.NotApplicable),
            inventory.SchemaHistoryTopicName is { } historyTopicName
                ? new(historyTopicName, CdcKafkaPolicyItemState.NotApplicable)
                : null,
            new(CdcKafkaPolicyItemState.Satisfied, 67_108_864, 67_108_864),
            []
        );

    private CdcProviderSetupResult ProviderSetupResult(CdcProviderSetupRequest request) =>
        CdcControlTemplateTestData
            .BuildFreshProviderSetupEvidence(
                _ddlProvider,
                request.Mode,
                request.Mode == Ddl.CdcProviderSetupMode.ValidateOnly
                    ? ValidateOnlyProviderSetupOutcome
                    : ProviderSetupOutcome
            )
            .Result;

    private CdcControlOptions ControlOptions() =>
        new()
        {
            DeploymentKey = CdcControlTemplateTestData.DeploymentKey,
            InstanceKey = CdcControlTemplateTestData.InstanceKey,
            TopicPrefix = CdcControlTemplateTestData.TopicPrefix,
            Generation = CdcControlTemplateTestData.BindingGeneration,
            PartitionCount = 1,
            KafkaBootstrapServers = CdcControlTemplateTestData.KafkaBootstrapServers,
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = CdcControlTemplateTestData.MaxRecordBytes,
            HeartbeatInterval = CdcControlTemplateTestData.HeartbeatInterval,
            SqlServerPollInterval =
                _provider == CoreCdc.CdcProvider.SqlServer
                    ? CdcControlTemplateTestData.SqlServerPollInterval
                    : null,
            ProviderConnectionProperties = new Dictionary<string, string>(
                CdcControlTemplateTestData.BuildConnectionProperties(_ddlProvider),
                StringComparer.Ordinal
            ),
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
            Timeouts = Timeouts,
        };

    private IConfiguration Configuration()
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal);
        for (int index = 0; index < ConfiguredProjectionTargets.Count; index++)
        {
            string prefix = $"{CdcExplicitProjectionTargetProof.TargetsSectionName}:{index}";
            settings[$"{prefix}:TenantKey"] = ConfiguredProjectionTargets[index].TenantKey;
            settings[$"{prefix}:DataStoreId"] = ConfiguredProjectionTargets[index]
                .DataStoreId.ToString(CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// A clock that advances by a fixed step on every read, so evidence collected in order carries
    /// timestamps in that order.
    /// </summary>
    private sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private long _reads = -1;

        public override DateTimeOffset GetUtcNow() => start + step * Interlocked.Increment(ref _reads);
    }
}
