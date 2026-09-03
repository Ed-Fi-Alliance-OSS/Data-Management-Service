// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed record CdcProviderSetupObservationMapping(
    CoreCdc.CdcProviderSetupObservation ProviderSetup,
    CoreCdc.CdcProviderSourceHistoryEvidence ProviderHistory
);

internal sealed record CdcProviderSetupResultCorrelation(
    CoreCdc.CdcProvider Provider,
    string? ObservedPhysicalSourceFingerprint,
    CoreCdc.CdcProviderSetupOutcome SetupOutcome,
    CoreCdc.CdcProviderSetupState StateWhenUntrusted,
    bool CanTrustResultEvidence,
    IReadOnlyList<CoreCdc.CdcDiagnostic> Diagnostics
);

public static class CdcProviderSetupResultMapper
{
    private sealed record ProviderSetupStateMapping(
        CoreCdc.CdcProviderSetupState State,
        IReadOnlyList<CoreCdc.CdcDiagnostic> StateDiagnostics
    );

    private const string PostgresqlReplicationSlotPath =
        "$.providerSetup.providerHistory.postgresqlReplicationSlot";
    private const string SqlServerRetainedRangeStartPath =
        "$.providerSetup.providerHistory.retainedRangeStart";
    private const string SqlServerRetainedRangeEndPath = "$.providerSetup.providerHistory.retainedRangeEnd";

    private static readonly IReadOnlySet<CdcProviderArtifactKind> SourceHistoryArtifactKinds =
        new HashSet<CdcProviderArtifactKind>
        {
            CdcProviderArtifactKind.ProviderHistory,
            CdcProviderArtifactKind.PostgresqlPublication,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            CdcProviderArtifactKind.SqlServerCaptureInstance,
        };

    /// <summary>
    /// The provider diagnostics of a setup result, translated to the shared diagnostic contract on
    /// their own.
    /// </summary>
    /// <remarks>
    /// For the passes whose result is not validate-only evidence and so has no observation to compose:
    /// the create-or-exact-match pass a provisioning sequence runs first. Its refusal is reported from
    /// the step that refused, but the provider's own account of why - a missing principal, a refused
    /// grant, an exhausted step budget - is only in these diagnostics, and a refusal that dropped them
    /// would leave an operator with the outcome and nothing to act on.
    /// </remarks>
    public static IReadOnlyList<CoreCdc.CdcDiagnostic> MapResultDiagnostics(
        CdcProviderSetupResult result,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        return MapDiagnostics(result.Diagnostics, observedAt.ToUniversalTime());
    }

    public static CdcProviderSetupObservationMapping MapValidateOnlyResult(
        string operationId,
        DateTimeOffset observedAt,
        CoreCdc.CdcBinding binding,
        CdcProviderSetupResult result
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(result);

        DateTimeOffset normalizedObservedAt = observedAt.ToUniversalTime();
        IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics = MapDiagnostics(
            result.Diagnostics,
            normalizedObservedAt
        );
        CdcProviderSetupResultCorrelation correlation = CorrelateValidateOnlyResult(
            binding,
            result,
            normalizedObservedAt
        );
        IReadOnlyList<CoreCdc.CdcDiagnostic> combinedDiagnostics =
        [
            .. diagnostics,
            .. correlation.Diagnostics,
        ];
        CoreCdc.CdcProviderSourceHistoryEvidence providerHistory = ToProviderSourceHistoryEvidence(
            binding,
            result,
            combinedDiagnostics,
            correlation,
            normalizedObservedAt
        );
        bool canMapInventoryStates =
            correlation.CanTrustResultEvidence
            && correlation.SetupOutcome == CoreCdc.CdcProviderSetupOutcome.Satisfied;
        ProviderSetupStateMapping artifactInventoryState = canMapInventoryStates
            ? MapNonSourceHistoryArtifactState(result)
            : KnownState(correlation.StateWhenUntrusted);
        ProviderSetupStateMapping grantInventoryState = canMapInventoryStates
            ? MapGrantInventoryState(result, normalizedObservedAt)
            : KnownState(correlation.StateWhenUntrusted);
        ProviderSetupStateMapping sourceInventoryState = canMapInventoryStates
            ? MapSourceInventoryState(result, normalizedObservedAt)
            : KnownState(correlation.StateWhenUntrusted);
        ProviderSetupStateMapping heartbeatState = canMapInventoryStates
            ? MapHeartbeatState(result, normalizedObservedAt)
            : KnownState(correlation.StateWhenUntrusted);
        IReadOnlyList<ProviderSetupStateMapping> setupStates =
        [
            artifactInventoryState,
            grantInventoryState,
            sourceInventoryState,
            heartbeatState,
        ];
        CoreCdc.CdcProviderSetupOutcome setupOutcome = MapProviderSetupOutcome(
            result,
            correlation,
            setupStates
        );
        IReadOnlyList<CoreCdc.CdcDiagnostic> providerSetupDiagnostics =
            CoreCdc.CdcDiagnostic.NormalizeDiagnostics([
                .. combinedDiagnostics,
                .. setupStates.SelectMany(state => state.StateDiagnostics),
            ]);

        return new(
            new CoreCdc.CdcProviderSetupObservation(
                CoreCdc.CdcJsonContract.CurrentContractVersion,
                operationId,
                normalizedObservedAt,
                binding.ToTargetIdentity(),
                correlation.Provider,
                correlation.ObservedPhysicalSourceFingerprint,
                MapSetupMode(result.Mode),
                setupOutcome,
                artifactInventoryState.State,
                grantInventoryState.State,
                sourceInventoryState.State,
                heartbeatState.State,
                providerSetupDiagnostics
            ),
            providerHistory
        );
    }

    private static CoreCdc.CdcProviderSourceHistoryEvidence ToProviderSourceHistoryEvidence(
        CoreCdc.CdcBinding binding,
        CdcProviderSetupResult result,
        IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics,
        CdcProviderSetupResultCorrelation correlation,
        DateTimeOffset observedAt
    )
    {
        CoreCdc.CdcArtifactNameResult artifactNames = CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(
            binding
        );
        IReadOnlyList<CoreCdc.CdcDiagnostic> combinedDiagnostics =
        [
            .. diagnostics,
            .. artifactNames.Diagnostics,
        ];

        if (artifactNames.Inventory is null)
        {
            return UnknownProviderHistory(null, combinedDiagnostics, binding.Provider);
        }

        if (!correlation.CanTrustResultEvidence)
        {
            return UnknownProviderHistory(
                DefaultProviderArtifactName(binding.Provider, artifactNames.Inventory),
                combinedDiagnostics,
                binding.Provider
            );
        }

        return binding.Provider == CoreCdc.CdcProvider.Postgresql
            ? ToPostgresqlProviderHistory(result, artifactNames.Inventory, combinedDiagnostics, observedAt)
            : ToSqlServerProviderHistory(result, artifactNames.Inventory, combinedDiagnostics, observedAt);
    }

