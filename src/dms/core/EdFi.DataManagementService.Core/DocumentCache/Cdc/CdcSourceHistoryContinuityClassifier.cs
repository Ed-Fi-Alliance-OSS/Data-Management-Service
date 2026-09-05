// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcProviderSourceHistoryEvidence(
    CdcProviderArtifactContinuityState ProviderArtifactState,
    CdcProviderRetainedRangeState RetainedRangeState,
    string? ProviderArtifactName,
    string? RetainedRangeStart,
    string? RetainedRangeEnd,
    IReadOnlyList<CdcIncidentUnavailableFact> UnavailableFacts
)
{
    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; init; } = [];

    public CdcSqlServerCdcJobEvidence? SqlServerJobs { get; init; }
}

/// <summary>
/// Whether the binding's public topic has ever carried a published record.
/// </summary>
/// <remarks>
/// This is what separates an established stream from an initial enablement that has not finished, and
/// it is read from the topic's own log-end offsets rather than from any durable marker: a topic that
/// has received a record has a log end past zero, and compaction never moves it back, so the answer
/// survives every retention rule the public topic is under.
///
/// <paramref name="Readable"/> false means the broker did not answer. Evidence that could not be
/// obtained is never read as proof that the stream is unestablished, and never as proof that it is.
/// </remarks>
public sealed record CdcPublicTopicPublicationEvidence(bool Readable, bool HasPublishedRecords)
{
    /// <summary>Nothing was asked, so nothing is established by it.</summary>
    public static CdcPublicTopicPublicationEvidence Unreadable { get; } = new(false, false);

    /// <summary>The stream is established: at least one record has been published to the public topic.</summary>
    public bool ProvesEstablishedStream => Readable && HasPublishedRecords;
}

