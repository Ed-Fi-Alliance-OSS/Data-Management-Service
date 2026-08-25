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

internal static class CdcObservationValidationRules
{
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
                ValidateSha256(physicalSourceFingerprint, path, "physicalSourceFingerprint", diagnostics);
            }

            return;
        }

        ValidateSha256(physicalSourceFingerprint, path, "physicalSourceFingerprint", diagnostics);
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

    private static void ValidateSha256(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        const string sha256Prefix = "sha256:";
        if (
            value is null
            || value.Length != sha256Prefix.Length + 64
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

    private static bool IsLowercaseHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
