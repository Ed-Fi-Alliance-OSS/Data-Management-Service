// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcDatabaseCreationMode>))]
public enum CdcDatabaseCreationMode
{
    CreatedForInitialCdcProvisioning,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcWriteAdmissionState>))]
public enum CdcWriteAdmissionState
{
    ClosedNeverOpened,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcConsistencyScope>))]
public enum CdcConsistencyScope
{
    SingleProviderTransaction,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcCacheAheadState>))]
public enum CdcCacheAheadState
{
    Clear,
    RecoveryRequired,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProjectionCorrelationState>))]
public enum CdcProjectionCorrelationState
{
    Matched,
    TargetMismatch,
    ProviderMismatch,
    SourceMismatch,
    Unavailable,
    InvalidPayload,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupMode>))]
public enum CdcProviderSetupMode
{
    InitialCreateOrExactMatch,
    ValidateOnly,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupOutcome>))]
public enum CdcProviderSetupOutcome
{
    Satisfied,
    Invalid,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderSetupState>))]
public enum CdcProviderSetupState
{
    Matched,
    Mismatched,
    Missing,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderBarrierState>))]
public enum CdcProviderBarrierState
{
    Reached,
    NotReached,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderArtifactContinuityState>))]
public enum CdcProviderArtifactContinuityState
{
    ExactMatch,
    Missing,
    Recreated,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProviderRetainedRangeState>))]
public enum CdcProviderRetainedRangeState
{
    CoversCommittedOffset,
    Gap,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSqlServerSchemaHistoryEnablementPhase>))]
public enum CdcSqlServerSchemaHistoryEnablementPhase
{
    BeforeInitialAdmission,
    AfterInitialAdmission,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcSqlServerSchemaHistoryState>))]
public enum CdcSqlServerSchemaHistoryState
{
    Valid,
    Missing,
    EmptyWithRetainedOffset,
    RequiredRecordLost,
    Unreadable,
    Unknown,
    NotApplicable,
}

public interface ICdcObservationContract : ICdcJsonContract
{
    string OperationId { get; }

    DateTimeOffset ObservedAt { get; }

    CdcTargetIdentity TargetIdentity { get; }

    CdcProvider Provider { get; }

    string? PhysicalSourceFingerprint { get; }

    IReadOnlyList<CdcDiagnostic> Diagnostics { get; }
}

public sealed record CdcObservationValidationContext(
    string OperationId,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint,
    DateTimeOffset NowUtc
);

public sealed record InitialCdcProvisioningProof(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string ProofId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string SetupControllerRunId,
    [property: JsonRequired] CdcDatabaseCreationMode DatabaseCreationMode,
    [property: JsonRequired] CdcWriteAdmissionState WriteAdmissionState,
    [property: JsonRequired] DateTimeOffset IssuedAt
) : ICdcJsonContract;

public sealed record InitialCdcEligibilityObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] DateTimeOffset DurableObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string SetupControllerRunId,
    [property: JsonRequired] string WriteAdmissionProofId,
    [property: JsonRequired] CdcConsistencyScope ConsistencyScope,
    [property: JsonRequired] CdcLifecycleState LifecycleState,
    [property: JsonRequired] CdcCacheAheadState CacheAheadState,
    [property: JsonRequired] bool CanonicalRowsPresent,
    [property: JsonRequired] bool CacheRowsPresent,
    [property: JsonRequired] bool WorkRowsPresent,
    [property: JsonRequired] string ProviderConsistencyToken,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcProjectionCorrelationObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] DateTimeOffset ProjectionObservedAt,
    [property: JsonRequired] DocumentCacheStatusTargetKey E18TargetKey,
    [property: JsonRequired] CdcProjectionCorrelationState CorrelationState,
    [property: JsonRequired] DocumentCacheOperationalHealthStatus OperationalHealthStatus,
    [property: JsonRequired] DocumentCacheStatusReason OperationalHealthReason,
    [property: JsonRequired] DocumentCacheCaughtUpStatus CaughtUpStatus,
    [property: JsonRequired] DocumentCacheStatusReason CaughtUpReason,
    [property: JsonRequired] DocumentCacheStatusQueuePresence QueuePresence,
    [property: JsonRequired]
        IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory> EnqueueFailureCategories,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcProviderSetupObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcProviderSetupMode SetupMode,
    [property: JsonRequired] CdcProviderSetupOutcome SetupOutcome,
    [property: JsonRequired] CdcProviderSetupState ArtifactInventoryState,
    [property: JsonRequired] CdcProviderSetupState GrantInventoryState,
    [property: JsonRequired] CdcProviderSetupState SourceInventoryState,
    [property: JsonRequired] CdcProviderSetupState HeartbeatState,
    [property: JsonRequired] CdcProviderSetupState ProviderHistoryState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcProviderBarrierObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] DateTimeOffset ProjectionCaughtUpObservedAt,
    [property: JsonRequired] DateTimeOffset BarrierCapturedAt,
    [property: JsonRequired] DateTimeOffset ConnectorOffsetObservedAt,
    [property: JsonRequired] CdcProviderBarrierState BarrierState,
    [property: JsonRequired] string? PostgresqlBarrierLsn,
    [property: JsonRequired] string? SqlServerCommitLsn,
    [property: JsonRequired] string? SqlServerChangeLsn,
    [property: JsonRequired] long? SqlServerEventSerialNo,
    [property: JsonRequired] string? CommittedPosition,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcConnectorOffsetObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] string ConnectorName,
    [property: JsonRequired] string TopicPrefix,
    [property: JsonRequired] CdcConnectorOffsetMatchResult SourcePartitionMatchResult,
    [property: JsonRequired] string ConnectSourcePartitionHash,
    [property: JsonRequired] bool IsSnapshot,
    [property: JsonRequired] bool IsNull,
    [property: JsonRequired] long? LsnProc,
    [property: JsonRequired] string? CommitLsn,
    [property: JsonRequired] string? ChangeLsn,
    [property: JsonRequired] long? EventSerialNo,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public sealed record CdcSourceHistoryObservation(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcTargetIdentity TargetIdentity,
    [property: JsonRequired] CdcProvider Provider,
    [property: JsonRequired] string? PhysicalSourceFingerprint,
    [property: JsonRequired] CdcSourceHistoryContinuity Continuity,
    [property: JsonRequired] bool IncidentLatched,
    [property: JsonRequired] CdcProviderArtifactContinuityState ProviderArtifactState,
    [property: JsonRequired] CdcProviderRetainedRangeState RetainedRangeState,
    [property: JsonRequired] CdcIncidentPositionMetadata? PositionEvidence,
    [property: JsonRequired] CdcIncidentFailureCategory? IncidentFailureCategory,
    [property: JsonRequired] CdcSqlServerSchemaHistoryEnablementPhase? SchemaHistoryEnablementPhase,
    [property: JsonRequired] CdcSqlServerSchemaHistoryState SchemaHistoryState,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcObservationContract;

public static class InitialCdcProvisioningProofValidator
{
    public static CdcContractValidationResult Validate(
        InitialCdcProvisioningProof proof,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateContractVersion(
            proof.ContractVersion,
            "$.contractVersion",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            proof.ProofId,
            "$.proofId",
            "proofId",
            diagnostics
        );
        CdcObservationValidationRules.ValidateOperationId(
            proof.OperationId,
            context.OperationId,
            "$.operationId",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTargetIdentity(
            proof.TargetIdentity,
            context.TargetIdentity,
            "$.targetIdentity",
            diagnostics
        );
        CdcObservationValidationRules.ValidateProvider(
            proof.Provider,
            context.TargetIdentity.Provider,
            "$.provider",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            proof.SetupControllerRunId,
            "$.setupControllerRunId",
            "setupControllerRunId",
            diagnostics
        );
        ValidateDatabaseCreationMode(proof.DatabaseCreationMode, diagnostics);
        ValidateWriteAdmissionState(proof.WriteAdmissionState, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            proof.IssuedAt,
            context.NowUtc,
            "$.issuedAt",
            diagnostics
        );

        return diagnostics.ToValidationResult();
    }

    private static void ValidateDatabaseCreationMode(
        CdcDatabaseCreationMode mode,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(mode) || mode != CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.databaseCreationMode",
                "CDC provisioning proof databaseCreationMode must be createdForInitialCdcProvisioning."
            );
        }
    }

    private static void ValidateWriteAdmissionState(
        CdcWriteAdmissionState state,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(state) || state != CdcWriteAdmissionState.ClosedNeverOpened)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.writeAdmissionState",
                "CDC provisioning proof writeAdmissionState must be closedNeverOpened."
            );
        }
    }
}