public sealed record CdcSqlServerSchemaHistoryEvidence(
    CdcSqlServerSchemaHistoryEnablementPhase EnablementPhase,
    CdcSqlServerSchemaHistoryState State
)
{
    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record CdcSourceHistoryClassificationInput(
    string OperationId,
    DateTimeOffset ObservedAt,
    DateTimeOffset NowUtc,
    CdcBinding Binding
)
{
    public CdcProviderSetupObservation? ProviderSetup { get; init; }

    public CdcConnectorOffsetObservation? ConnectorOffset { get; init; }

    public CdcProviderSourceHistoryEvidence? ProviderHistory { get; init; }

    public CdcSqlServerSchemaHistoryEvidence? SqlServerSchemaHistory { get; init; }

    /// <summary>
    /// Whether the binding's public topic proves an established stream. Absent evidence is treated
    /// exactly as unreadable evidence: the classifier withholds a terminal classification either way.
    /// </summary>
    public CdcPublicTopicPublicationEvidence? PublicTopicPublication { get; init; }

    public CdcIncident? LatchedIncident { get; init; }

    public string? ExpectedConnectSourcePartitionHash { get; init; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record CdcSourceHistoryIncidentCandidate(
    DateTimeOffset ObservedAt,
    CdcCompleteBindingIdentity BindingIdentity,
    CdcIncidentFailureCategory FailureCategory,
    CdcIncidentPositionMetadata PositionMetadata
)
{
    public CdcIncident ToIncident() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            ObservedAt,
            BindingIdentity,
            FailureCategory,
            PositionMetadata
        );
}

public sealed record CdcSourceHistoryClassificationResult(
    CdcSourceHistoryObservation Observation,
    CdcSourceHistoryIncidentCandidate? IncidentCandidate
);

public static class CdcSourceHistoryContinuityClassifier
{
    public static CdcSourceHistoryClassificationResult Evaluate(CdcSourceHistoryClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Binding);

        CdcBinding binding = input.Binding;
        DateTimeOffset observedAt = input.ObservedAt.ToUniversalTime();
        DateTimeOffset nowUtc = input.NowUtc.ToUniversalTime();
        CdcDiagnosticCollector diagnostics = new(observedAt);

        if (TryUseValidLatchedIncident(input, binding, observedAt, nowUtc, diagnostics) is { } validLatch)
        {
            return validLatch;
        }

        AddDiagnostics(diagnostics, input.Diagnostics);

        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        AddDiagnostics(diagnostics, artifactNameResult.Diagnostics);
        if (artifactNameResult.Inventory is null)
        {
            return Unknown(input, observedAt, diagnostics);
        }

        CdcArtifactInventory inventory = artifactNameResult.Inventory;
        string? expectedSourcePartitionHash = ResolveExpectedSourcePartitionHash(
            input,
            inventory,
            diagnostics
        );
        if (diagnostics.HasDiagnostics)
        {
            return Unknown(input, observedAt, diagnostics);
        }

        if (
            TryUseLatchedIncident(
                input,
                binding,
                inventory,
                expectedSourcePartitionHash,
                observedAt,
                nowUtc,
                diagnostics
            ) is
            { } latched
        )
        {
            return latched;
        }

        if (
            TryEvaluateProviderSetup(
                input,
                binding,
                inventory,
                expectedSourcePartitionHash,
                observedAt,
                nowUtc,
                diagnostics
            ) is
            { } setupResult
        )
        {
            return setupResult;
        }

        if (
            TryEvaluateTerminalProviderHistoryWithoutConnectorOffset(
                input,
                inventory,
                expectedSourcePartitionHash,
                observedAt,
                diagnostics
            ) is
            { } terminalProviderResult
        )
        {
            return terminalProviderResult;
        }

        CdcConnectorOffsetEvaluation offsetEvaluation = EvaluateConnectorOffset(
            input,
            binding,
            inventory,
            expectedSourcePartitionHash,
            observedAt,
            nowUtc,
            diagnostics
        );
        if (offsetEvaluation.Result is not null)
        {
            return offsetEvaluation.Result;
        }

        CdcCommittedSourcePosition committedPosition =
            offsetEvaluation.Position
            ?? throw new InvalidOperationException("CDC connector offset parsing must produce a position.");

        if (
            TryEvaluateProviderHistory(
                input,
                inventory,
                expectedSourcePartitionHash,
                committedPosition,
                observedAt,
                diagnostics
            ) is
            { } providerResult
        )
        {
            return providerResult;
        }

        if (
            TryEvaluateSqlServerSchemaHistory(
                input,
                inventory,
                expectedSourcePartitionHash,
                committedPosition,
                observedAt,
                diagnostics
            ) is
            { } schemaResult
        )
        {
            return schemaResult;
        }

        CdcIncidentPositionMetadata positionMetadata = BuildPositionMetadata(
            binding.Provider,
            inventory,
            expectedSourcePartitionHash,
            committedPosition,
            input.ProviderHistory,
            []
        );

        return Healthy(input, observedAt, diagnostics, positionMetadata);
    }

    private static CdcSourceHistoryClassificationResult? TryUseValidLatchedIncident(
        CdcSourceHistoryClassificationInput input,
        CdcBinding binding,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.LatchedIncident is null)
        {
            return null;
        }

        CdcContractValidationResult validation = CdcIncidentValidator.ValidateForBinding(
            input.LatchedIncident,
            binding,
            nowUtc
        );

        CdcSourceHistoryClassificationInput latchedInput = input with
        {
            ProviderHistory = null,
            SqlServerSchemaHistory = null,
        };

        return validation.Succeeded
            ? Lost(
                latchedInput,
                observedAt,
                diagnostics,
                input.LatchedIncident.FailureCategory,
                input.LatchedIncident.PositionMetadata,
                incidentLatched: true,
                createCandidate: false
            )
            : null;
    }

    private static CdcSourceHistoryClassificationResult? TryUseLatchedIncident(
        CdcSourceHistoryClassificationInput input,
        CdcBinding binding,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.LatchedIncident is null)
        {
            return null;
        }

        CdcContractValidationResult validation = CdcIncidentValidator.ValidateForBinding(
            input.LatchedIncident,
            binding,
            nowUtc
        );
        AddPrefixedDiagnostics(diagnostics, validation.Diagnostics, "$.latchedIncident");

        return validation.Succeeded
            ? Lost(
                input,
                observedAt,
                diagnostics,
                input.LatchedIncident.FailureCategory,
                input.LatchedIncident.PositionMetadata,
                incidentLatched: true,
                createCandidate: false
            )
            : UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                null,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            );
    }

    private static CdcSourceHistoryClassificationResult? TryEvaluateProviderSetup(
        CdcSourceHistoryClassificationInput input,
        CdcBinding binding,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.ProviderSetup is null)
        {
            diagnostics.MissingRequiredField("$.providerSetup", "providerSetup");
            return Unknown(input, observedAt, diagnostics);
        }

        AddDiagnostics(diagnostics, input.ProviderSetup.Diagnostics);
        CdcContractValidationResult validation = CdcProviderSetupObservationValidator.Validate(
            input.ProviderSetup,
            new(input.OperationId, binding.ToTargetIdentity(), binding.PhysicalSourceFingerprint, nowUtc)
        );
        AddPrefixedDiagnostics(diagnostics, validation.Diagnostics, "$.providerSetup");
        if (!validation.Succeeded)
        {
            return Unknown(input, observedAt, diagnostics);
        }

        if (
            !HasTerminalProviderHistory(input.ProviderHistory)
            && (
                input.ProviderSetup.SetupOutcome != CdcProviderSetupOutcome.Satisfied
                || HasProviderSetupState(input.ProviderSetup, CdcProviderSetupState.Unknown)
                || HasProviderSetupState(input.ProviderSetup, CdcProviderSetupState.Missing)
                || HasProviderSetupState(input.ProviderSetup, CdcProviderSetupState.Mismatched)
            )
        )
        {
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                null,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderRetainedRange]
            );
        }

        return null;
    }

    private static bool HasTerminalProviderHistory(CdcProviderSourceHistoryEvidence? providerHistory) =>
        providerHistory?.ProviderArtifactState
            is CdcProviderArtifactContinuityState.Missing
                or CdcProviderArtifactContinuityState.Recreated
        || providerHistory?.RetainedRangeState == CdcProviderRetainedRangeState.Gap
        || providerHistory?.SqlServerJobs?.HasMissingJob == true;

    private static CdcSourceHistoryClassificationResult? TryEvaluateTerminalProviderHistoryWithoutConnectorOffset(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.ConnectorOffset is not null || !HasTerminalProviderHistory(input.ProviderHistory))
        {
            return null;
        }

        CdcProviderSourceHistoryEvidence providerHistory =
            input.ProviderHistory
            ?? throw new InvalidOperationException(
                "Terminal provider history cannot be detected without provider history evidence."
            );
        AddDiagnostics(diagnostics, providerHistory.Diagnostics);
        diagnostics.LocalStateUnavailable(
            "$.connectorOffset",
            "CDC connector offset evidence is unavailable, but terminal provider source-history evidence was observed."
        );

        if (!Enum.IsDefined(providerHistory.ProviderArtifactState))
        {
            diagnostics.InvalidEnumValue(
                "$.providerHistory.providerArtifactState",
                "CDC provider source-history artifact state is unsupported."
            );
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                input.Binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                null,
                providerHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact, CdcIncidentUnavailableFact.ConnectOffset]
            );
        }

        if (!Enum.IsDefined(providerHistory.RetainedRangeState))
        {
            diagnostics.InvalidEnumValue(
                "$.providerHistory.retainedRangeState",
                "CDC provider source-history retained range state is unsupported."
            );
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                input.Binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                null,
                providerHistory,
                [CdcIncidentUnavailableFact.ProviderRetainedRange, CdcIncidentUnavailableFact.ConnectOffset]
            );
        }

        if (providerHistory.ProviderArtifactState == CdcProviderArtifactContinuityState.Missing)
        {
            return Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.ProviderArtifactMissing,
                BuildPositionMetadata(
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    providerHistory,
                    [CdcIncidentUnavailableFact.ProviderArtifact, CdcIncidentUnavailableFact.ConnectOffset]
                )
            );
        }

        if (providerHistory.ProviderArtifactState == CdcProviderArtifactContinuityState.Recreated)
        {
            return Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.ProviderArtifactRecreated,
                BuildPositionMetadata(
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    providerHistory,
                    [CdcIncidentUnavailableFact.ProviderArtifact, CdcIncidentUnavailableFact.ConnectOffset]
                )
            );
        }

        if (providerHistory.RetainedRangeState == CdcProviderRetainedRangeState.Gap)
        {
            return Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.RetainedHistoryGap,
                BuildPositionMetadata(
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    providerHistory,
                    [CdcIncidentUnavailableFact.ConnectOffset]
                )
            );
        }

        if (input.Binding.Provider != CdcProvider.SqlServer)
        {
            return null;
        }

        CdcSqlServerCdcJobEvidence? sqlServerJobs = providerHistory.SqlServerJobs;
        if (sqlServerJobs is null)
        {
            diagnostics.MissingRequiredField("$.providerHistory.sqlServerJobs", "sqlServerJobs");
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                null,
                providerHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact, CdcIncidentUnavailableFact.ConnectOffset]
            );
        }

        if (!sqlServerJobs.HasMissingJob)
        {
            return null;
        }

        diagnostics.Add(
            CdcDiagnosticCategory.InvalidObservation,
            "$.providerHistory.sqlServerJobs",
            "CDC SQL Server capture and cleanup jobs must both exist."
        );
        return Lost(
            input,
            observedAt,
            diagnostics,
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            BuildPositionMetadata(
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                null,
                providerHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact, CdcIncidentUnavailableFact.ConnectOffset]
            )
        );
    }

    private static CdcConnectorOffsetEvaluation EvaluateConnectorOffset(
        CdcSourceHistoryClassificationInput input,
        CdcBinding binding,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.ConnectorOffset is null)
        {
            diagnostics.LocalStateUnavailable(
                "$.connectorOffset",
                "CDC connector offset evidence is unavailable."
            );
            return new(
                UnknownWithMetadata(
                    input,
                    observedAt,
                    diagnostics,
                    binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ConnectOffset]
                ),
                null
            );
        }

        AddDiagnostics(diagnostics, input.ConnectorOffset.Diagnostics);

        CdcDiagnosticCollector envelopeDiagnostics = new(observedAt);
        CdcObservationValidationRules.ValidateEnvelope(
            input.ConnectorOffset,
            new(input.OperationId, binding.ToTargetIdentity(), binding.PhysicalSourceFingerprint, nowUtc),
            envelopeDiagnostics
        );
        ValidateConnectorOffsetBindingNames(input.ConnectorOffset, inventory, envelopeDiagnostics);
        CdcObservationValidationRules.ValidateHashValue(
            input.ConnectorOffset.ConnectSourcePartitionHash,
            "$.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            true,
            envelopeDiagnostics
        );
        AddPrefixedDiagnostics(diagnostics, envelopeDiagnostics.Diagnostics, "$.connectorOffset");
        if (envelopeDiagnostics.HasDiagnostics)
        {
            return new(
                UnknownWithMetadata(
                    input,
                    observedAt,
                    diagnostics,
                    binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ConnectOffset]
                ),
                null
            );
        }

        CdcDiagnosticCollector sourcePartitionHashDiagnostics = new(observedAt);
        if (
            !ValidateExpectedSourcePartitionHash(
                input.ConnectorOffset.ConnectSourcePartitionHash,
                expectedSourcePartitionHash,
                sourcePartitionHashDiagnostics
            )
        )
        {
            AddPrefixedDiagnostics(
                diagnostics,
                sourcePartitionHashDiagnostics.Diagnostics,
                "$.connectorOffset"
            );
            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectSourcePartitionMismatch,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        if (input.ConnectorOffset.SourcePartitionMatchResult == CdcConnectorOffsetMatchResult.Unavailable)
        {
            // The worker was not asked successfully. An answer that was never obtained disproves
            // nothing, so continuity is unknown - readiness stays false and neither start nor resume
            // is automated, but no terminal loss is latched over an offset nobody read.
            diagnostics.Add(
                CdcDiagnosticCategory.StatusObservationUnavailable,
                "$.connectorOffset.sourcePartitionMatchResult",
                "CDC connector offset evidence was not obtained from the worker."
            );

            return new(
                UnknownWithMetadata(
                    input,
                    observedAt,
                    diagnostics,
                    binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    null,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ConnectOffset]
                ),
                null
            );
        }

        if (input.ConnectorOffset.SourcePartitionMatchResult == CdcConnectorOffsetMatchResult.Missing)
        {
            // Terminal for an ESTABLISHED binding only, which is the scope this classification has
            // always been given. A binding whose public topic has never carried a record has published
            // nothing: no consumer can hold state from it, and there is no committed position for the
            // source to have aged out from under - the connector has simply not committed its first
            // offset yet, because the enablement that would have produced it has not finished. Latching
            // there would turn an interrupted initial setup into a terminal history loss and close the
            // documented retry, which refuses an incident-latched binding.
            //
            // Evidence that could not be read withholds the terminal classification too. It is not
            // proof that the stream is established, and the irreversible action needs proof.
            if (input.PublicTopicPublication?.ProvesEstablishedStream != true)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.StatusObservationUnavailable,
                    "$.connectorOffset.sourcePartitionMatchResult",
                    "CDC connector has committed no offset and its public topic does not prove a stream "
                        + "this loss could be terminal for."
                );

                return new(
                    UnknownWithMetadata(
                        input,
                        observedAt,
                        diagnostics,
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    ),
                    null
                );
            }

            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectOffsetMissing,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        if (
            input.ConnectorOffset.SourcePartitionMatchResult
            is CdcConnectorOffsetMatchResult.Multiple
                or CdcConnectorOffsetMatchResult.SourcePartitionMismatch
        )
        {
            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectSourcePartitionMismatch,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        if (input.ConnectorOffset.IsSnapshot)
        {
            // A snapshot offset is the connector's own progress through a phase that has not reached
            // streaming yet, not a position this classification can decide continuity from. It is
            // separated from the malformed offset below deliberately: latching is irreversible and
            // closes the documented enable retry, which refuses an incident-latched binding, and an
            // enablement whose initial snapshot was interrupted is the ordinary way to arrive here.
            // Readiness stays false and neither start nor resume is automated, which is what a phase
            // still in progress warrants.
            //
            // That exception is unfinished initial provisioning only, and it is narrowed by the same
            // evidence the missing-offset branch above is narrowed by. An ESTABLISHED stream - one
            // whose public topic has carried a record - that is back in a snapshot has lost the
            // streaming position it would have resumed from, and the snapshot replacing it reads
            // current rows: it cannot reconstruct a delete that happened while the stream was down, so
            // a consumer holding cache state keeps an entry for a document that is gone. Left unlatched
            // this heals itself out of sight, because the snapshot commits a fresh streaming offset and
            // every observation after that classifies healthy.
            if (input.PublicTopicPublication?.ProvesEstablishedStream != true)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.StatusObservationUnavailable,
                    "$.connectorOffset.isSnapshot",
                    "CDC connector has committed only a snapshot position and its public topic does not "
                        + "prove a stream this loss could be terminal for."
                );

                return new(
                    UnknownWithMetadata(
                        input,
                        observedAt,
                        diagnostics,
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    ),
                    null
                );
            }

            diagnostics.Add(
                CdcDiagnosticCategory.SourceHistoryLost,
                "$.connectorOffset.isSnapshot",
                "CDC connector of an established stream has committed only a snapshot position, so the "
                    + "streaming position it would resume from is gone."
            );

            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectOffsetMissing,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        if (input.ConnectorOffset.IsNull)
        {
            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectOffsetMalformed,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        CdcCommittedSourcePositionResult positionResult = ParseCommittedSourcePosition(
            input.ConnectorOffset,
            binding.Provider
        );
        AddPrefixedDiagnostics(diagnostics, positionResult.Diagnostics, "$.connectorOffset");
        if (!positionResult.Succeeded || positionResult.Position is null)
        {
            return new(
                Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.ConnectOffsetMalformed,
                    BuildPositionMetadata(
                        binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        null,
                        input.ProviderHistory,
                        [CdcIncidentUnavailableFact.ConnectOffset]
                    )
                ),
                null
            );
        }

        return new(null, positionResult.Position);
    }

    private static CdcSourceHistoryClassificationResult? TryEvaluateProviderHistory(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition position,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.ProviderHistory is null)
        {
            diagnostics.LocalStateUnavailable(
                "$.providerHistory",
                "CDC provider source-history evidence is unavailable."
            );
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                input.Binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                position,
                null,
                [CdcIncidentUnavailableFact.ProviderRetainedRange]
            );
        }

        AddDiagnostics(diagnostics, input.ProviderHistory.Diagnostics);
        if (!Enum.IsDefined(input.ProviderHistory.ProviderArtifactState))
        {
            diagnostics.InvalidEnumValue(
                "$.providerHistory.providerArtifactState",
                "CDC provider source-history artifact state is unsupported."
            );
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                input.Binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            );
        }

        if (input.ProviderHistory.ProviderArtifactState == CdcProviderArtifactContinuityState.Missing)
        {
            return Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.ProviderArtifactMissing,
                BuildPositionMetadata(
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    position,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ProviderArtifact]
                )
            );
        }

        if (input.ProviderHistory.ProviderArtifactState == CdcProviderArtifactContinuityState.Recreated)
        {
            return Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.ProviderArtifactRecreated,
                BuildPositionMetadata(
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    position,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ProviderArtifact]
                )
            );
        }

        if (input.ProviderHistory.ProviderArtifactState == CdcProviderArtifactContinuityState.Unknown)
        {
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                input.Binding.Provider,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            );
        }

        if (
            TryEvaluateMissingSqlServerJob(
                input,
                inventory,
                expectedSourcePartitionHash,
                position,
                observedAt,
                diagnostics
            ) is
            { } missingJobResult
        )
        {
            return missingJobResult;
        }

        CdcRetainedRangeEvaluation retainedRange = EvaluateRetainedRange(
            input.Binding.Provider,
            input.ProviderHistory,
            position
        );
        AddDiagnostics(diagnostics, retainedRange.Diagnostics);

        switch (retainedRange.State)
        {
            case CdcRetainedRangeEvaluationState.Covers:
                break;
            case CdcRetainedRangeEvaluationState.Gap:
                return Lost(
                    input,
                    observedAt,
                    diagnostics,
                    CdcIncidentFailureCategory.RetainedHistoryGap,
                    BuildPositionMetadata(
                        input.Binding.Provider,
                        inventory,
                        expectedSourcePartitionHash,
                        position,
                        input.ProviderHistory,
                        []
                    )
                );
            default:
                return UnknownWithMetadata(
                    input,
                    observedAt,
                    diagnostics,
                    input.Binding.Provider,
                    inventory,
                    expectedSourcePartitionHash,
                    position,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.ProviderRetainedRange]
                );
        }

        if (
            TryEvaluateUnavailableSqlServerJob(
                input,
                inventory,
                expectedSourcePartitionHash,
                position,
                observedAt,
                diagnostics
            ) is
            { } unavailableJobResult
        )
        {
            return unavailableJobResult;
        }

        return null;
    }

    private static CdcSourceHistoryClassificationResult? TryEvaluateMissingSqlServerJob(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition position,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.Binding.Provider != CdcProvider.SqlServer)
        {
            return null;
        }

        CdcSqlServerCdcJobEvidence? sqlServerJobs = input.ProviderHistory?.SqlServerJobs;
        if (
            TryValidateSqlServerJobEvidence(
                sqlServerJobs,
                input,
                inventory,
                expectedSourcePartitionHash,
                position,
                observedAt,
                diagnostics
            ) is
            { } validationResult
        )
        {
            return validationResult;
        }

        if (!sqlServerJobs!.HasMissingJob)
        {
            return null;
        }

        diagnostics.Add(
            CdcDiagnosticCategory.InvalidObservation,
            "$.providerHistory.sqlServerJobs",
            "CDC SQL Server capture and cleanup jobs must both exist."
        );
        return Lost(
            input,
            observedAt,
            diagnostics,
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            BuildPositionMetadata(
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
        );
    }

    private static CdcSourceHistoryClassificationResult? TryValidateSqlServerJobEvidence(
        CdcSqlServerCdcJobEvidence? sqlServerJobs,
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition position,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (sqlServerJobs is null)
        {
            diagnostics.MissingRequiredField("$.providerHistory.sqlServerJobs", "sqlServerJobs");
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            );
        }

        bool captureJobStateDefined = Enum.IsDefined(sqlServerJobs.CaptureJobState);
        if (!captureJobStateDefined)
        {
            diagnostics.InvalidEnumValue(
                "$.providerHistory.sqlServerJobs.captureJobState",
                "CDC SQL Server capture job state is unsupported."
            );
        }

        bool cleanupJobStateDefined = Enum.IsDefined(sqlServerJobs.CleanupJobState);
        if (!cleanupJobStateDefined)
        {
            diagnostics.InvalidEnumValue(
                "$.providerHistory.sqlServerJobs.cleanupJobState",
                "CDC SQL Server cleanup job state is unsupported."
            );
        }

        return captureJobStateDefined && cleanupJobStateDefined
            ? null
            : UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            );
    }

    private static CdcSourceHistoryClassificationResult? TryEvaluateUnavailableSqlServerJob(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition position,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.Binding.Provider != CdcProvider.SqlServer)
        {
            return null;
        }

        CdcSqlServerCdcJobEvidence sqlServerJobs =
            input.ProviderHistory?.SqlServerJobs
            ?? throw new InvalidOperationException("SQL Server job evidence must be validated first.");

        if (sqlServerJobs.IsHealthy)
        {
            return null;
        }

        if (sqlServerJobs.HasStoppedOrFailedJob)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.providerHistory.sqlServerJobs",
                "CDC SQL Server capture and cleanup jobs must both be healthy."
            );
        }
        else if (sqlServerJobs.HasUnknownJob && input.ProviderHistory!.Diagnostics.Count == 0)
        {
            diagnostics.LocalStateUnavailable(
                "$.providerHistory.sqlServerJobs",
                "CDC SQL Server capture and cleanup job health is unavailable."
            );
        }

        return UnknownWithMetadata(
            input,
            observedAt,
            diagnostics,
            CdcProvider.SqlServer,
            inventory,
            expectedSourcePartitionHash,
            position,
            input.ProviderHistory,
            [CdcIncidentUnavailableFact.ProviderArtifact]
        );
    }

    private static CdcSourceHistoryClassificationResult? TryEvaluateSqlServerSchemaHistory(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition position,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.Binding.Provider == CdcProvider.Postgresql)
        {
            return null;
        }

        if (input.SqlServerSchemaHistory is null)
        {
            diagnostics.LocalStateUnavailable(
                "$.sqlServerSchemaHistory",
                "CDC SQL Server schema-history evidence is unavailable."
            );
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.SchemaHistory]
            );
        }

        AddDiagnostics(diagnostics, input.SqlServerSchemaHistory.Diagnostics);
        bool enablementPhaseDefined = Enum.IsDefined(input.SqlServerSchemaHistory.EnablementPhase);
        if (!enablementPhaseDefined)
        {
            diagnostics.InvalidEnumValue(
                "$.sqlServerSchemaHistory.enablementPhase",
                "CDC SQL Server schema-history enablement phase is unsupported."
            );
        }

        bool stateDefined = Enum.IsDefined(input.SqlServerSchemaHistory.State);
        if (!stateDefined)
        {
            diagnostics.InvalidEnumValue(
                "$.sqlServerSchemaHistory.state",
                "CDC SQL Server schema-history state is unsupported."
            );
        }

        if (!enablementPhaseDefined || !stateDefined)
        {
            return Unknown(
                input,
                observedAt,
                diagnostics,
                input.ProviderHistory?.ProviderArtifactState ?? CdcProviderArtifactContinuityState.ExactMatch,
                input.ProviderHistory?.RetainedRangeState
                    ?? CdcProviderRetainedRangeState.CoversCommittedOffset,
                BuildPositionMetadata(
                    CdcProvider.SqlServer,
                    inventory,
                    expectedSourcePartitionHash,
                    position,
                    input.ProviderHistory,
                    [CdcIncidentUnavailableFact.SchemaHistory]
                ),
                enablementPhaseDefined
                    ? input.SqlServerSchemaHistory.EnablementPhase
                    : CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                CdcSqlServerSchemaHistoryState.Unknown
            );
        }

        if (input.SqlServerSchemaHistory.State == CdcSqlServerSchemaHistoryState.Valid)
        {
            return null;
        }

        if (
            input.SqlServerSchemaHistory.State
            is CdcSqlServerSchemaHistoryState.Unknown
                or CdcSqlServerSchemaHistoryState.Unreadable
        )
        {
            return UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.SchemaHistory]
            );
        }

        CdcIncidentPositionMetadata metadata = BuildPositionMetadata(
            CdcProvider.SqlServer,
            inventory,
            expectedSourcePartitionHash,
            position,
            input.ProviderHistory,
            [CdcIncidentUnavailableFact.SchemaHistory]
        );

        if (
            input.SqlServerSchemaHistory.EnablementPhase
            == CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission
        )
        {
            return Unknown(
                input,
                observedAt,
                diagnostics,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset,
                metadata,
                CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
                input.SqlServerSchemaHistory.State
            );
        }

        return input.SqlServerSchemaHistory.State switch
        {
            CdcSqlServerSchemaHistoryState.Missing => Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.SchemaHistoryMissing,
                metadata
            ),
            CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset => Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.SchemaHistoryEmptyWithRetainedOffset,
                metadata
            ),
            CdcSqlServerSchemaHistoryState.RequiredRecordLost => Lost(
                input,
                observedAt,
                diagnostics,
                CdcIncidentFailureCategory.SchemaHistoryRequiredRecordLost,
                metadata
            ),
            _ => UnknownWithMetadata(
                input,
                observedAt,
                diagnostics,
                CdcProvider.SqlServer,
                inventory,
                expectedSourcePartitionHash,
                position,
                input.ProviderHistory,
                [CdcIncidentUnavailableFact.SchemaHistory]
            ),
        };
    }

    private static string? ResolveExpectedSourcePartitionHash(
        CdcSourceHistoryClassificationInput input,
        CdcArtifactInventory inventory,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.ExpectedConnectSourcePartitionHash is not null)
        {
            CdcObservationValidationRules.ValidateHashValue(
                input.ExpectedConnectSourcePartitionHash,
                "$.expectedConnectSourcePartitionHash",
                "expectedConnectSourcePartitionHash",
                true,
                diagnostics
            );
            return input.ExpectedConnectSourcePartitionHash;
        }

        if (input.Binding.Provider != CdcProvider.Postgresql)
        {
            diagnostics.LocalStateUnavailable(
                "$.expectedConnectSourcePartitionHash",
                "CDC SQL Server expected Connect source-partition hash evidence is unavailable."
            );
            return null;
        }

        CdcSourcePartitionHashResult result = CdcSourcePartitionHashCalculator.ComputePostgresql(
            inventory.ConnectorName
        );
        AddDiagnostics(diagnostics, result.Diagnostics);

        return result.Hash;
    }

    private static void ValidateConnectorOffsetBindingNames(
        CdcConnectorOffsetObservation offset,
        CdcArtifactInventory inventory,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!string.Equals(offset.ConnectorName, inventory.ConnectorName, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.connectorName",
                "CDC connector offset connectorName must match the binding-derived inventory."
            );
        }

        if (!string.Equals(offset.TopicPrefix, inventory.ConnectorName, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.topicPrefix",
                "CDC connector offset topicPrefix must match the binding-derived connector name."
            );
        }
    }

    private static bool ValidateExpectedSourcePartitionHash(
        string observedHash,
        string? expectedHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (expectedHash is null)
        {
            return true;
        }

        if (!string.Equals(observedHash, expectedHash, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                "$.connectSourcePartitionHash",
                "CDC connector offset source partition hash must match the expected Connect source partition."
            );
            return false;
        }

        return true;
    }

    private static CdcCommittedSourcePositionResult ParseCommittedSourcePosition(
        CdcConnectorOffsetObservation offset,
        CdcProvider provider
    ) =>
        provider == CdcProvider.Postgresql
            ? ParsePostgresqlCommittedSourcePosition(offset)
            : ParseSqlServerCommittedSourcePosition(offset);

    private static CdcCommittedSourcePositionResult ParsePostgresqlCommittedSourcePosition(
        CdcConnectorOffsetObservation offset
    )
    {
        CdcProviderPositionComparisonResult comparison =
            CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                new(0),
                new(offset.SourcePartitionMatchResult, offset.IsSnapshot, offset.IsNull, offset.LsnProc)
            );

        return comparison.Succeeded && comparison.CommittedPosition is not null
            ? CdcCommittedSourcePositionResult.Success(
                new(comparison.CommittedPosition, null, null, null, null)
            )
            : CdcCommittedSourcePositionResult.Failure(comparison.Diagnostics);
    }

    private static CdcCommittedSourcePositionResult ParseSqlServerCommittedSourcePosition(
        CdcConnectorOffsetObservation offset
    )
    {
        CdcProviderPositionComparisonResult comparison =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                new(new(0, 0, 0), new(0, 0, 0), 0),
                new(
                    offset.SourcePartitionMatchResult,
                    offset.IsSnapshot,
                    offset.IsNull,
                    offset.CommitLsn,
                    offset.ChangeLsn,
                    offset.EventSerialNo
                )
            );

        if (!comparison.Succeeded || offset.EventSerialNo is null)
        {
            return CdcCommittedSourcePositionResult.Failure(comparison.Diagnostics);
        }

        CdcSqlServerLsnResult commitLsn = CdcSqlServerProviderPositionParser.ParseLsn(
            offset.CommitLsn,
            "$.commitLsn"
        );
        CdcSqlServerLsnResult changeLsn = CdcSqlServerProviderPositionParser.ParseLsn(
            offset.ChangeLsn,
            "$.changeLsn"
        );
        if (commitLsn.Lsn is null || changeLsn.Lsn is null)
        {
            return CdcCommittedSourcePositionResult.Failure([
                .. comparison.Diagnostics,
                .. commitLsn.Diagnostics,
                .. changeLsn.Diagnostics,
            ]);
        }

        return CdcCommittedSourcePositionResult.Success(
            new(
                null,
                commitLsn.Lsn.Value.ToString(),
                changeLsn.Lsn.Value.ToString(),
                offset.EventSerialNo,
                comparison.CommittedPosition
            )
        );
    }

    private static CdcRetainedRangeEvaluation EvaluateRetainedRange(
        CdcProvider provider,
        CdcProviderSourceHistoryEvidence providerHistory,
        CdcCommittedSourcePosition committedPosition
    )
    {
        if (!Enum.IsDefined(providerHistory.RetainedRangeState))
        {
            return CdcRetainedRangeEvaluation.UnknownResult([
                new(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    "$.providerHistory.retainedRangeState",
                    "CDC provider source-history retained range state is unsupported."
                ),
            ]);
        }

        if (providerHistory.RetainedRangeState == CdcProviderRetainedRangeState.Unknown)
        {
            return CdcRetainedRangeEvaluation.UnknownResult([]);
        }

        if (providerHistory.RetainedRangeState == CdcProviderRetainedRangeState.Gap)
        {
            return CdcRetainedRangeEvaluation.Gap([]);
        }

        return provider == CdcProvider.Postgresql
            ? EvaluatePostgresqlRetainedRange(providerHistory, committedPosition)
            : EvaluateSqlServerRetainedRange(providerHistory, committedPosition);
    }

    private static CdcRetainedRangeEvaluation EvaluatePostgresqlRetainedRange(
        CdcProviderSourceHistoryEvidence providerHistory,
        CdcCommittedSourcePosition committedPosition
    )
    {
        CdcPostgresqlWalPositionResult committed = CdcPostgresqlProviderPosition.ParseWalLsn(
            committedPosition.LsnProc,
            "$.connectorOffset.lsnProc"
        );
        CdcPostgresqlWalPositionResult start = CdcPostgresqlProviderPosition.ParseWalLsn(
            providerHistory.RetainedRangeStart,
            "$.providerHistory.retainedRangeStart"
        );
        CdcPostgresqlWalPositionResult end = CdcPostgresqlProviderPosition.ParseWalLsn(
            providerHistory.RetainedRangeEnd,
            "$.providerHistory.retainedRangeEnd"
        );
        IReadOnlyList<CdcDiagnostic> diagnostics =
        [
            .. committed.Diagnostics,
            .. start.Diagnostics,
            .. end.Diagnostics,
        ];

        if (committed.Position is null || start.Position is null || end.Position is null)
        {
            return CdcRetainedRangeEvaluation.UnknownResult(diagnostics);
        }

        if (
            committed.Position.Value.CompareTo(start.Position.Value) < 0
            || committed.Position.Value.CompareTo(end.Position.Value) > 0
        )
        {
            return CdcRetainedRangeEvaluation.Gap(diagnostics);
        }

        return start.Position.Value.CompareTo(end.Position.Value) <= 0
            ? CdcRetainedRangeEvaluation.Covers(diagnostics)
            : CdcRetainedRangeEvaluation.UnknownResult([
                .. diagnostics,
                new(
                    CdcDiagnosticCategory.InvalidOrdering,
                    "$.providerHistory.retainedRangeStart",
                    "CDC provider retained range start must not be after retained range end."
                ),
            ]);
    }

    private static CdcRetainedRangeEvaluation EvaluateSqlServerRetainedRange(
        CdcProviderSourceHistoryEvidence providerHistory,
        CdcCommittedSourcePosition committedPosition
    )
    {
        CdcSqlServerLsnResult committed = CdcSqlServerProviderPositionParser.ParseLsn(
            committedPosition.CommitLsn,
            "$.connectorOffset.commitLsn"
        );
        CdcSqlServerLsnResult start = CdcSqlServerProviderPositionParser.ParseLsn(
            providerHistory.RetainedRangeStart,
            "$.providerHistory.retainedRangeStart"
        );
        CdcSqlServerLsnResult end = CdcSqlServerProviderPositionParser.ParseLsn(
            providerHistory.RetainedRangeEnd,
            "$.providerHistory.retainedRangeEnd"
        );
        IReadOnlyList<CdcDiagnostic> diagnostics =
        [
            .. committed.Diagnostics,
            .. start.Diagnostics,
            .. end.Diagnostics,
        ];

        if (committed.Lsn is null || start.Lsn is null || end.Lsn is null)
        {
            return CdcRetainedRangeEvaluation.UnknownResult(diagnostics);
        }

        if (
            committed.Lsn.Value.CompareTo(start.Lsn.Value) < 0
            || committed.Lsn.Value.CompareTo(end.Lsn.Value) > 0
        )
        {
            return CdcRetainedRangeEvaluation.Gap(diagnostics);
        }

        return start.Lsn.Value.CompareTo(end.Lsn.Value) <= 0
            ? CdcRetainedRangeEvaluation.Covers(diagnostics)
            : CdcRetainedRangeEvaluation.UnknownResult([
                .. diagnostics,
                new(
                    CdcDiagnosticCategory.InvalidOrdering,
                    "$.providerHistory.retainedRangeStart",
                    "CDC provider retained range start must not be after retained range end."
                ),
            ]);
    }

    private static CdcIncidentPositionMetadata BuildPositionMetadata(
        CdcProvider provider,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition? committedPosition,
        CdcProviderSourceHistoryEvidence? providerHistory,
        IReadOnlyList<CdcIncidentUnavailableFact> additionalUnavailableFacts
    )
    {
        HashSet<CdcIncidentUnavailableFact> unavailableFacts = [];
        if (providerHistory?.UnavailableFacts is not null)
        {
            foreach (CdcIncidentUnavailableFact fact in providerHistory.UnavailableFacts)
            {
                unavailableFacts.Add(fact);
            }
        }

        foreach (CdcIncidentUnavailableFact fact in additionalUnavailableFacts)
        {
            unavailableFacts.Add(fact);
        }

        return new(
            inventory.ConnectorName,
            inventory.TopicName,
            inventory.ProgressTopicName,
            inventory.SchemaHistoryTopicName,
            providerHistory?.ProviderArtifactName ?? DefaultProviderArtifactName(provider, inventory),
            expectedSourcePartitionHash,
            committedPosition?.LsnProc,
            committedPosition?.CommitLsn,
            committedPosition?.ChangeLsn,
            committedPosition?.EventSerialNo,
            providerHistory?.RetainedRangeStart,
            providerHistory?.RetainedRangeEnd,
            unavailableFacts.Order().ToArray()
        );
    }

    private static string? DefaultProviderArtifactName(
        CdcProvider provider,
        CdcArtifactInventory inventory
    ) =>
        provider == CdcProvider.Postgresql
            ? inventory.PostgresqlLogicalSlotName
            : inventory.SqlServerCaptureInstanceCdcHeartbeatName;

    private static CdcSourceHistoryClassificationResult Healthy(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics,
        CdcIncidentPositionMetadata positionMetadata
    ) =>
        new(
            CreateObservation(
                input,
                observedAt,
                CdcSourceHistoryContinuity.Healthy,
                incidentLatched: false,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset,
                positionMetadata,
                null,
                SqlServerPhase(input),
                SqlServerState(input),
                diagnostics.Diagnostics
            ),
            null
        );

    private static CdcSourceHistoryClassificationResult Unknown(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    ) =>
        Unknown(
            input,
            observedAt,
            diagnostics,
            input.ProviderHistory?.ProviderArtifactState ?? CdcProviderArtifactContinuityState.Unknown,
            input.ProviderHistory?.RetainedRangeState ?? CdcProviderRetainedRangeState.Unknown,
            null,
            SqlServerPhase(input),
            SqlServerState(input)
        );

    private static CdcSourceHistoryClassificationResult UnknownWithMetadata(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics,
        CdcProvider provider,
        CdcArtifactInventory inventory,
        string? expectedSourcePartitionHash,
        CdcCommittedSourcePosition? committedPosition,
        CdcProviderSourceHistoryEvidence? providerHistory,
        IReadOnlyList<CdcIncidentUnavailableFact> unavailableFacts
    ) =>
        Unknown(
            input,
            observedAt,
            diagnostics,
            providerHistory?.ProviderArtifactState ?? CdcProviderArtifactContinuityState.Unknown,
            providerHistory?.RetainedRangeState ?? CdcProviderRetainedRangeState.Unknown,
            BuildPositionMetadata(
                provider,
                inventory,
                expectedSourcePartitionHash,
                committedPosition,
                providerHistory,
                unavailableFacts
            ),
            SqlServerPhase(input),
            SqlServerState(input)
        );

    private static CdcSourceHistoryClassificationResult Unknown(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics,
        CdcProviderArtifactContinuityState providerArtifactState,
        CdcProviderRetainedRangeState retainedRangeState,
        CdcIncidentPositionMetadata? positionMetadata,
        CdcSqlServerSchemaHistoryEnablementPhase? schemaHistoryEnablementPhase,
        CdcSqlServerSchemaHistoryState schemaHistoryState
    ) =>
        new(
            CreateObservation(
                input,
                observedAt,
                CdcSourceHistoryContinuity.Unknown,
                incidentLatched: false,
                providerArtifactState,
                retainedRangeState,
                positionMetadata,
                null,
                schemaHistoryEnablementPhase,
                schemaHistoryState,
                diagnostics.Diagnostics
            ),
            null
        );

    private static CdcSourceHistoryClassificationResult Lost(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics,
        CdcIncidentFailureCategory failureCategory,
        CdcIncidentPositionMetadata positionMetadata,
        bool incidentLatched = false,
        bool createCandidate = true
    )
    {
        CdcSourceHistoryObservation observation = CreateObservation(
            input,
            observedAt,
            CdcSourceHistoryContinuity.Lost,
            incidentLatched,
            LostProviderArtifactState(failureCategory, input.ProviderHistory),
            LostRetainedRangeState(failureCategory, input.ProviderHistory),
            positionMetadata,
            failureCategory,
            SqlServerPhase(input),
            SqlServerState(input, failureCategory),
            diagnostics.Diagnostics
        );

        CdcSourceHistoryIncidentCandidate? incidentCandidate = createCandidate
            ? new(observedAt, input.Binding.ToCompleteBindingIdentity(), failureCategory, positionMetadata)
            : null;

        return new(observation, incidentCandidate);
    }

    private static CdcSourceHistoryObservation CreateObservation(
        CdcSourceHistoryClassificationInput input,
        DateTimeOffset observedAt,
        CdcSourceHistoryContinuity continuity,
        bool incidentLatched,
        CdcProviderArtifactContinuityState providerArtifactState,
        CdcProviderRetainedRangeState retainedRangeState,
        CdcIncidentPositionMetadata? positionMetadata,
        CdcIncidentFailureCategory? incidentFailureCategory,
        CdcSqlServerSchemaHistoryEnablementPhase? schemaHistoryEnablementPhase,
        CdcSqlServerSchemaHistoryState schemaHistoryState,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            input.OperationId,
            observedAt,
            input.Binding.ToTargetIdentity(),
            input.Binding.Provider,
            input.Binding.PhysicalSourceFingerprint,
            continuity,
            incidentLatched,
            providerArtifactState,
            retainedRangeState,
            positionMetadata,
            incidentFailureCategory,
            schemaHistoryEnablementPhase,
            schemaHistoryState,
            [.. diagnostics]
        )
        {
            SqlServerJobs = SqlServerJobs(input),
        };

    private static CdcSqlServerCdcJobEvidence? SqlServerJobs(CdcSourceHistoryClassificationInput input) =>
        input.Binding.Provider == CdcProvider.SqlServer
            ? input.ProviderHistory?.SqlServerJobs ?? CdcSqlServerCdcJobEvidence.Unknown
            : null;

    private static CdcProviderArtifactContinuityState LostProviderArtifactState(
        CdcIncidentFailureCategory failureCategory,
        CdcProviderSourceHistoryEvidence? providerHistory
    ) =>
        failureCategory switch
        {
            CdcIncidentFailureCategory.ProviderArtifactMissing => CdcProviderArtifactContinuityState.Missing,
            CdcIncidentFailureCategory.ProviderArtifactRecreated =>
                CdcProviderArtifactContinuityState.Recreated,
            _ => providerHistory?.ProviderArtifactState ?? CdcProviderArtifactContinuityState.ExactMatch,
        };

    private static CdcProviderRetainedRangeState LostRetainedRangeState(
        CdcIncidentFailureCategory failureCategory,
        CdcProviderSourceHistoryEvidence? providerHistory
    ) =>
        failureCategory == CdcIncidentFailureCategory.RetainedHistoryGap
            ? CdcProviderRetainedRangeState.Gap
            : providerHistory?.RetainedRangeState ?? CdcProviderRetainedRangeState.CoversCommittedOffset;

    private static CdcSqlServerSchemaHistoryEnablementPhase? SqlServerPhase(
        CdcSourceHistoryClassificationInput input
    ) =>
        input.Binding.Provider == CdcProvider.SqlServer
            ? input.SqlServerSchemaHistory?.EnablementPhase
                ?? CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission
            : null;

    private static CdcSqlServerSchemaHistoryState SqlServerState(
        CdcSourceHistoryClassificationInput input,
        CdcIncidentFailureCategory? failureCategory = null
    )
    {
        if (input.Binding.Provider == CdcProvider.Postgresql)
        {
            return CdcSqlServerSchemaHistoryState.NotApplicable;
        }

        if (input.SqlServerSchemaHistory is not null)
        {
            return input.SqlServerSchemaHistory.State;
        }

        return failureCategory switch
        {
            CdcIncidentFailureCategory.SchemaHistoryMissing => CdcSqlServerSchemaHistoryState.Missing,
            CdcIncidentFailureCategory.SchemaHistoryEmptyWithRetainedOffset =>
                CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset,
            CdcIncidentFailureCategory.SchemaHistoryRequiredRecordLost =>
                CdcSqlServerSchemaHistoryState.RequiredRecordLost,
            _ => CdcSqlServerSchemaHistoryState.Unknown,
        };
    }

    private static bool HasProviderSetupState(
        CdcProviderSetupObservation observation,
        CdcProviderSetupState state
    ) =>
        observation.ArtifactInventoryState == state
        || observation.GrantInventoryState == state
        || observation.SourceInventoryState == state
        || observation.HeartbeatState == state;

    private static void AddDiagnostics(
        CdcDiagnosticCollector collector,
        IReadOnlyList<CdcDiagnostic>? diagnostics
    )
    {
        if (diagnostics is null)
        {
            return;
        }

        foreach (CdcDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic is not null))
        {
            collector.Add(diagnostic);
        }
    }

    private static void AddPrefixedDiagnostics(
        CdcDiagnosticCollector collector,
        IReadOnlyList<CdcDiagnostic>? diagnostics,
        string prefix
    )
    {
        if (diagnostics is null)
        {
            return;
        }

        foreach (CdcDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic is not null))
        {
            collector.Add(
                diagnostic.Category,
                $"{prefix}{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                diagnostic.Message
            );
        }
    }

    private sealed record CdcConnectorOffsetEvaluation(
        CdcSourceHistoryClassificationResult? Result,
        CdcCommittedSourcePosition? Position
    );

    private sealed record CdcCommittedSourcePosition(
        string? LsnProc,
        string? CommitLsn,
        string? ChangeLsn,
        long? EventSerialNo,
        string? CommittedPosition
    );

    private sealed record CdcCommittedSourcePositionResult
    {
        private CdcCommittedSourcePositionResult(
            CdcCommittedSourcePosition? position,
            IReadOnlyList<CdcDiagnostic> diagnostics
        )
        {
            Position = position;
            Diagnostics = diagnostics;
        }

        public CdcCommittedSourcePosition? Position { get; }

        public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

        public bool Succeeded => Position is not null && Diagnostics.Count == 0;

        public static CdcCommittedSourcePositionResult Success(CdcCommittedSourcePosition position) =>
            new(position, []);

        public static CdcCommittedSourcePositionResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics) =>
            new(null, diagnostics);
    }

    private enum CdcRetainedRangeEvaluationState
    {
        Covers,
        Gap,
        Unknown,
    }

    private sealed record CdcRetainedRangeEvaluation(
        CdcRetainedRangeEvaluationState State,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    )
    {
        public static CdcRetainedRangeEvaluation Covers(IReadOnlyList<CdcDiagnostic> diagnostics) =>
            new(CdcRetainedRangeEvaluationState.Covers, diagnostics);

        public static CdcRetainedRangeEvaluation Gap(IReadOnlyList<CdcDiagnostic> diagnostics) =>
            new(CdcRetainedRangeEvaluationState.Gap, diagnostics);

        public static CdcRetainedRangeEvaluation UnknownResult(IReadOnlyList<CdcDiagnostic> diagnostics) =>
            new(CdcRetainedRangeEvaluationState.Unknown, diagnostics);
    }
}