    private static CdcProviderSetupResultCorrelation CorrelateValidateOnlyResult(
        CoreCdc.CdcBinding binding,
        CdcProviderSetupResult result,
        DateTimeOffset observedAt
    )
    {
        List<CoreCdc.CdcDiagnostic> diagnostics = [];
        bool hasInvalidCorrelation = false;
        bool hasUnknownCorrelation = false;
        bool hasFailedOutcome = false;
        bool hasTerminalSourceHistoryEvidence = HasTerminalSourceHistoryEvidence(result);
        CoreCdc.CdcProvider? mappedProvider = MapProvider(result.Provider);
        CoreCdc.CdcProvider provider = mappedProvider ?? binding.Provider;

        if (mappedProvider is null)
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.InvalidEnumValue,
                    "$.providerSetup.provider",
                    observedAt,
                    "CDC provider setup result provider is unsupported."
                )
            );
        }
        else if (mappedProvider != binding.Provider)
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.ProviderMismatch,
                    "$.providerSetup.provider",
                    observedAt,
                    "CDC provider setup result provider did not match the binding provider.",
                    binding.Provider.ToString(),
                    mappedProvider.ToString()
                )
            );
        }

        if (result.Mode != CdcProviderSetupMode.ValidateOnly)
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.InvalidObservation,
                    "$.providerSetup.mode",
                    observedAt,
                    "CDC provider setup result must be validate-only evidence.",
                    CdcProviderSetupMode.ValidateOnly.ToString(),
                    result.Mode.ToString()
                )
            );
        }

        if (result.Outcome == CdcProviderSetupOutcome.Failed)
        {
            hasFailedOutcome = true;
            hasUnknownCorrelation = !hasTerminalSourceHistoryEvidence;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable,
                    "$.providerSetup.outcome",
                    observedAt,
                    "CDC provider setup validate-only result is unavailable.",
                    CdcProviderSetupOutcome.ExactMatch.ToString(),
                    result.Outcome.ToString()
                )
            );
        }
        else if (result.Outcome != CdcProviderSetupOutcome.ExactMatch)
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.InvalidObservation,
                    "$.providerSetup.outcome",
                    observedAt,
                    "CDC provider setup validate-only result must be an exact match.",
                    CdcProviderSetupOutcome.ExactMatch.ToString(),
                    result.Outcome.ToString()
                )
            );
        }

        string? boundPhysicalSourceFingerprint = ValidateFingerprint(
            result.BoundPhysicalSourceFingerprint,
            "$.providerSetup.boundPhysicalSourceFingerprint",
            observedAt,
            diagnostics,
            ref hasUnknownCorrelation
        );
        if (
            boundPhysicalSourceFingerprint is not null
            && !string.Equals(
                boundPhysicalSourceFingerprint,
                binding.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.SourceMismatch,
                    "$.providerSetup.boundPhysicalSourceFingerprint",
                    observedAt,
                    "CDC provider setup result bound physical-source fingerprint did not match the binding.",
                    binding.PhysicalSourceFingerprint,
                    boundPhysicalSourceFingerprint
                )
            );
        }

        string? observedPhysicalSourceFingerprint = ValidateFingerprint(
            result.ObservedSourceFingerprint,
            "$.providerSetup.observedSourceFingerprint",
            observedAt,
            diagnostics,
            ref hasUnknownCorrelation
        );
        if (
            observedPhysicalSourceFingerprint is not null
            && !string.Equals(
                observedPhysicalSourceFingerprint,
                binding.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            hasInvalidCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.SourceMismatch,
                    "$.providerSetup.observedSourceFingerprint",
                    observedAt,
                    "CDC provider setup result observed physical-source fingerprint did not match the binding.",
                    binding.PhysicalSourceFingerprint,
                    observedPhysicalSourceFingerprint
                )
            );
        }

        if (hasInvalidCorrelation)
        {
            return new(
                provider,
                observedPhysicalSourceFingerprint,
                CoreCdc.CdcProviderSetupOutcome.Invalid,
                CoreCdc.CdcProviderSetupState.Mismatched,
                false,
                diagnostics
            );
        }

        if (hasUnknownCorrelation)
        {
            return new(
                provider,
                observedPhysicalSourceFingerprint,
                CoreCdc.CdcProviderSetupOutcome.Unknown,
                CoreCdc.CdcProviderSetupState.Unknown,
                false,
                diagnostics
            );
        }

        return new(
            provider,
            observedPhysicalSourceFingerprint,
            hasFailedOutcome
                ? CoreCdc.CdcProviderSetupOutcome.Unknown
                : CoreCdc.CdcProviderSetupOutcome.Satisfied,
            hasFailedOutcome ? CoreCdc.CdcProviderSetupState.Unknown : CoreCdc.CdcProviderSetupState.Matched,
            true,
            diagnostics
        );
    }

    private static string? ValidateFingerprint(
        CdcSourceFingerprint? fingerprint,
        string path,
        DateTimeOffset observedAt,
        List<CoreCdc.CdcDiagnostic> diagnostics,
        ref bool hasUnknownCorrelation
    )
    {
        if (fingerprint is null)
        {
            hasUnknownCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable,
                    path,
                    observedAt,
                    "CDC provider setup result physical-source fingerprint is unavailable."
                )
            );
            return null;
        }

        if (
            !string.Equals(
                fingerprint.Version,
                CdcSourceFingerprintMetadata.Version,
                StringComparison.Ordinal
            ) || !IsValidPhysicalSourceFingerprint(fingerprint.Value)
        )
        {
            hasUnknownCorrelation = true;
            diagnostics.Add(
                Diagnostic(
                    CoreCdc.CdcDiagnosticCategory.MalformedObservation,
                    path,
                    observedAt,
                    "CDC provider setup result physical-source fingerprint is malformed."
                )
            );
            return null;
        }

        return fingerprint.Value;
    }

    private static bool IsValidPhysicalSourceFingerprint(string? value)
    {
        const string prefix = "sha256:";
        return value is not null
            && value.Length == prefix.Length + 64
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value[prefix.Length..].All(IsLowerHex);
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static CoreCdc.CdcDiagnostic Diagnostic(
        CoreCdc.CdcDiagnosticCategory category,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? expected = null,
        string? observed = null
    ) =>
        new CoreCdc.CdcDiagnostic(
            $"providerSetup{category}",
            category,
            category == CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable
                ? CoreCdc.CdcDiagnosticSeverity.Warning
                : CoreCdc.CdcDiagnosticSeverity.Error,
            CoreCdc.CdcDiagnosticComponent.ProviderSetup,
            observedAt,
            message,
            category == CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable,
            expected: expected,
            observed: observed
        ).WithPath(path);

    private static CoreCdc.CdcProviderSourceHistoryEvidence ToPostgresqlProviderHistory(
        CdcProviderSetupResult result,
        CoreCdc.CdcArtifactInventory inventory,
        IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics,
        DateTimeOffset observedAt
    )
    {
        List<CoreCdc.CdcDiagnostic> historyDiagnostics = [.. diagnostics];
        string slotName = inventory.PostgresqlLogicalSlotName ?? string.Empty;
        string publicationName = inventory.PostgresqlPublicationName ?? string.Empty;
        CdcProviderArtifactObservation? slotArtifact = Artifact(
            result,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            slotName
        );
        CdcProviderArtifactObservation? publicationArtifact = Artifact(
            result,
            CdcProviderArtifactKind.PostgresqlPublication,
            publicationName
        );
        CdcProviderHistoryObservation? slotHistory = History(
            result,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            slotName
        );

        bool slotMatched = IsMatchedProviderArtifactObservation(
            slotArtifact,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            slotName,
            "$.providerSetup.artifactInventory.postgresqlReplicationSlot",
            observedAt,
            historyDiagnostics
        );
        bool publicationMatched = IsMatchedProviderArtifactObservation(
            publicationArtifact,
            CdcProviderArtifactKind.PostgresqlPublication,
            publicationName,
            "$.providerSetup.artifactInventory.postgresqlPublication",
            observedAt,
            historyDiagnostics
        );
        if (slotHistory is null)
        {
            historyDiagnostics.Add(
                RequiredProviderHistoryUnavailableDiagnostic(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    slotName,
                    "$.providerSetup.providerHistory.postgresqlReplicationSlot",
                    observedAt,
                    "CDC provider setup result did not include required PostgreSQL slot history evidence."
                )
            );
        }

        string? retainedStart = PostgresqlWalFromSafe(ObservedValue(slotHistory, "restart_lsn"));
        string? retainedEnd = PostgresqlWalFromSafe(ObservedValue(slotHistory, "confirmed_flush_lsn"));
        bool retainedWalLost =
            string.Equals(
                ObservedValue(slotHistory, "wal_status"),
                "lost",
                StringComparison.OrdinalIgnoreCase
            ) || !string.IsNullOrWhiteSpace(ObservedValue(slotHistory, "invalidation_reason"));
        CoreCdc.CdcProviderArtifactContinuityState artifactState = PostgresqlArtifactState(
            slotArtifact,
            publicationArtifact,
            slotMatched,
            publicationMatched,
            retainedWalLost,
            HasProviderHistoryUnavailable(result)
        );
        CoreCdc.CdcProviderRetainedRangeState retainedRangeState = PostgresqlRetainedRangeState(
            artifactState,
            retainedStart,
            retainedEnd,
            retainedWalLost
        );

        return new(
            artifactState,
            retainedRangeState,
            PostgresqlProviderArtifactName(artifactState, slotArtifact, publicationArtifact, slotName),
            retainedStart,
            retainedEnd,
            UnavailableFacts(artifactState, retainedRangeState, null)
        )
        {
            Diagnostics = CoreCdc.CdcDiagnostic.NormalizeDiagnostics(historyDiagnostics),
        };
    }

    private static CoreCdc.CdcProviderArtifactContinuityState PostgresqlArtifactState(
        CdcProviderArtifactObservation? slotArtifact,
        CdcProviderArtifactObservation? publicationArtifact,
        bool slotMatched,
        bool publicationMatched,
        bool retainedWalLost,
        bool hasProviderHistoryUnavailable
    )
    {
        if (slotArtifact?.State == CdcProviderArtifactState.Missing)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Missing;
        }

        if (publicationArtifact?.State == CdcProviderArtifactState.Missing)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Missing;
        }

        if (slotArtifact?.State == CdcProviderArtifactState.Mismatched)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Recreated;
        }

        if (publicationArtifact?.State == CdcProviderArtifactState.Mismatched)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Recreated;
        }

        if (!slotMatched || !publicationMatched)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Unknown;
        }

        if (retainedWalLost)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.ExactMatch;
        }

        if (hasProviderHistoryUnavailable)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Unknown;
        }

        return CoreCdc.CdcProviderArtifactContinuityState.ExactMatch;
    }

    private static CoreCdc.CdcProviderRetainedRangeState PostgresqlRetainedRangeState(
        CoreCdc.CdcProviderArtifactContinuityState artifactState,
        string? retainedStart,
        string? retainedEnd,
        bool retainedWalLost
    )
    {
        if (artifactState != CoreCdc.CdcProviderArtifactContinuityState.ExactMatch)
        {
            return CoreCdc.CdcProviderRetainedRangeState.Unknown;
        }

        if (retainedWalLost)
        {
            return CoreCdc.CdcProviderRetainedRangeState.Gap;
        }

        CoreCdc.CdcPostgresqlWalPositionResult start = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(
            retainedStart,
            PostgresqlReplicationSlotPath
        );
        CoreCdc.CdcPostgresqlWalPositionResult end = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(
            retainedEnd,
            PostgresqlReplicationSlotPath
        );
        if (start.Position is null || end.Position is null)
        {
            return CoreCdc.CdcProviderRetainedRangeState.Unknown;
        }

        return start.Position.Value.CompareTo(end.Position.Value) <= 0
            ? CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset
            : CoreCdc.CdcProviderRetainedRangeState.Unknown;
    }

    private static string PostgresqlProviderArtifactName(
        CoreCdc.CdcProviderArtifactContinuityState artifactState,
        CdcProviderArtifactObservation? slotArtifact,
        CdcProviderArtifactObservation? publicationArtifact,
        string slotName
    ) =>
        artifactState switch
        {
            CoreCdc.CdcProviderArtifactContinuityState.Missing
                when slotArtifact?.State == CdcProviderArtifactState.Missing => slotArtifact
                .SafeArtifactName
                .Value,
            CoreCdc.CdcProviderArtifactContinuityState.Missing
                when publicationArtifact?.State == CdcProviderArtifactState.Missing => publicationArtifact
                .SafeArtifactName
                .Value,
            CoreCdc.CdcProviderArtifactContinuityState.Recreated
                when slotArtifact?.State == CdcProviderArtifactState.Mismatched => slotArtifact
                .SafeArtifactName
                .Value,
            CoreCdc.CdcProviderArtifactContinuityState.Recreated
                when publicationArtifact?.State == CdcProviderArtifactState.Mismatched => publicationArtifact
                .SafeArtifactName
                .Value,
            _ => slotName,
        };

    private static CoreCdc.CdcProviderSourceHistoryEvidence ToSqlServerProviderHistory(
        CdcProviderSetupResult result,
        CoreCdc.CdcArtifactInventory inventory,
        IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics,
        DateTimeOffset observedAt
    )
    {
        List<CoreCdc.CdcDiagnostic> historyDiagnostics = [.. diagnostics];
        string heartbeatCaptureName = inventory.SqlServerCaptureInstanceCdcHeartbeatName ?? string.Empty;
        CdcProviderHistoryObservation? databaseHistory = History(
            result,
            CdcProviderArtifactKind.ProviderHistory,
            "sqlserver_database_cdc"
        );
        if (databaseHistory is null)
        {
            historyDiagnostics.Add(
                RequiredProviderHistoryUnavailableDiagnostic(
                    CdcProviderArtifactKind.ProviderHistory,
                    "sqlserver_database_cdc",
                    "$.providerSetup.providerHistory.sqlServerDatabaseCdc",
                    observedAt,
                    "CDC provider setup result did not include required SQL Server database CDC history evidence."
                )
            );
        }

        IReadOnlyList<string> requiredCaptureNames = RequiredSqlServerCaptureInstanceNames(inventory);
        IReadOnlyList<CdcProviderArtifactObservation?> captureArtifacts = requiredCaptureNames
            .Select(captureName =>
                Artifact(result, CdcProviderArtifactKind.SqlServerCaptureInstance, captureName)
            )
            .ToArray();
        IReadOnlyList<CdcProviderHistoryObservation?> captureHistory = requiredCaptureNames
            .Select(captureName =>
                History(result, CdcProviderArtifactKind.SqlServerCaptureInstance, captureName)
            )
            .ToArray();
        bool captureArtifactsMatched = RequiredSqlServerCaptureArtifactsAreMatched(
            requiredCaptureNames,
            captureArtifacts,
            observedAt,
            historyDiagnostics
        );

        CoreCdc.CdcSqlServerCdcJobEvidence jobs = SqlServerJobs(databaseHistory);
        CoreCdc.CdcProviderArtifactContinuityState artifactState = SqlServerArtifactState(
            databaseHistory,
            captureArtifacts,
            jobs,
            captureArtifactsMatched
        );
        string providerArtifactName = SqlServerProviderArtifactName(
            artifactState,
            captureArtifacts,
            heartbeatCaptureName
        );
        string? retainedStart = MaxSqlServerLsn(
            captureHistory.Select(history => ObservedValue(history, "retained_min_lsn"))
        );
        string? retainedEnd =
            SqlServerLsnFromSafe(ObservedValue(databaseHistory, "retained_max_lsn"))
            ?? MaxSqlServerLsn(captureHistory.Select(history => ObservedValue(history, "retained_max_lsn")));
        bool hasCompleteRetainedRangeHistory = HasCompleteSqlServerRetainedRangeHistory(
            requiredCaptureNames,
            captureHistory,
            observedAt,
            historyDiagnostics
        );
        CoreCdc.CdcProviderRetainedRangeState retainedRangeState = SqlServerRetainedRangeState(
            artifactState,
            retainedStart,
            retainedEnd,
            hasCompleteRetainedRangeHistory
        );

        return new(
            artifactState,
            retainedRangeState,
            providerArtifactName,
            retainedStart,
            retainedEnd,
            UnavailableFacts(artifactState, retainedRangeState, jobs)
        )
        {
            Diagnostics = CoreCdc.CdcDiagnostic.NormalizeDiagnostics(historyDiagnostics),
            SqlServerJobs = jobs,
        };
    }

    private static CoreCdc.CdcProviderArtifactContinuityState SqlServerArtifactState(
        CdcProviderHistoryObservation? databaseHistory,
        IReadOnlyList<CdcProviderArtifactObservation?> captureArtifacts,
        CoreCdc.CdcSqlServerCdcJobEvidence jobs,
        bool captureArtifactsMatched
    )
    {
        if (
            databaseHistory is not null
            && ObservedValue(databaseHistory, "history") != "unavailable"
            && !BoolValue(databaseHistory, "database_cdc_enabled")
        )
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Missing;
        }

        if (jobs.HasMissingJob)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Missing;
        }

        if (captureArtifacts.Any(artifact => artifact?.State == CdcProviderArtifactState.Missing))
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Missing;
        }

        if (captureArtifacts.Any(artifact => artifact?.State == CdcProviderArtifactState.Mismatched))
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Recreated;
        }

        if (databaseHistory is null || ObservedValue(databaseHistory, "history") == "unavailable")
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Unknown;
        }

        if (!captureArtifactsMatched)
        {
            return CoreCdc.CdcProviderArtifactContinuityState.Unknown;
        }

        return CoreCdc.CdcProviderArtifactContinuityState.ExactMatch;
    }

    private static string SqlServerProviderArtifactName(
        CoreCdc.CdcProviderArtifactContinuityState artifactState,
        IReadOnlyList<CdcProviderArtifactObservation?> captureArtifacts,
        string heartbeatCaptureName
    )
    {
        CdcProviderArtifactObservation? blockingCapture = artifactState switch
        {
            CoreCdc.CdcProviderArtifactContinuityState.Missing => captureArtifacts.FirstOrDefault(artifact =>
                artifact is not null && artifact.State == CdcProviderArtifactState.Missing
            ),
            CoreCdc.CdcProviderArtifactContinuityState.Recreated => captureArtifacts.FirstOrDefault(
                artifact => artifact is not null && artifact.State == CdcProviderArtifactState.Mismatched
            ),
            CoreCdc.CdcProviderArtifactContinuityState.Unknown => captureArtifacts.FirstOrDefault(artifact =>
                artifact is not null && artifact.State == CdcProviderArtifactState.Unavailable
            ),
            _ => null,
        };

        return blockingCapture?.SafeArtifactName.Value ?? heartbeatCaptureName;
    }

    private static CoreCdc.CdcProviderRetainedRangeState SqlServerRetainedRangeState(
        CoreCdc.CdcProviderArtifactContinuityState artifactState,
        string? retainedStart,
        string? retainedEnd,
        bool hasCompleteRetainedRangeHistory
    )
    {
        if (
            artifactState
            is CoreCdc.CdcProviderArtifactContinuityState.Missing
                or CoreCdc.CdcProviderArtifactContinuityState.Unknown
        )
        {
            return CoreCdc.CdcProviderRetainedRangeState.Unknown;
        }

        if (!hasCompleteRetainedRangeHistory)
        {
            return CoreCdc.CdcProviderRetainedRangeState.Unknown;
        }

        CoreCdc.CdcSqlServerLsnResult start = CoreCdc.CdcSqlServerProviderPositionParser.ParseLsn(
            retainedStart,
            SqlServerRetainedRangeStartPath
        );
        CoreCdc.CdcSqlServerLsnResult end = CoreCdc.CdcSqlServerProviderPositionParser.ParseLsn(
            retainedEnd,
            SqlServerRetainedRangeEndPath
        );
        if (start.Lsn is null || end.Lsn is null)
        {
            return CoreCdc.CdcProviderRetainedRangeState.Unknown;
        }

        return start.Lsn.Value.CompareTo(end.Lsn.Value) <= 0
            ? CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset
            : CoreCdc.CdcProviderRetainedRangeState.Unknown;
    }

    private static CoreCdc.CdcSqlServerCdcJobEvidence SqlServerJobs(
        CdcProviderHistoryObservation? databaseHistory
    )
    {
        if (databaseHistory is null)
        {
            return CoreCdc.CdcSqlServerCdcJobEvidence.Unknown;
        }

        if (ObservedValue(databaseHistory, "history") == "unavailable")
        {
            CoreCdc.CdcSqlServerCdcJobEvidence unavailableEvidence = new(
                SqlServerUnavailableJobState(databaseHistory, "capture"),
                SqlServerUnavailableJobState(databaseHistory, "cleanup")
            );
            return unavailableEvidence.HasMissingJob
                ? unavailableEvidence
                : CoreCdc.CdcSqlServerCdcJobEvidence.Unknown;
        }

        return new(
            SqlServerJobState(databaseHistory, "capture", captureJob: true),
            SqlServerJobState(databaseHistory, "cleanup", captureJob: false)
        );
    }

    private static CoreCdc.CdcSqlServerCdcJobState SqlServerUnavailableJobState(
        CdcProviderHistoryObservation databaseHistory,
        string jobType
    ) =>
        IsFalseValue(databaseHistory, $"{jobType}_job_present")
            ? CoreCdc.CdcSqlServerCdcJobState.Missing
            : CoreCdc.CdcSqlServerCdcJobState.Unknown;

    private static CoreCdc.CdcSqlServerCdcJobState SqlServerJobState(
        CdcProviderHistoryObservation? databaseHistory,
        string jobType,
        bool captureJob
    )
    {
        if (IsFalseValue(databaseHistory, $"{jobType}_job_present"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Missing;
        }

        if (!BoolValue(databaseHistory, $"{jobType}_job_present"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Unknown;
        }

        if (IsFalseValue(databaseHistory, $"{jobType}_job_enabled"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Stopped;
        }

        if (!BoolValue(databaseHistory, $"{jobType}_job_enabled"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Unknown;
        }

        if (captureJob && IsFalseValue(databaseHistory, $"{jobType}_job_running"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Stopped;
        }

        if (captureJob && !BoolValue(databaseHistory, $"{jobType}_job_running"))
        {
            return CoreCdc.CdcSqlServerCdcJobState.Unknown;
        }

        return SqlServerLastRunJobState(databaseHistory, jobType);
    }

    private static CoreCdc.CdcSqlServerCdcJobState SqlServerLastRunJobState(
        CdcProviderHistoryObservation? databaseHistory,
        string jobType
    ) =>
        ObservedValue(databaseHistory, $"{jobType}_job_last_run_status") switch
        {
            "1" or "" => CoreCdc.CdcSqlServerCdcJobState.Healthy,
            "0" or "2" or "3" or "4" => CoreCdc.CdcSqlServerCdcJobState.Failed,
            null => CoreCdc.CdcSqlServerCdcJobState.Unknown,
            _ => CoreCdc.CdcSqlServerCdcJobState.Unknown,
        };

    private static IReadOnlyList<CoreCdc.CdcIncidentUnavailableFact> UnavailableFacts(
        CoreCdc.CdcProviderArtifactContinuityState artifactState,
        CoreCdc.CdcProviderRetainedRangeState retainedRangeState,
        CoreCdc.CdcSqlServerCdcJobEvidence? jobs
    )
    {
        HashSet<CoreCdc.CdcIncidentUnavailableFact> facts = [];
        if (artifactState != CoreCdc.CdcProviderArtifactContinuityState.ExactMatch)
        {
            facts.Add(CoreCdc.CdcIncidentUnavailableFact.ProviderArtifact);
        }

        if (retainedRangeState == CoreCdc.CdcProviderRetainedRangeState.Unknown)
        {
            facts.Add(CoreCdc.CdcIncidentUnavailableFact.ProviderRetainedRange);
        }

        if (jobs is not null && !jobs.IsHealthy)
        {
            facts.Add(CoreCdc.CdcIncidentUnavailableFact.ProviderArtifact);
        }

        return facts.Order().ToArray();
    }

    private static ProviderSetupStateMapping MapNonSourceHistoryArtifactState(
        CdcProviderSetupResult result
    ) =>
        KnownState(
            ReduceArtifactState(
                ArtifactInventory(result)
                    .Where(artifact =>
                        !SourceHistoryArtifactKinds.Contains(artifact.ArtifactKind)
                        && artifact.ArtifactKind != CdcProviderArtifactKind.Grant
                        && artifact.ArtifactKind != CdcProviderArtifactKind.HeartbeatTable
                    )
                    .Select(artifact => artifact.State)
            )
        );

    private static ProviderSetupStateMapping MapGrantInventoryState(
        CdcProviderSetupResult result,
        DateTimeOffset observedAt
    )
    {
        if (HasErrorDiagnostic(result, CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure))
        {
            return KnownState(CoreCdc.CdcProviderSetupState.Mismatched);
        }

        return GrantInventory(result).Count == 0
            ? UnknownSetupEvidence(
                "$.providerSetup.grantInventory",
                observedAt,
                "CDC provider setup result did not include required grant inventory evidence."
            )
            : KnownState(CoreCdc.CdcProviderSetupState.Matched);
    }

    private static ProviderSetupStateMapping MapSourceInventoryState(
        CdcProviderSetupResult result,
        DateTimeOffset observedAt
    )
    {
        if (
            HasErrorDiagnostic(result, CdcProviderDiagnosticCategory.MissingRequiredSourceObject)
            || HasErrorDiagnostic(result, CdcProviderDiagnosticCategory.WorkTableCaptureViolation)
        )
        {
            return KnownState(CoreCdc.CdcProviderSetupState.Mismatched);
        }

        return SourceTableInventory(result).Count == 0
            ? UnknownSetupEvidence(
                "$.providerSetup.sourceTableInventory",
                observedAt,
                "CDC provider setup result did not include required source table inventory evidence."
            )
            : KnownState(CoreCdc.CdcProviderSetupState.Matched);
    }

    private static ProviderSetupStateMapping MapHeartbeatState(
        CdcProviderSetupResult result,
        DateTimeOffset observedAt
    )
    {
        List<CoreCdc.CdcDiagnostic> diagnostics = [];
        CdcProviderArtifactState[] heartbeatArtifactStates =
        [
            .. ArtifactInventory(result)
                .Where(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable)
                .Select(artifact => artifact.State),
        ];
        CoreCdc.CdcProviderSetupState heartbeatState = ReduceArtifactState(
            heartbeatArtifactStates,
            defaultWhenEmpty: CoreCdc.CdcProviderSetupState.Unknown
        );

        if (heartbeatArtifactStates.Length == 0)
        {
            diagnostics.Add(
                SetupEvidenceUnavailableDiagnostic(
                    "$.providerSetup.artifactInventory.heartbeatTable",
                    observedAt,
                    "CDC provider setup result did not include required heartbeat table evidence."
                )
            );
        }

        if (result.HeartbeatActionQuery is null)
        {
            diagnostics.Add(
                SetupEvidenceUnavailableDiagnostic(
                    "$.providerSetup.heartbeatActionQuery",
                    observedAt,
                    "CDC provider setup result did not include required heartbeat action-query evidence."
                )
            );
        }

        if (heartbeatState == CoreCdc.CdcProviderSetupState.Matched && diagnostics.Count > 0)
        {
            heartbeatState = CoreCdc.CdcProviderSetupState.Unknown;
        }

        return new(heartbeatState, CoreCdc.CdcDiagnostic.NormalizeDiagnostics(diagnostics));
    }

    private static CoreCdc.CdcProviderSetupOutcome MapProviderSetupOutcome(
        CdcProviderSetupResult result,
        CdcProviderSetupResultCorrelation correlation,
        IReadOnlyList<ProviderSetupStateMapping> setupStates
    )
    {
        if (
            !correlation.CanTrustResultEvidence
            || correlation.SetupOutcome != CoreCdc.CdcProviderSetupOutcome.Satisfied
        )
        {
            return correlation.CanTrustResultEvidence && HasNonSourceHistoryError(result)
                ? CoreCdc.CdcProviderSetupOutcome.Invalid
                : correlation.SetupOutcome;
        }

        CoreCdc.CdcProviderSetupState[] states = setupStates.Select(state => state.State).ToArray();
        if (
            HasNonSourceHistoryError(result)
            || Array.Exists(
                states,
                state =>
                    state is CoreCdc.CdcProviderSetupState.Missing or CoreCdc.CdcProviderSetupState.Mismatched
            )
        )
        {
            return CoreCdc.CdcProviderSetupOutcome.Invalid;
        }

        return Array.Exists(states, state => state == CoreCdc.CdcProviderSetupState.Unknown)
            ? CoreCdc.CdcProviderSetupOutcome.Unknown
            : CoreCdc.CdcProviderSetupOutcome.Satisfied;
    }

    private static ProviderSetupStateMapping KnownState(CoreCdc.CdcProviderSetupState state) =>
        new(state, []);

    private static ProviderSetupStateMapping UnknownSetupEvidence(
        string path,
        DateTimeOffset observedAt,
        string message
    ) =>
        new(
            CoreCdc.CdcProviderSetupState.Unknown,
            [SetupEvidenceUnavailableDiagnostic(path, observedAt, message)]
        );

    private static CoreCdc.CdcProviderSetupState ReduceArtifactState(
        IEnumerable<CdcProviderArtifactState> states,
        CoreCdc.CdcProviderSetupState defaultWhenEmpty = CoreCdc.CdcProviderSetupState.Matched
    )
    {
        CdcProviderArtifactState[] stateArray = states.ToArray();
        if (stateArray.Length == 0)
        {
            return defaultWhenEmpty;
        }

        if (Array.Exists(stateArray, state => state == CdcProviderArtifactState.Missing))
        {
            return CoreCdc.CdcProviderSetupState.Missing;
        }

        if (Array.Exists(stateArray, state => state == CdcProviderArtifactState.Mismatched))
        {
            return CoreCdc.CdcProviderSetupState.Mismatched;
        }

        if (Array.Exists(stateArray, state => state == CdcProviderArtifactState.Unavailable))
        {
            return CoreCdc.CdcProviderSetupState.Unknown;
        }

        return CoreCdc.CdcProviderSetupState.Matched;
    }

    private static bool HasNonSourceHistoryError(CdcProviderSetupResult result) =>
        Diagnostics(result)
            .Any(diagnostic =>
                diagnostic.Severity == CdcProviderDiagnosticSeverity.Error
                && !IsSourceHistoryDiagnostic(diagnostic)
            );

    private static bool HasProviderHistoryUnavailable(CdcProviderSetupResult result) =>
        Diagnostics(result)
            .Any(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
            );

    private static bool HasTerminalSourceHistoryEvidence(CdcProviderSetupResult result) =>
        ArtifactInventory(result)
            .Any(artifact =>
                SourceHistoryArtifactKinds.Contains(artifact.ArtifactKind)
                && artifact.State is CdcProviderArtifactState.Missing or CdcProviderArtifactState.Mismatched
            )
        || Diagnostics(result)
            .Any(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
            )
        || ProviderHistoryObservations(result).Any(HasTerminalProviderHistoryObservation);

    private static bool HasTerminalProviderHistoryObservation(CdcProviderHistoryObservation history) =>
        history.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
            && (
                IsFalseValue(history, "database_cdc_enabled")
                || IsFalseValue(history, "capture_job_present")
                || IsFalseValue(history, "cleanup_job_present")
            )
        || history.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
            && (
                string.Equals(
                    ObservedValue(history, "wal_status"),
                    "lost",
                    StringComparison.OrdinalIgnoreCase
                ) || !string.IsNullOrWhiteSpace(ObservedValue(history, "invalidation_reason"))
            );

    private static bool HasErrorDiagnostic(
        CdcProviderSetupResult result,
        CdcProviderDiagnosticCategory category
    ) =>
        Diagnostics(result)
            .Any(diagnostic =>
                diagnostic.Severity == CdcProviderDiagnosticSeverity.Error && diagnostic.Category == category
            );

    private static bool IsSourceHistoryDiagnostic(CdcProviderDiagnostic diagnostic) =>
        diagnostic.Category
            is CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                or CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
        || SourceHistoryArtifactKinds.Contains(diagnostic.ArtifactKind);

    private static IReadOnlyList<CoreCdc.CdcDiagnostic> MapDiagnostics(
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics,
        DateTimeOffset observedAt
    ) =>
        Diagnostics(diagnostics)
            .Select(diagnostic => new CoreCdc.CdcDiagnostic(
                diagnostic.Code,
                MapDiagnosticCategory(diagnostic),
                MapDiagnosticSeverity(diagnostic.Severity),
                IsSourceHistoryDiagnostic(diagnostic)
                    ? CoreCdc.CdcDiagnosticComponent.SourceHistory
                    : CoreCdc.CdcDiagnosticComponent.ProviderSetup,
                observedAt,
                $"CDC provider setup diagnostic `{diagnostic.Code}`.",
                diagnostic.Classification
                    is CdcProviderRetryContinuityClassification.Retryable
                        or CdcProviderRetryContinuityClassification.SourceHistoryUnknown,
                diagnostic.ArtifactKind.ToString(),
                diagnostic.SafeName.Value,
                diagnostic.ExpectedValue,
                diagnostic.ObservedValue
            ))
            .ToArray();

    private static CoreCdc.CdcDiagnosticCategory MapDiagnosticCategory(CdcProviderDiagnostic diagnostic) =>
        diagnostic.Category switch
        {
            CdcProviderDiagnosticCategory.ProviderHistoryUnavailable => CoreCdc
                .CdcDiagnosticCategory
                .ProviderHistoryUnknown,
            CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence => CoreCdc
                .CdcDiagnosticCategory
                .SourceHistoryLost,
            _ => CoreCdc.CdcDiagnosticCategory.ProviderSetupInvalid,
        };

    private static CoreCdc.CdcDiagnosticSeverity MapDiagnosticSeverity(
        CdcProviderDiagnosticSeverity severity
    ) =>
        severity switch
        {
            CdcProviderDiagnosticSeverity.Info => CoreCdc.CdcDiagnosticSeverity.Info,
            CdcProviderDiagnosticSeverity.Warning => CoreCdc.CdcDiagnosticSeverity.Warning,
            _ => CoreCdc.CdcDiagnosticSeverity.Error,
        };

    private static CoreCdc.CdcProviderSetupMode MapSetupMode(CdcProviderSetupMode mode) =>
        mode switch
        {
            CdcProviderSetupMode.InitialCreateOrExactMatch => CoreCdc
                .CdcProviderSetupMode
                .InitialCreateOrExactMatch,
            CdcProviderSetupMode.ValidateOnly => CoreCdc.CdcProviderSetupMode.ValidateOnly,
            _ => CoreCdc.CdcProviderSetupMode.ValidateOnly,
        };

    private static CoreCdc.CdcProvider? MapProvider(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => CoreCdc.CdcProvider.Postgresql,
            CdcProvider.SqlServer => CoreCdc.CdcProvider.SqlServer,
            _ => null,
        };

    private static CdcProviderArtifactObservation? Artifact(
        CdcProviderSetupResult result,
        CdcProviderArtifactKind artifactKind,
        string safeName
    ) =>
        Artifacts(result, artifactKind)
            .FirstOrDefault(artifact =>
                string.Equals(artifact.SafeArtifactName.Value, safeName, StringComparison.Ordinal)
            );

    private static IEnumerable<CdcProviderArtifactObservation> Artifacts(
        CdcProviderSetupResult result,
        CdcProviderArtifactKind artifactKind
    ) => ArtifactInventory(result).Where(artifact => artifact.ArtifactKind == artifactKind);

    private static CdcProviderHistoryObservation? History(
        CdcProviderSetupResult result,
        CdcProviderArtifactKind artifactKind,
        string safeName
    ) =>
        Histories(result, artifactKind)
            .FirstOrDefault(history =>
                string.Equals(history.SafeArtifactName.Value, safeName, StringComparison.Ordinal)
            );

    private static bool IsMatchedProviderArtifactObservation(
        CdcProviderArtifactObservation? artifact,
        CdcProviderArtifactKind artifactKind,
        string safeName,
        string path,
        DateTimeOffset observedAt,
        List<CoreCdc.CdcDiagnostic> diagnostics
    )
    {
        if (artifact is null)
        {
            diagnostics.Add(
                RequiredProviderHistoryUnavailableDiagnostic(
                    artifactKind,
                    safeName,
                    path,
                    observedAt,
                    "CDC provider setup result did not include required provider artifact evidence."
                )
            );
            return false;
        }

        if (artifact.State == CdcProviderArtifactState.Matched)
        {
            return true;
        }

        if (artifact.State is CdcProviderArtifactState.Missing or CdcProviderArtifactState.Mismatched)
        {
            return false;
        }

        diagnostics.Add(
            RequiredProviderHistoryUnavailableDiagnostic(
                artifactKind,
                safeName,
                path,
                observedAt,
                "CDC provider setup result did not include matched required provider artifact evidence.",
                observed: artifact.State.ToString()
            )
        );
        return false;
    }

    private static IReadOnlyList<string> RequiredSqlServerCaptureInstanceNames(
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        [
            inventory.SqlServerCaptureInstanceDocumentCacheName ?? string.Empty,
            inventory.SqlServerCaptureInstanceDocumentName ?? string.Empty,
            inventory.SqlServerCaptureInstanceCdcHeartbeatName ?? string.Empty,
        ];

    private static bool RequiredSqlServerCaptureArtifactsAreMatched(
        IReadOnlyList<string> requiredCaptureNames,
        IReadOnlyList<CdcProviderArtifactObservation?> captureArtifacts,
        DateTimeOffset observedAt,
        List<CoreCdc.CdcDiagnostic> diagnostics
    )
    {
        bool allMatched = true;
        for (int index = 0; index < requiredCaptureNames.Count; index++)
        {
            allMatched =
                IsMatchedProviderArtifactObservation(
                    captureArtifacts[index],
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    requiredCaptureNames[index],
                    "$.providerSetup.artifactInventory.sqlServerCaptureInstance",
                    observedAt,
                    diagnostics
                ) && allMatched;
        }

        return allMatched;
    }

    private static bool HasCompleteSqlServerRetainedRangeHistory(
        IReadOnlyList<string> requiredCaptureNames,
        IReadOnlyList<CdcProviderHistoryObservation?> captureHistory,
        DateTimeOffset observedAt,
        List<CoreCdc.CdcDiagnostic> diagnostics
    )
    {
        bool hasCompleteHistory = true;
        for (int index = 0; index < requiredCaptureNames.Count; index++)
        {
            CdcProviderHistoryObservation? history = captureHistory[index];
            if (history is null)
            {
                diagnostics.Add(
                    RequiredProviderHistoryUnavailableDiagnostic(
                        CdcProviderArtifactKind.SqlServerCaptureInstance,
                        requiredCaptureNames[index],
                        "$.providerSetup.providerHistory.sqlServerCaptureInstance",
                        observedAt,
                        "CDC provider setup result did not include required SQL Server retained-range history evidence."
                    )
                );
                hasCompleteHistory = false;
                continue;
            }

            if (
                SqlServerLsnFromSafe(ObservedValue(history, "retained_min_lsn")) is null
                || SqlServerLsnFromSafe(ObservedValue(history, "retained_max_lsn")) is null
            )
            {
                diagnostics.Add(
                    RequiredProviderHistoryUnavailableDiagnostic(
                        CdcProviderArtifactKind.SqlServerCaptureInstance,
                        requiredCaptureNames[index],
                        "$.providerSetup.providerHistory.sqlServerRetainedRange",
                        observedAt,
                        "CDC provider setup result did not include a valid SQL Server retained-range history window."
                    )
                );
                hasCompleteHistory = false;
            }
        }

        return hasCompleteHistory;
    }

    private static CoreCdc.CdcDiagnostic SetupEvidenceUnavailableDiagnostic(
        string path,
        DateTimeOffset observedAt,
        string message
    ) =>
        new CoreCdc.CdcDiagnostic(
            "providerSetupEvidenceUnavailable",
            CoreCdc.CdcDiagnosticCategory.StatusObservationUnavailable,
            CoreCdc.CdcDiagnosticSeverity.Warning,
            CoreCdc.CdcDiagnosticComponent.ProviderSetup,
            observedAt,
            message,
            true,
            observed: "absent"
        ).WithPath(path);

    private static CoreCdc.CdcDiagnostic RequiredProviderHistoryUnavailableDiagnostic(
        CdcProviderArtifactKind artifactKind,
        string safeName,
        string path,
        DateTimeOffset observedAt,
        string message,
        string? observed = "absent"
    ) =>
        new CoreCdc.CdcDiagnostic(
            "providerHistoryEvidenceUnavailable",
            CoreCdc.CdcDiagnosticCategory.ProviderHistoryUnknown,
            CoreCdc.CdcDiagnosticSeverity.Warning,
            CoreCdc.CdcDiagnosticComponent.SourceHistory,
            observedAt,
            message,
            true,
            artifactKind.ToString(),
            safeName,
            CdcProviderArtifactState.Matched.ToString(),
            observed
        ).WithPath(path);

    private static IEnumerable<CdcProviderHistoryObservation> Histories(
        CdcProviderSetupResult result,
        CdcProviderArtifactKind artifactKind
    ) => ProviderHistoryObservations(result).Where(history => history.ArtifactKind == artifactKind);

    private static string? ObservedValue(CdcProviderHistoryObservation? observation, string key)
    {
        if (observation?.SafeObservedValues is null)
        {
            return null;
        }

        return observation.SafeObservedValues.TryGetValue(key, out string? value) ? value : null;
    }

    private static bool BoolValue(CdcProviderHistoryObservation? observation, string key) =>
        ObservedValue(observation, key) is "True" or "true" or "1";

    private static bool IsFalseValue(CdcProviderHistoryObservation? observation, string key) =>
        ObservedValue(observation, key) is "False" or "false" or "0";

    private static string? PostgresqlWalFromSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "none")
        {
            return null;
        }

        int separatorIndex = value.IndexOf('_', StringComparison.Ordinal);
        return separatorIndex <= 0 || value.IndexOf('_', separatorIndex + 1) >= 0
            ? value
            : $"{value[..separatorIndex]}/{value[(separatorIndex + 1)..]}";
    }

    private static string? SqlServerLsnFromSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "none")
        {
            return null;
        }

        CoreCdc.CdcSqlServerLsnResult result = CoreCdc.CdcSqlServerProviderPositionParser.ParseLsn(
            value,
            "$.providerSetup.providerHistory.sqlServerLsn"
        );
        return result.Lsn?.ToString();
    }

    private static string? MaxSqlServerLsn(IEnumerable<string?> values)
    {
        CoreCdc.CdcSqlServerLsn? max = null;
        foreach (string? value in values)
        {
            CoreCdc.CdcSqlServerLsnResult parsed = CoreCdc.CdcSqlServerProviderPositionParser.ParseLsn(
                SqlServerLsnFromSafe(value),
                "$.providerSetup.providerHistory.sqlServerLsn"
            );
            if (parsed.Lsn is null)
            {
                continue;
            }

            if (max is null || parsed.Lsn.Value.CompareTo(max.Value) > 0)
            {
                max = parsed.Lsn.Value;
            }
        }

        return max?.ToString();
    }

    private static string? DefaultProviderArtifactName(
        CoreCdc.CdcProvider provider,
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        provider == CoreCdc.CdcProvider.Postgresql
            ? inventory.PostgresqlLogicalSlotName
            : inventory.SqlServerCaptureInstanceCdcHeartbeatName;

    private static CoreCdc.CdcProviderSourceHistoryEvidence UnknownProviderHistory(
        string? providerArtifactName,
        IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics,
        CoreCdc.CdcProvider provider
    ) =>
        new(
            CoreCdc.CdcProviderArtifactContinuityState.Unknown,
            CoreCdc.CdcProviderRetainedRangeState.Unknown,
            providerArtifactName,
            null,
            null,
            [CoreCdc.CdcIncidentUnavailableFact.ProviderRetainedRange]
        )
        {
            Diagnostics = diagnostics,
            SqlServerJobs =
                provider == CoreCdc.CdcProvider.SqlServer ? CoreCdc.CdcSqlServerCdcJobEvidence.Unknown : null,
        };

    private static IReadOnlyList<CdcProviderArtifactObservation> ArtifactInventory(
        CdcProviderSetupResult result
    ) => result.ArtifactInventory?.Where(artifact => artifact is not null).ToArray() ?? [];

    private static IReadOnlyList<CdcGrantObservation> GrantInventory(CdcProviderSetupResult result) =>
        result.GrantInventory?.Where(grant => grant is not null).ToArray() ?? [];

    private static IReadOnlyList<CdcSourceTableInventory> SourceTableInventory(
        CdcProviderSetupResult result
    ) => result.SourceTableInventory?.Where(sourceTable => sourceTable is not null).ToArray() ?? [];

    private static IReadOnlyList<CdcProviderHistoryObservation> ProviderHistoryObservations(
        CdcProviderSetupResult result
    ) => result.ProviderHistoryObservations?.Where(observation => observation is not null).ToArray() ?? [];

    private static IReadOnlyList<CdcProviderDiagnostic> Diagnostics(CdcProviderSetupResult result) =>
        Diagnostics(result.Diagnostics);

    private static IReadOnlyList<CdcProviderDiagnostic> Diagnostics(
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics
    ) => diagnostics?.Where(diagnostic => diagnostic is not null).ToArray() ?? [];
}