public static class InitialCdcEligibilityObservationValidator
{
    public static CdcContractValidationResult Validate(
        InitialCdcEligibilityObservation observation,
        InitialCdcProvisioningProof proof,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            observation.DurableObservedAt,
            context.NowUtc,
            "$.durableObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateObservedNotBeforeDurable(
            observation.DurableObservedAt,
            observation.ObservedAt,
            "$.durableObservedAt",
            "$.observedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.SetupControllerRunId,
            "$.setupControllerRunId",
            "setupControllerRunId",
            diagnostics
        );
        if (
            !string.Equals(
                observation.SetupControllerRunId,
                proof.SetupControllerRunId,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                "$.setupControllerRunId",
                "CDC eligibility setupControllerRunId must match the provisioning proof."
            );
        }

        CdcObservationValidationRules.ValidateRequiredToken(
            observation.WriteAdmissionProofId,
            "$.writeAdmissionProofId",
            "writeAdmissionProofId",
            diagnostics
        );
        if (!string.Equals(observation.WriteAdmissionProofId, proof.ProofId, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                "$.writeAdmissionProofId",
                "CDC eligibility writeAdmissionProofId must match the provisioning proof."
            );
        }

        ValidateConsistencyScope(observation.ConsistencyScope, diagnostics);
        ValidateLifecycleState(observation.LifecycleState, diagnostics);
        ValidateCacheAheadState(observation.CacheAheadState, diagnostics);
        ValidateInitialRowsAbsent(observation, diagnostics);
        ValidateProviderConsistencyToken(observation.ProviderConsistencyToken, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateConsistencyScope(
        CdcConsistencyScope consistencyScope,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            !Enum.IsDefined(consistencyScope)
            || consistencyScope != CdcConsistencyScope.SingleProviderTransaction
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.consistencyScope",
                "CDC eligibility consistencyScope must be singleProviderTransaction."
            );
        }
    }

    private static void ValidateLifecycleState(
        CdcLifecycleState lifecycleState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(lifecycleState) || lifecycleState == CdcLifecycleState.Unknown)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.lifecycleState",
                "CDC eligibility lifecycleState must be authoritative."
            );
        }
    }

    private static void ValidateCacheAheadState(
        CdcCacheAheadState cacheAheadState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(cacheAheadState) || cacheAheadState == CdcCacheAheadState.Unknown)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.cacheAheadState",
                "CDC eligibility cacheAheadState must be authoritative."
            );
        }
    }

    private static void ValidateInitialRowsAbsent(
        InitialCdcEligibilityObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.CanonicalRowsPresent)
        {
            AddRowsPresentDiagnostic("$.canonicalRowsPresent", "canonical");
        }

        if (observation.CacheRowsPresent)
        {
            AddRowsPresentDiagnostic("$.cacheRowsPresent", "cache");
        }

        if (observation.WorkRowsPresent)
        {
            AddRowsPresentDiagnostic("$.workRowsPresent", "work");
        }

        void AddRowsPresentDiagnostic(string path, string rowSet)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC eligibility requires {rowSet} rows to be absent before binding creation."
            );
        }
    }

    private static void ValidateProviderConsistencyToken(
        string? providerConsistencyToken,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            string.IsNullOrWhiteSpace(providerConsistencyToken)
            || providerConsistencyToken == CdcContractText.EvidenceUnavailable
            || !string.Equals(
                providerConsistencyToken,
                CdcContractText.SanitizeRequired(providerConsistencyToken),
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                "$.providerConsistencyToken",
                "CDC eligibility providerConsistencyToken must be bounded sanitized evidence."
            );
        }
    }
}

public static class CdcProjectionCorrelationObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProjectionCorrelationObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ProjectionObservedAt,
            context.NowUtc,
            "$.projectionObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateObservedNotBeforeDurable(
            observation.ProjectionObservedAt,
            observation.ObservedAt,
            "$.projectionObservedAt",
            "$.observedAt",
            diagnostics
        );
        ValidateE18TargetKey(observation.E18TargetKey, context.TargetIdentity, diagnostics);
        ValidateCorrelationState(observation.CorrelationState, diagnostics);
        ValidateE18Enums(observation, diagnostics);
        ValidateEnqueueFailureCategories(observation.EnqueueFailureCategories, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateE18TargetKey(
        DocumentCacheStatusTargetKey? e18TargetKey,
        CdcTargetIdentity expectedTargetIdentity,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (e18TargetKey is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.e18TargetKey",
                "Missing required field `e18TargetKey`."
            );
            return;
        }

        string bindingTenantKey = CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(
            e18TargetKey.TenantKey
        );
        string dataStoreId = e18TargetKey.DataStoreId.ToString(CultureInfo.InvariantCulture);
        if (
            !string.Equals(bindingTenantKey, expectedTargetIdentity.TenantKey, StringComparison.Ordinal)
            || !string.Equals(dataStoreId, expectedTargetIdentity.DataStoreId, StringComparison.Ordinal)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                "$.e18TargetKey",
                "CDC projection observation E18 target key must match the CDC binding target."
            );
        }
    }

    private static void ValidateCorrelationState(
        CdcProjectionCorrelationState correlationState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(correlationState))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.correlationState",
                "CDC projection correlationState is unsupported."
            );
            return;
        }

        CdcDiagnosticCategory? category = correlationState switch
        {
            CdcProjectionCorrelationState.TargetMismatch => CdcDiagnosticCategory.TargetMismatch,
            CdcProjectionCorrelationState.ProviderMismatch => CdcDiagnosticCategory.ProviderMismatch,
            CdcProjectionCorrelationState.SourceMismatch => CdcDiagnosticCategory.SourceMismatch,
            CdcProjectionCorrelationState.InvalidPayload => CdcDiagnosticCategory.MalformedPayload,
            _ => null,
        };

        if (category is not null)
        {
            diagnostics.Add(
                category.Value,
                "$.correlationState",
                "CDC projection observation correlationState must be matched or unavailable for a structurally valid observation."
            );
        }
    }

    private static void ValidateE18Enums(
        CdcProjectionCorrelationObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateEnum(
            observation.OperationalHealthStatus,
            "$.operationalHealthStatus",
            "operationalHealthStatus"
        );
        ValidateEnum(
            observation.OperationalHealthReason,
            "$.operationalHealthReason",
            "operationalHealthReason"
        );
        ValidateEnum(observation.CaughtUpStatus, "$.caughtUpStatus", "caughtUpStatus");
        ValidateEnum(observation.CaughtUpReason, "$.caughtUpReason", "caughtUpReason");
        ValidateEnum(observation.QueuePresence, "$.queuePresence", "queuePresence");

        void ValidateEnum<TEnum>(TEnum value, string path, string fieldName)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    path,
                    $"CDC projection observation {fieldName} is unsupported."
                );
            }
        }
    }

    private static void ValidateEnqueueFailureCategories(
        IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory>? categories,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (categories is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.enqueueFailureCategories",
                "Missing required field `enqueueFailureCategories`."
            );
            return;
        }

        for (int index = 0; index < categories.Count; index++)
        {
            if (!Enum.IsDefined(categories[index]))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    $"$.enqueueFailureCategories[{index}]",
                    "CDC projection observation enqueueFailureCategories contains an unsupported category."
                );
            }
        }
    }
}

public static class CdcProviderSetupObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProviderSetupObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateSetupMode(observation.SetupMode, diagnostics);
        ValidateSetupOutcome(observation.SetupOutcome, diagnostics);
        ValidateSetupState(observation.ArtifactInventoryState, "$.artifactInventoryState", diagnostics);
        ValidateSetupState(observation.GrantInventoryState, "$.grantInventoryState", diagnostics);
        ValidateSetupState(observation.SourceInventoryState, "$.sourceInventoryState", diagnostics);
        ValidateSetupState(observation.HeartbeatState, "$.heartbeatState", diagnostics);
        ValidateSetupState(observation.ProviderHistoryState, "$.providerHistoryState", diagnostics);
        ValidateOutcomeStateConsistency(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateSetupMode(CdcProviderSetupMode setupMode, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(setupMode))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.setupMode",
                "CDC provider setup observation setupMode is unsupported."
            );
        }
    }

    private static void ValidateSetupOutcome(
        CdcProviderSetupOutcome setupOutcome,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(setupOutcome))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                "$.setupOutcome",
                "CDC provider setup observation setupOutcome is unsupported."
            );
        }
    }

    private static void ValidateSetupState(
        CdcProviderSetupState setupState,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(setupState))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                path,
                "CDC provider setup observation state is unsupported."
            );
        }
    }

    private static void ValidateOutcomeStateConsistency(
        CdcProviderSetupObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.SetupOutcome))
        {
            return;
        }

        CdcProviderSetupState[] states =
        [
            observation.ArtifactInventoryState,
            observation.GrantInventoryState,
            observation.SourceInventoryState,
            observation.HeartbeatState,
            observation.ProviderHistoryState,
        ];

        bool allStatesDefined = Array.TrueForAll(states, Enum.IsDefined);
        if (!allStatesDefined)
        {
            return;
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Satisfied
            && Array.Exists(
                states,
                state => state is not (CdcProviderSetupState.Matched or CdcProviderSetupState.NotApplicable)
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.setupOutcome",
                "CDC provider setup satisfied outcome requires matched or notApplicable inventory states."
            );
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Unknown
            && Array.TrueForAll(states, state => state != CdcProviderSetupState.Unknown)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.setupOutcome",
                "CDC provider setup unknown outcome requires at least one unknown state."
            );
        }
    }
}

public static class CdcProviderBarrierObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcProviderBarrierObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateOrderingEvidence(observation, context.NowUtc, diagnostics);
        ValidateBarrierState(observation.BarrierState, diagnostics);
        ValidateProviderBarrierFields(observation, diagnostics);
        ValidateCommittedPosition(observation, diagnostics);
        ValidateStateConsistency(observation, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateOrderingEvidence(
        CdcProviderBarrierObservation observation,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ProjectionCaughtUpObservedAt,
            nowUtc,
            "$.projectionCaughtUpObservedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            observation.BarrierCapturedAt,
            nowUtc,
            "$.barrierCapturedAt",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            observation.ConnectorOffsetObservedAt,
            nowUtc,
            "$.connectorOffsetObservedAt",
            diagnostics
        );

        ValidateNotAfter(
            observation.ProjectionCaughtUpObservedAt,
            observation.BarrierCapturedAt,
            "$.projectionCaughtUpObservedAt",
            "$.barrierCapturedAt",
            diagnostics
        );
        ValidateNotAfter(
            observation.BarrierCapturedAt,
            observation.ConnectorOffsetObservedAt,
            "$.barrierCapturedAt",
            "$.connectorOffsetObservedAt",
            diagnostics
        );
        ValidateNotAfter(
            observation.ConnectorOffsetObservedAt,
            observation.ObservedAt,
            "$.connectorOffsetObservedAt",
            "$.observedAt",
            diagnostics
        );
    }

    private static void ValidateNotAfter(
        DateTimeOffset earlier,
        DateTimeOffset later,
        string earlierPath,
        string laterPath,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (earlier > later)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                earlierPath,
                $"CDC provider barrier observation requires {earlierPath} to be no later than {laterPath}."
            );
        }
    }

    private static void ValidateBarrierState(
        CdcProviderBarrierState barrierState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(barrierState))
        {
            diagnostics.InvalidEnumValue(
                "$.barrierState",
                "CDC provider barrier observation barrierState is unsupported."
            );
        }
    }

    private static void ValidateProviderBarrierFields(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool barrierRequired =
            observation.BarrierState is CdcProviderBarrierState.Reached or CdcProviderBarrierState.NotReached;

        switch (observation.Provider)
        {
            case CdcProvider.Postgresql:
                ValidatePostgresqlBarrierFields(observation, barrierRequired, diagnostics);
                break;
            case CdcProvider.SqlServer:
                ValidateSqlServerBarrierFields(observation, barrierRequired, diagnostics);
                break;
            default:
                break;
        }
    }

    private static void ValidatePostgresqlBarrierFields(
        CdcProviderBarrierObservation observation,
        bool barrierRequired,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(
            observation.SqlServerCommitLsn,
            "$.sqlServerCommitLsn",
            "sqlServerCommitLsn",
            diagnostics
        );
        ValidateProviderInapplicable(
            observation.SqlServerChangeLsn,
            "$.sqlServerChangeLsn",
            "sqlServerChangeLsn",
            diagnostics
        );
        ValidateProviderInapplicable(
            observation.SqlServerEventSerialNo,
            "$.sqlServerEventSerialNo",
            "sqlServerEventSerialNo",
            diagnostics
        );

        if (barrierRequired || observation.PostgresqlBarrierLsn is not null)
        {
            Add(
                CdcPostgresqlProviderPosition.ParseWalLsn(
                    observation.PostgresqlBarrierLsn,
                    "$.postgresqlBarrierLsn"
                )
            );
        }

        void Add(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateSqlServerBarrierFields(
        CdcProviderBarrierObservation observation,
        bool barrierRequired,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(
            observation.PostgresqlBarrierLsn,
            "$.postgresqlBarrierLsn",
            "postgresqlBarrierLsn",
            diagnostics
        );

        if (barrierRequired || observation.SqlServerCommitLsn is not null)
        {
            Add(
                CdcSqlServerProviderPositionParser.ParseLsn(
                    observation.SqlServerCommitLsn,
                    "$.sqlServerCommitLsn"
                )
            );
        }

        if (barrierRequired || observation.SqlServerChangeLsn is not null)
        {
            Add(
                CdcSqlServerProviderPositionParser.ParseLsn(
                    observation.SqlServerChangeLsn,
                    "$.sqlServerChangeLsn"
                )
            );
        }

        if (barrierRequired && observation.SqlServerEventSerialNo is null)
        {
            diagnostics.MissingRequiredField("$.sqlServerEventSerialNo", "sqlServerEventSerialNo");
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateCommittedPosition(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool required = observation.BarrierState == CdcProviderBarrierState.Reached;
        CdcObservationValidationRules.ValidateSanitizedEvidenceText(
            observation.CommittedPosition,
            "$.committedPosition",
            "committedPosition",
            required,
            diagnostics
        );
    }

    private static void ValidateStateConsistency(
        CdcProviderBarrierObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            observation.BarrierState == CdcProviderBarrierState.NotReached
            && observation.CommittedPosition is not null
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.committedPosition",
                "CDC provider barrier notReached observation must not report a committed position as reached."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC provider barrier observation {fieldName} is not applicable for this provider."
            );
        }
    }
}

public static class CdcConnectorOffsetObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcConnectorOffsetObservation observation,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash = null
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, expectedConnectSourcePartitionHash, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcConnectorOffsetObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash = null
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, expectedConnectSourcePartitionHash, diagnostics);
        ValidateBindingDerivedNames(observation, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcConnectorOffsetObservation observation,
        CdcObservationValidationContext context,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        CdcObservationValidationRules.ValidateArtifactName(
            observation.ConnectorName,
            "$.connectorName",
            "connectorName",
            true,
            diagnostics
        );
        CdcObservationValidationRules.ValidateRequiredToken(
            observation.TopicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSourcePartitionMatch(
            observation.SourcePartitionMatchResult,
            diagnostics
        );
        CdcConnectorOffsetValidationRules.ValidateSnapshotFlag(observation.IsSnapshot, diagnostics);
        CdcConnectorOffsetValidationRules.ValidateNullFlag(observation.IsNull, diagnostics);
        ValidateConnectSourcePartitionHash(observation, expectedConnectSourcePartitionHash, diagnostics);
        ValidateProviderOffsetFields(observation, diagnostics);
    }

    private static void ValidateConnectSourcePartitionHash(
        CdcConnectorOffsetObservation observation,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateSha256Fingerprint(
            observation.ConnectSourcePartitionHash,
            "$.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            true,
            diagnostics
        );

        if (
            expectedConnectSourcePartitionHash is not null
            && !string.Equals(
                observation.ConnectSourcePartitionHash,
                expectedConnectSourcePartitionHash,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                "$.connectSourcePartitionHash",
                "CDC connector offset source partition hash must match the expected Connect source partition."
            );
        }
    }

    private static void ValidateProviderOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (observation.Provider)
        {
            case CdcProvider.Postgresql:
                ValidatePostgresqlOffsetFields(observation, diagnostics);
                break;
            case CdcProvider.SqlServer:
                ValidateSqlServerOffsetFields(observation, diagnostics);
                break;
            default:
                break;
        }
    }

    private static void ValidatePostgresqlOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(observation.CommitLsn, "$.commitLsn", "commitLsn", diagnostics);
        ValidateProviderInapplicable(observation.ChangeLsn, "$.changeLsn", "changeLsn", diagnostics);
        ValidateProviderInapplicable(
            observation.EventSerialNo,
            "$.eventSerialNo",
            "eventSerialNo",
            diagnostics
        );

        if (observation.LsnProc is null)
        {
            diagnostics.MissingRequiredField("$.lsnProc", "lsnProc");
        }
    }

    private static void ValidateSqlServerOffsetFields(
        CdcConnectorOffsetObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateProviderInapplicable(observation.LsnProc, "$.lsnProc", "lsnProc", diagnostics);

        Add(CdcSqlServerProviderPositionParser.ParseLsn(observation.CommitLsn, "$.commitLsn"));
        Add(CdcSqlServerProviderPositionParser.ParseLsn(observation.ChangeLsn, "$.changeLsn"));

        if (observation.EventSerialNo is null)
        {
            diagnostics.MissingRequiredField("$.eventSerialNo", "eventSerialNo");
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateBindingDerivedNames(
        CdcConnectorOffsetObservation observation,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC connector offset binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        if (
            !string.Equals(
                observation.ConnectorName,
                artifactNameResult.Inventory.ConnectorName,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.connectorName",
                "CDC connector offset connectorName must match the binding-derived inventory."
            );
        }

        if (
            !string.Equals(
                observation.TopicPrefix,
                artifactNameResult.Inventory.TopicPrefix,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                "$.topicPrefix",
                "CDC connector offset topicPrefix must match the binding-derived inventory."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC connector offset observation {fieldName} is not applicable for this provider."
            );
        }
    }
}

public static class CdcSourceHistoryObservationValidator
{
    public static CdcContractValidationResult Validate(
        CdcSourceHistoryObservation observation,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, null, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult ValidateForBinding(
        CdcSourceHistoryObservation observation,
        CdcBinding binding,
        CdcObservationValidationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(observation, context, binding, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcSourceHistoryObservation observation,
        CdcObservationValidationContext context,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateEnvelope(observation, context, diagnostics);
        ValidateContinuity(observation.Continuity, diagnostics);
        ValidateProviderArtifactState(observation.ProviderArtifactState, diagnostics);
        ValidateRetainedRangeState(observation.RetainedRangeState, diagnostics);
        ValidateSchemaHistory(observation, diagnostics);
        ValidateIncidentCandidate(observation, binding, context.NowUtc, diagnostics);
        ValidateContinuityStateConsistency(observation, diagnostics);
        ValidatePositionEvidence(observation, binding, diagnostics);
    }

    private static void ValidateContinuity(
        CdcSourceHistoryContinuity continuity,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(continuity))
        {
            diagnostics.InvalidEnumValue(
                "$.continuity",
                "CDC source-history observation continuity is unsupported."
            );
        }
    }

    private static void ValidateProviderArtifactState(
        CdcProviderArtifactContinuityState providerArtifactState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(providerArtifactState))
        {
            diagnostics.InvalidEnumValue(
                "$.providerArtifactState",
                "CDC source-history observation providerArtifactState is unsupported."
            );
        }
    }

    private static void ValidateRetainedRangeState(
        CdcProviderRetainedRangeState retainedRangeState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(retainedRangeState))
        {
            diagnostics.InvalidEnumValue(
                "$.retainedRangeState",
                "CDC source-history observation retainedRangeState is unsupported."
            );
        }
    }

    private static void ValidateSchemaHistory(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(observation.SchemaHistoryState))
        {
            diagnostics.InvalidEnumValue(
                "$.schemaHistoryState",
                "CDC source-history observation schemaHistoryState is unsupported."
            );
            return;
        }

        if (observation.Provider == CdcProvider.Postgresql)
        {
            if (observation.SchemaHistoryEnablementPhase is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryEnablementPhase",
                    "CDC source-history schemaHistoryEnablementPhase is SQL Server-only evidence."
                );
            }

            if (observation.SchemaHistoryState != CdcSqlServerSchemaHistoryState.NotApplicable)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.schemaHistoryState",
                    "CDC source-history schemaHistoryState must be notApplicable for PostgreSQL."
                );
            }

            return;
        }

        if (observation.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        if (observation.SchemaHistoryEnablementPhase is null)
        {
            diagnostics.MissingRequiredField(
                "$.schemaHistoryEnablementPhase",
                "schemaHistoryEnablementPhase"
            );
        }
        else if (!Enum.IsDefined(observation.SchemaHistoryEnablementPhase.Value))
        {
            diagnostics.InvalidEnumValue(
                "$.schemaHistoryEnablementPhase",
                "CDC source-history observation schemaHistoryEnablementPhase is unsupported."
            );
        }

        if (observation.SchemaHistoryState == CdcSqlServerSchemaHistoryState.NotApplicable)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history schemaHistoryState is required for SQL Server."
            );
        }
    }

    private static void ValidateIncidentCandidate(
        CdcSourceHistoryObservation observation,
        CdcBinding? binding,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Continuity != CdcSourceHistoryContinuity.Lost)
        {
            if (observation.IncidentFailureCategory is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.incidentFailureCategory",
                    "CDC source-history incidentFailureCategory is only valid for lost continuity."
                );
            }

            return;
        }

        if (observation.IncidentFailureCategory is null)
        {
            diagnostics.MissingRequiredField("$.incidentFailureCategory", "incidentFailureCategory");
            return;
        }

        if (!Enum.IsDefined(observation.IncidentFailureCategory.Value))
        {
            diagnostics.InvalidEnumValue(
                "$.incidentFailureCategory",
                "CDC source-history observation incidentFailureCategory is unsupported."
            );
            return;
        }

        if (binding is not null && observation.PositionEvidence is not null)
        {
            CdcIncident incident = new(
                CdcJsonContract.CurrentContractVersion,
                CdcIncidentType.SourceHistoryContinuityLost,
                observation.ObservedAt,
                binding.ToCompleteBindingIdentity(),
                observation.IncidentFailureCategory.Value,
                observation.PositionEvidence
            );
            CdcContractValidationResult incidentResult = CdcIncidentValidator.ValidateForBinding(
                incident,
                binding,
                nowUtc
            );

            foreach (CdcDiagnostic diagnostic in incidentResult.Diagnostics)
            {
                diagnostics.Add(
                    diagnostic.Category,
                    $"$.positionEvidence{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                    "CDC source-history incident candidate position evidence is invalid."
                );
            }
        }
    }

    private static void ValidateContinuityStateConsistency(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Continuity == CdcSourceHistoryContinuity.Healthy)
        {
            ValidateHealthyState(observation, diagnostics);
        }

        if (
            observation.Continuity != CdcSourceHistoryContinuity.Lost
            && observation.ProviderArtifactState
                is CdcProviderArtifactContinuityState.Missing
                    or CdcProviderArtifactContinuityState.Recreated
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.providerArtifactState",
                "CDC source-history missing or recreated provider artifacts must be reported as lost continuity."
            );
        }

        if (
            observation.Continuity != CdcSourceHistoryContinuity.Lost
            && observation.RetainedRangeState == CdcProviderRetainedRangeState.Gap
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.retainedRangeState",
                "CDC source-history retained range gap must be reported as lost continuity."
            );
        }

        ValidateSqlServerSchemaHistoryConsistency(observation, diagnostics);
    }

    private static void ValidateHealthyState(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.IncidentLatched)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.incidentLatched",
                "CDC source-history cannot be healthy after a valid incident latch."
            );
        }

        if (observation.ProviderArtifactState != CdcProviderArtifactContinuityState.ExactMatch)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.providerArtifactState",
                "CDC source-history healthy continuity requires exact-match provider artifacts."
            );
        }

        if (observation.RetainedRangeState != CdcProviderRetainedRangeState.CoversCommittedOffset)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.retainedRangeState",
                "CDC source-history healthy continuity requires retained history to cover the committed offset."
            );
        }

        if (
            observation.Provider == CdcProvider.SqlServer
            && observation.SchemaHistoryState != CdcSqlServerSchemaHistoryState.Valid
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history healthy SQL Server continuity requires valid schema history."
            );
        }
    }

    private static void ValidateSqlServerSchemaHistoryConsistency(
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        bool terminalSchemaHistoryLoss =
            observation.SchemaHistoryState
            is CdcSqlServerSchemaHistoryState.Missing
                or CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset
                or CdcSqlServerSchemaHistoryState.RequiredRecordLost;

        if (
            terminalSchemaHistoryLoss
            && observation.SchemaHistoryEnablementPhase
                == CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission
            && observation.Continuity == CdcSourceHistoryContinuity.Lost
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryEnablementPhase",
                "CDC source-history SQL Server schema-history loss cannot terminally latch before initial admission."
            );
        }

        if (
            terminalSchemaHistoryLoss
            && observation.SchemaHistoryEnablementPhase
                == CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission
            && observation.Continuity != CdcSourceHistoryContinuity.Lost
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.schemaHistoryState",
                "CDC source-history terminal SQL Server schema-history loss after admission must report lost continuity."
            );
        }
    }

    private static void ValidatePositionEvidence(
        CdcSourceHistoryObservation observation,
        CdcBinding? binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool required =
            observation.Continuity is CdcSourceHistoryContinuity.Healthy or CdcSourceHistoryContinuity.Lost;
        if (observation.PositionEvidence is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField("$.positionEvidence", "positionEvidence");
            }

            return;
        }

        ValidatePositionEvidenceShape(observation.PositionEvidence, observation.Provider, diagnostics);
        ValidateRetainedRangeEvidence(
            observation.PositionEvidence,
            observation.Provider,
            observation,
            diagnostics
        );

        if (binding is not null)
        {
            ValidatePositionEvidenceArtifacts(observation.PositionEvidence, binding, diagnostics);
        }
    }

    private static void ValidatePositionEvidenceShape(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ConnectorName,
            "$.positionEvidence.connectorName",
            "connectorName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.TopicName,
            "$.positionEvidence.topicName",
            "topicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ProgressTopicName,
            "$.positionEvidence.progressTopicName",
            "progressTopicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.SchemaHistoryTopicName,
            "$.positionEvidence.schemaHistoryTopicName",
            "schemaHistoryTopicName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateArtifactName(
            positionEvidence.ProviderArtifactName,
            "$.positionEvidence.providerArtifactName",
            "providerArtifactName",
            false,
            diagnostics
        );
        CdcObservationValidationRules.ValidateSha256Fingerprint(
            positionEvidence.ConnectSourcePartitionHash,
            "$.positionEvidence.connectSourcePartitionHash",
            "connectSourcePartitionHash",
            false,
            diagnostics
        );
        ValidateProviderPositionFields(positionEvidence, provider, diagnostics);
        ValidateUnavailableFacts(positionEvidence.UnavailableFacts, diagnostics);
    }

    private static void ValidateProviderPositionFields(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (provider)
        {
            case CdcProvider.Postgresql:
                CdcObservationValidationRules.ValidateProviderPositionText(
                    positionEvidence.LsnProc,
                    "$.positionEvidence.lsnProc",
                    "lsnProc",
                    true,
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.CommitLsn,
                    "$.positionEvidence.commitLsn",
                    "commitLsn",
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.ChangeLsn,
                    "$.positionEvidence.changeLsn",
                    "changeLsn",
                    diagnostics
                );
                ValidateProviderInapplicable(
                    positionEvidence.EventSerialNo,
                    "$.positionEvidence.eventSerialNo",
                    "eventSerialNo",
                    diagnostics
                );
                break;
            case CdcProvider.SqlServer:
                ValidateProviderInapplicable(
                    positionEvidence.LsnProc,
                    "$.positionEvidence.lsnProc",
                    "lsnProc",
                    diagnostics
                );
                Add(
                    CdcSqlServerProviderPositionParser.ParseLsn(
                        positionEvidence.CommitLsn,
                        "$.positionEvidence.commitLsn"
                    )
                );
                Add(
                    CdcSqlServerProviderPositionParser.ParseLsn(
                        positionEvidence.ChangeLsn,
                        "$.positionEvidence.changeLsn"
                    )
                );
                if (positionEvidence.EventSerialNo is null)
                {
                    diagnostics.MissingRequiredField("$.positionEvidence.eventSerialNo", "eventSerialNo");
                }

                break;
        }

        void Add(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateRetainedRangeEvidence(
        CdcIncidentPositionMetadata positionEvidence,
        CdcProvider provider,
        CdcSourceHistoryObservation observation,
        CdcDiagnosticCollector diagnostics
    )
    {
        bool retainedRangeRequired =
            observation.RetainedRangeState
            is CdcProviderRetainedRangeState.CoversCommittedOffset
                or CdcProviderRetainedRangeState.Gap;
        if (!retainedRangeRequired)
        {
            ValidateOptionalRetainedRangePosition(
                positionEvidence.RetainedRangeStart,
                "$.positionEvidence.retainedRangeStart",
                provider,
                diagnostics
            );
            ValidateOptionalRetainedRangePosition(
                positionEvidence.RetainedRangeEnd,
                "$.positionEvidence.retainedRangeEnd",
                provider,
                diagnostics
            );
            return;
        }

        if (positionEvidence.RetainedRangeStart is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.retainedRangeStart", "retainedRangeStart");
        }

        if (positionEvidence.RetainedRangeEnd is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.retainedRangeEnd", "retainedRangeEnd");
        }

        if (positionEvidence.RetainedRangeStart is null || positionEvidence.RetainedRangeEnd is null)
        {
            return;
        }

        ValidateRetainedRangeOrdering(
            positionEvidence.RetainedRangeStart,
            positionEvidence.RetainedRangeEnd,
            provider,
            diagnostics
        );
    }

    private static void ValidateOptionalRetainedRangePosition(
        string? value,
        string path,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            return;
        }

        ValidateRetainedRangePosition(value, path, provider, diagnostics);
    }

    private static void ValidateRetainedRangeOrdering(
        string retainedRangeStart,
        string retainedRangeEnd,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (provider)
        {
            case CdcProvider.Postgresql:
                CdcPostgresqlWalPositionResult start = CdcPostgresqlProviderPosition.ParseWalLsn(
                    retainedRangeStart,
                    "$.positionEvidence.retainedRangeStart"
                );
                CdcPostgresqlWalPositionResult end = CdcPostgresqlProviderPosition.ParseWalLsn(
                    retainedRangeEnd,
                    "$.positionEvidence.retainedRangeEnd"
                );
                AddPostgresql(start);
                AddPostgresql(end);
                if (
                    start.Position is not null
                    && end.Position is not null
                    && start.Position.Value.CompareTo(end.Position.Value) > 0
                )
                {
                    diagnostics.Add(
                        CdcDiagnosticCategory.InvalidOrdering,
                        "$.positionEvidence.retainedRangeStart",
                        "CDC source-history retainedRangeStart must not be after retainedRangeEnd."
                    );
                }

                break;
            case CdcProvider.SqlServer:
                CdcSqlServerLsnResult sqlStart = CdcSqlServerProviderPositionParser.ParseLsn(
                    retainedRangeStart,
                    "$.positionEvidence.retainedRangeStart"
                );
                CdcSqlServerLsnResult sqlEnd = CdcSqlServerProviderPositionParser.ParseLsn(
                    retainedRangeEnd,
                    "$.positionEvidence.retainedRangeEnd"
                );
                AddSqlServer(sqlStart);
                AddSqlServer(sqlEnd);
                if (
                    sqlStart.Lsn is not null
                    && sqlEnd.Lsn is not null
                    && sqlStart.Lsn.Value.CompareTo(sqlEnd.Lsn.Value) > 0
                )
                {
                    diagnostics.Add(
                        CdcDiagnosticCategory.InvalidOrdering,
                        "$.positionEvidence.retainedRangeStart",
                        "CDC source-history retainedRangeStart must not be after retainedRangeEnd."
                    );
                }

                break;
        }

        void AddPostgresql(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }

        void AddSqlServer(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidateRetainedRangePosition(
        string value,
        string path,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        switch (provider)
        {
            case CdcProvider.Postgresql:
                AddPostgresql(CdcPostgresqlProviderPosition.ParseWalLsn(value, path));
                break;
            case CdcProvider.SqlServer:
                AddSqlServer(CdcSqlServerProviderPositionParser.ParseLsn(value, path));
                break;
        }

        void AddPostgresql(CdcPostgresqlWalPositionResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }

        void AddSqlServer(CdcSqlServerLsnResult result)
        {
            foreach (CdcDiagnostic diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static void ValidatePositionEvidenceArtifacts(
        CdcIncidentPositionMetadata positionEvidence,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC source-history binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is null)
        {
            return;
        }

        ValidateOptionalExactMatch(
            positionEvidence.ConnectorName,
            artifactNameResult.Inventory.ConnectorName,
            "$.positionEvidence.connectorName",
            "connectorName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionEvidence.TopicName,
            artifactNameResult.Inventory.TopicName,
            "$.positionEvidence.topicName",
            "topicName",
            diagnostics
        );
        ValidateOptionalExactMatch(
            positionEvidence.ProgressTopicName,
            artifactNameResult.Inventory.ProgressTopicName,
            "$.positionEvidence.progressTopicName",
            "progressTopicName",
            diagnostics
        );

        if (artifactNameResult.Inventory.SchemaHistoryTopicName is null)
        {
            if (positionEvidence.SchemaHistoryTopicName is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    "$.positionEvidence.schemaHistoryTopicName",
                    "CDC source-history schemaHistoryTopicName is not applicable for this binding provider."
                );
            }
        }
        else
        {
            ValidateOptionalExactMatch(
                positionEvidence.SchemaHistoryTopicName,
                artifactNameResult.Inventory.SchemaHistoryTopicName,
                "$.positionEvidence.schemaHistoryTopicName",
                "schemaHistoryTopicName",
                diagnostics
            );
        }

        if (positionEvidence.ProviderArtifactName is not null)
        {
            HashSet<string> providerArtifactNames = artifactNameResult
                .Inventory.GovernedArtifacts.Where(artifact => IsProviderArtifact(artifact.Kind))
                .Select(artifact => artifact.Name)
                .ToHashSet(StringComparer.Ordinal);

            if (!providerArtifactNames.Contains(positionEvidence.ProviderArtifactName))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    "$.positionEvidence.providerArtifactName",
                    "CDC source-history providerArtifactName must match a binding-derived provider artifact."
                );
            }
        }
    }

    private static bool IsProviderArtifact(CdcGovernedArtifactKind kind) =>
        kind
            is CdcGovernedArtifactKind.PostgresqlPublication
                or CdcGovernedArtifactKind.PostgresqlLogicalSlot
                or CdcGovernedArtifactKind.SqlServerCdcGatingRole
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat;

    private static void ValidateOptionalExactMatch(
        string? value,
        string expected,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null && !string.Equals(value, expected, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                path,
                $"CDC source-history {fieldName} must match the binding-derived artifact."
            );
        }
    }

    private static void ValidateProviderInapplicable(
        object? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC source-history observation {fieldName} is not applicable for this provider."
            );
        }
    }

    private static void ValidateUnavailableFacts(
        IReadOnlyList<CdcIncidentUnavailableFact>? unavailableFacts,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (unavailableFacts is null)
        {
            diagnostics.MissingRequiredField("$.positionEvidence.unavailableFacts", "unavailableFacts");
            return;
        }

        HashSet<CdcIncidentUnavailableFact> seenFacts = [];
        for (int index = 0; index < unavailableFacts.Count; index++)
        {
            CdcIncidentUnavailableFact fact = unavailableFacts[index];
            if (!Enum.IsDefined(fact))
            {
                diagnostics.InvalidEnumValue(
                    $"$.positionEvidence.unavailableFacts[{index}]",
                    "CDC source-history unavailableFacts contains an unsupported fact."
                );
            }
            else if (!seenFacts.Add(fact))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    $"$.positionEvidence.unavailableFacts[{index}]",
                    "CDC source-history unavailableFacts must not contain duplicate facts."
                );
            }
        }
    }
}

internal static class CdcObservationValidationRules
{
    private const int MaximumEvidenceTextLength = 512;

    public static void ValidateEnvelope(
        ICdcObservationContract observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateContractVersion(observation.ContractVersion, "$.contractVersion", diagnostics);
        ValidateOperationId(observation.OperationId, context.OperationId, "$.operationId", diagnostics);
        ValidateTimestamp(observation.ObservedAt, context.NowUtc, "$.observedAt", diagnostics);
        ValidateTargetIdentity(
            observation.TargetIdentity,
            context.TargetIdentity,
            "$.targetIdentity",
            diagnostics
        );
        ValidateProvider(observation.Provider, context.TargetIdentity.Provider, "$.provider", diagnostics);
        ValidatePhysicalSourceFingerprint(
            observation.PhysicalSourceFingerprint,
            context.PhysicalSourceFingerprint,
            "$.physicalSourceFingerprint",
            diagnostics
        );
        ValidateDiagnostics(observation.Diagnostics, diagnostics);
    }

    public static void ValidateContractVersion(
        int contractVersion,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (contractVersion != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidContractVersion,
                path,
                $"CDC observation contract version `{contractVersion}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }
    }

    public static void ValidateOperationId(
        string? operationId,
        string expectedOperationId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateRequiredToken(operationId, path, "operationId", diagnostics);
        if (!string.Equals(operationId, expectedOperationId, StringComparison.Ordinal))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.OperationMismatch,
                path,
                "CDC observation operationId must match the current operation."
            );
        }
    }

    public static void ValidateRequiredToken(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            value is null
            || value.Length == 0
            || value.Length > 128
            || !CdcKafkaSafeTokenValidator.IsValid(value)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC observation {fieldName} must be a non-empty safe token."
            );
        }
    }

    public static void ValidateTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset nowUtc,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        DateTimeOffset normalizedNowUtc = nowUtc.ToUniversalTime();
        if (timestamp.Offset != TimeSpan.Zero || timestamp > normalizedNowUtc)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidTimestamp,
                path,
                "CDC observation timestamp must be UTC and must not be in the future."
            );
        }
    }

    public static void ValidateObservedNotBeforeDurable(
        DateTimeOffset durableObservedAt,
        DateTimeOffset observedAt,
        string durablePath,
        string observedPath,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (durableObservedAt > observedAt)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                durablePath,
                $"CDC observation durable evidence timestamp must not be later than {observedPath}."
            );
        }
    }

    public static void ValidateTargetIdentity(
        CdcTargetIdentity? targetIdentity,
        CdcTargetIdentity expectedTargetIdentity,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (targetIdentity is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                path,
                "Missing required field `targetIdentity`."
            );
            return;
        }

        CdcContractValidationResult identityResult = CdcTargetValidator.ValidateBindingIdentity(
            CdcBindingIdentity.FromTargetIdentity(targetIdentity)
        );
        foreach (CdcDiagnostic diagnostic in identityResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                $"{path}{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                "CDC observation targetIdentity is invalid."
            );
        }

        ValidateProvider(
            targetIdentity.Provider,
            expectedTargetIdentity.Provider,
            $"{path}.provider",
            diagnostics
        );

        if (targetIdentity != expectedTargetIdentity)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.TargetMismatch,
                path,
                "CDC observation targetIdentity must match the current target."
            );
        }
    }

    public static void ValidateProvider(
        CdcProvider provider,
        CdcProvider expectedProvider,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(provider))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidEnumValue,
                path,
                "CDC observation provider is unsupported."
            );
            return;
        }

        if (provider != expectedProvider)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC observation provider must match the current target provider."
            );
        }
    }

    private static void ValidatePhysicalSourceFingerprint(
        string? physicalSourceFingerprint,
        string? expectedPhysicalSourceFingerprint,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (expectedPhysicalSourceFingerprint is null)
        {
            if (physicalSourceFingerprint is not null)
            {
                ValidateSha256Fingerprint(
                    physicalSourceFingerprint,
                    path,
                    "physicalSourceFingerprint",
                    true,
                    diagnostics
                );
            }

            return;
        }

        ValidateSha256Fingerprint(
            physicalSourceFingerprint,
            path,
            "physicalSourceFingerprint",
            true,
            diagnostics
        );
        if (
            !string.Equals(
                physicalSourceFingerprint,
                expectedPhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                path,
                "CDC observation physicalSourceFingerprint must match the current source."
            );
        }
    }

    public static void ValidateArtifactName(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (
            value.Length == 0
            || value.Length > CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength
            || !CdcKafkaSafeTokenValidator.IsValid(value)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                path,
                $"CDC observation {fieldName} must be a non-empty safe artifact name."
            );
        }
    }

    public static void ValidateSha256Fingerprint(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        const string sha256Prefix = "sha256:";
        if (
            value.Length != sha256Prefix.Length + 64
            || !value.StartsWith(sha256Prefix, StringComparison.Ordinal)
            || !value[sha256Prefix.Length..].All(IsLowercaseHex)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedPayload,
                path,
                $"CDC observation {fieldName} must be `sha256:` plus 64 lowercase hex characters."
            );
        }
    }

    public static void ValidateProviderPositionText(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (value.Length == 0 || value.Length > 256 || !IsProviderPosition(value))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedPayload,
                path,
                $"CDC observation {fieldName} must be a provider-normalized position."
            );
        }
    }

    public static void ValidateSanitizedEvidenceText(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value is null)
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (
            string.IsNullOrWhiteSpace(value)
            || value == CdcContractText.EvidenceUnavailable
            || value.Length > MaximumEvidenceTextLength
            || !string.Equals(value, CdcContractText.SanitizeRequired(value), StringComparison.Ordinal)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                path,
                $"CDC observation {fieldName} must be bounded sanitized evidence."
            );
        }
    }

    private static void ValidateDiagnostics(
        IReadOnlyList<CdcDiagnostic>? diagnosticsFromObservation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (diagnosticsFromObservation is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MissingRequiredField,
                "$.diagnostics",
                "Missing required field `diagnostics`."
            );
            return;
        }

        for (int index = 0; index < diagnosticsFromObservation.Count; index++)
        {
            if (diagnosticsFromObservation[index] is null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.MalformedPayload,
                    $"$.diagnostics[{index}]",
                    "CDC observation diagnostics must not contain null items."
                );
            }
        }
    }

    private static bool IsProviderPosition(string value) =>
        IsDecimalInteger(value) || IsPostgresqlWalLsn(value) || IsSqlServerLsn(value);

    private static bool IsDecimalInteger(string value) =>
        value.Length != 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsPostgresqlWalLsn(string value)
    {
        int separatorIndex = value.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex > 0
            && separatorIndex == value.LastIndexOf('/')
            && separatorIndex < value.Length - 1
            && value[..separatorIndex].All(IsHex)
            && value[(separatorIndex + 1)..].All(IsHex);
    }

    private static bool IsSqlServerLsn(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 3
            && parts[0].Length == 8
            && parts[1].Length == 8
            && parts[2].Length == 4
            && Array.TrueForAll(parts, part => part.All(IsHex));
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsLowercaseHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
