// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcInitialEnablePreBindingEligibilityInput(
    string OperationId,
    DateTimeOffset ObservedAt,
    DateTimeOffset NowUtc,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint,
    InitialCdcProvisioningProof? ProvisioningProof,
    InitialCdcEligibilityObservation? EligibilityObservation
);

public sealed record CdcInitialEnablePreBindingEligibilityResult(
    bool CanCreateBinding,
    CdcRetry? Rejection,
    IReadOnlyList<CdcDiagnostic> Diagnostics
);

public sealed record CdcInitialEnableRetryClassificationInput(
    string OperationId,
    DateTimeOffset ObservedAt,
    DateTimeOffset NowUtc,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint,
    InitialCdcProvisioningProof? ProvisioningProof,
    InitialCdcEligibilityObservation? EligibilityObservation,
    CdcBindingStateContract? BindingState
);

public static class CdcInitialEnableRetryClassifier
{
    public static CdcInitialEnablePreBindingEligibilityResult EvaluatePreBindingEligibility(
        CdcInitialEnablePreBindingEligibilityInput input
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TargetIdentity);

        DateTimeOffset observedAt = input.ObservedAt.ToUniversalTime();
        EvidenceEvaluation evidence = EvaluateEvidence(
            input.OperationId,
            observedAt,
            input.NowUtc,
            input.TargetIdentity,
            input.PhysicalSourceFingerprint,
            input.ProvisioningProof,
            input.EligibilityObservation
        );

        CdcRetry? rejection = ClassifyPreBindingRejection(input, evidence, observedAt);
        return new(
            rejection is null,
            rejection,
            CdcDiagnostic.NormalizeDiagnostics(evidence.Diagnostics.Diagnostics)
        );
    }

    public static CdcRetry EvaluateRetry(CdcInitialEnableRetryClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TargetIdentity);

        DateTimeOffset observedAt = input.ObservedAt.ToUniversalTime();
        EvidenceEvaluation evidence = EvaluateEvidence(
            input.OperationId,
            observedAt,
            input.NowUtc,
            input.TargetIdentity,
            input.PhysicalSourceFingerprint,
            input.ProvisioningProof,
            input.EligibilityObservation
        );

        if (!evidence.ValidExceptRows)
        {
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            );
        }

        CdcRetry? bindingRejection = TryRejectBindingState(input, evidence, observedAt);
        if (bindingRejection is not null)
        {
            return bindingRejection;
        }

        return ClassifyLifecycleRetry(input, evidence, observedAt);
    }

    private static CdcRetry? ClassifyPreBindingRejection(
        CdcInitialEnablePreBindingEligibilityInput input,
        EvidenceEvaluation evidence,
        DateTimeOffset observedAt
    )
    {
        if (!evidence.ValidExceptRows)
        {
            return Retry(
                input.OperationId,
                observedAt,
                input.TargetIdentity,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            );
        }

        InitialCdcEligibilityObservation observation = evidence.EligibilityObservation!;
        return ClassifyLifecycleState(
            input.OperationId,
            observedAt,
            input.TargetIdentity,
            observation,
            evidence.Diagnostics,
            bindingIsPresent: false
        );
    }

    private static CdcRetry? TryRejectBindingState(
        CdcInitialEnableRetryClassificationInput input,
        EvidenceEvaluation evidence,
        DateTimeOffset observedAt
    )
    {
        if (input.BindingState is null)
        {
            evidence.Diagnostics.LocalStateUnavailable(
                "$.bindingState",
                "CDC retry binding state is unavailable."
            );
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            );
        }

        CdcBindingStateContract bindingState = input.BindingState;
        CdcObservationValidationRules.ValidateContractVersion(
            bindingState.ContractVersion,
            "$.bindingState.contractVersion",
            evidence.Diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            bindingState.ObservedAt,
            observedAt,
            "$.bindingState.observedAt",
            evidence.Diagnostics
        );

        if (!Enum.IsDefined(bindingState.State))
        {
            evidence.Diagnostics.InvalidEnumValue(
                "$.bindingState.state",
                "CDC retry binding state is unsupported."
            );
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            );
        }

        if (bindingState.State == CdcBindingState.BindingMismatch)
        {
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectBindingMismatch,
                CdcBlockingCategory.BindingMismatch,
                evidence.Diagnostics
            );
        }

        if (bindingState.State == CdcBindingState.BindingMissing)
        {
            return MissingBindingRetry(input, evidence, observedAt);
        }

        if (bindingState.State == CdcBindingState.IncidentLatched)
        {
            evidence.Diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                "$.bindingState.state",
                "CDC initial-enable retry does not accept an incident-latched binding state."
            );
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.SourceHistoryLost,
                evidence.Diagnostics
            );
        }

        return ValidateExactBinding(input, evidence, observedAt);
    }

    private static CdcRetry MissingBindingRetry(
        CdcInitialEnableRetryClassificationInput input,
        EvidenceEvaluation evidence,
        DateTimeOffset observedAt
    )
    {
        if (evidence.EligibilityObservation?.LifecycleState == CdcLifecycleState.Tracking)
        {
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectUnboundTracking,
                CdcBlockingCategory.BindingMissing,
                evidence.Diagnostics
            );
        }

        return Retry(
            input,
            observedAt,
            CdcRetryClassification.RejectNotInitialWorkflow,
            CdcBlockingCategory.BindingMissing,
            evidence.Diagnostics
        );
    }

    private static CdcRetry? ValidateExactBinding(
        CdcInitialEnableRetryClassificationInput input,
        EvidenceEvaluation evidence,
        DateTimeOffset observedAt
    )
    {
        CdcBinding? binding = input.BindingState?.Binding;
        if (binding is null)
        {
            evidence.Diagnostics.MissingRequiredField("$.bindingState.binding", "binding");
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            );
        }

        CdcDiagnosticCollector bindingDiagnostics = new();
        CdcProofValidationRules.ValidateBinding(binding, "$.bindingState.binding", bindingDiagnostics);
        if (
            binding.ToTargetIdentity() != input.TargetIdentity
            || !string.Equals(
                binding.PhysicalSourceFingerprint,
                input.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            bindingDiagnostics.Add(
                CdcDiagnosticCategory.BindingIdentityMismatch,
                "$.bindingState.binding",
                "CDC retry binding must exact-match the current target and physical source."
            );
        }

        AddDiagnostics(evidence.Diagnostics, bindingDiagnostics.Diagnostics);
        if (bindingDiagnostics.HasDiagnostics)
        {
            return Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectBindingMismatch,
                CdcBlockingCategory.BindingMismatch,
                evidence.Diagnostics
            );
        }

        return null;
    }

    private static CdcRetry ClassifyLifecycleRetry(
        CdcInitialEnableRetryClassificationInput input,
        EvidenceEvaluation evidence,
        DateTimeOffset observedAt
    )
    {
        InitialCdcEligibilityObservation observation = evidence.EligibilityObservation!;
        CdcRetry? rejection = ClassifyLifecycleState(
            input.OperationId,
            observedAt,
            input.TargetIdentity,
            observation,
            evidence.Diagnostics,
            bindingIsPresent: true
        );

        if (rejection is not null)
        {
            return rejection;
        }

        return observation.LifecycleState switch
        {
            CdcLifecycleState.Disabled => Retry(
                input,
                observedAt,
                CdcRetryClassification.RetryGuardedActivation,
                CdcBlockingCategory.None,
                evidence.Diagnostics
            ),
            CdcLifecycleState.Tracking => Retry(
                input,
                observedAt,
                CdcRetryClassification.ResumeProviderTopicConnectorSetup,
                CdcBlockingCategory.None,
                evidence.Diagnostics
            ),
            _ => Retry(
                input,
                observedAt,
                CdcRetryClassification.RejectNotInitialWorkflow,
                CdcBlockingCategory.StatusObservationUnavailable,
                evidence.Diagnostics
            ),
        };
    }

    private static CdcRetry? ClassifyLifecycleState(
        string operationId,
        DateTimeOffset observedAt,
        CdcTargetIdentity targetIdentity,
        InitialCdcEligibilityObservation observation,
        CdcDiagnosticCollector diagnostics,
        bool bindingIsPresent
    )
    {
        if (observation.LifecycleState == CdcLifecycleState.Tracking && !bindingIsPresent)
        {
            return Retry(
                operationId,
                observedAt,
                targetIdentity,
                CdcRetryClassification.RejectUnboundTracking,
                CdcBlockingCategory.BindingMissing,
                diagnostics
            );
        }

        if (observation.LifecycleState == CdcLifecycleState.Resetting)
        {
            return Retry(
                operationId,
                observedAt,
                targetIdentity,
                CdcRetryClassification.RejectResettingLifecycle,
                CdcBlockingCategory.ProjectionNonOperational,
                diagnostics
            );
        }

        if (observation.LifecycleState == CdcLifecycleState.Rebuilding)
        {
            return Retry(
                operationId,
                observedAt,
                targetIdentity,
                CdcRetryClassification.RejectRebuildingLifecycle,
                CdcBlockingCategory.ProjectionNonOperational,
                diagnostics
            );
        }

        if (observation.CacheAheadState == CdcCacheAheadState.RecoveryRequired)
        {
            return Retry(
                operationId,
                observedAt,
                targetIdentity,
                CdcRetryClassification.RejectCacheAheadLatch,
                CdcBlockingCategory.ProjectionNonOperational,
                diagnostics
            );
        }

        if (HasUnexpectedRows(observation))
        {
            return Retry(
                operationId,
                observedAt,
                targetIdentity,
                CdcRetryClassification.RejectUnexpectedRows,
                CdcBlockingCategory.ProjectionBacklog,
                diagnostics
            );
        }

        return null;
    }

    private static EvidenceEvaluation EvaluateEvidence(
        string operationId,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcTargetIdentity targetIdentity,
        string? physicalSourceFingerprint,
        InitialCdcProvisioningProof? provisioningProof,
        InitialCdcEligibilityObservation? eligibilityObservation
    )
    {
        DateTimeOffset normalizedNowUtc = nowUtc.ToUniversalTime();
        CdcDiagnosticCollector diagnostics = new();
        CdcObservationValidationRules.ValidateTimestamp(
            observedAt,
            normalizedNowUtc,
            "$.observedAt",
            diagnostics
        );

        if (string.IsNullOrWhiteSpace(physicalSourceFingerprint))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.SourceMismatch,
                "$.physicalSourceFingerprint",
                "CDC initial-enable classification requires a resolved physical source fingerprint."
            );
        }

        CdcObservationValidationContext context = new(
            operationId,
            targetIdentity,
            physicalSourceFingerprint,
            normalizedNowUtc
        );

        if (provisioningProof is null)
        {
            diagnostics.MissingRequiredField("$.provisioningProof", "provisioningProof");
        }
        else
        {
            AddDiagnostics(
                diagnostics,
                InitialCdcProvisioningProofValidator.Validate(provisioningProof, context).Diagnostics
            );
        }

        if (eligibilityObservation is null)
        {
            diagnostics.MissingRequiredField("$.eligibilityObservation", "eligibilityObservation");
        }
        else if (provisioningProof is not null)
        {
            AddDiagnostics(
                diagnostics,
                InitialCdcEligibilityObservationValidator
                    .Validate(eligibilityObservation, provisioningProof, context)
                    .Diagnostics
            );
        }

        bool validExceptRows = !diagnostics.Diagnostics.Any(IsNotRowPresenceDiagnostic);
        return new(eligibilityObservation, diagnostics, validExceptRows);
    }

    private static bool IsNotRowPresenceDiagnostic(CdcDiagnostic diagnostic) =>
        diagnostic.Category != CdcDiagnosticCategory.InvalidObservation
        || diagnostic.Path is not ("$.canonicalRowsPresent" or "$.cacheRowsPresent" or "$.workRowsPresent");

    private static bool HasUnexpectedRows(InitialCdcEligibilityObservation observation) =>
        observation.CanonicalRowsPresent || observation.CacheRowsPresent || observation.WorkRowsPresent;

    private static CdcRetry Retry(
        CdcInitialEnableRetryClassificationInput input,
        DateTimeOffset observedAt,
        CdcRetryClassification classification,
        CdcBlockingCategory primaryBlockingCategory,
        CdcDiagnosticCollector diagnostics
    ) =>
        Retry(
            input.OperationId,
            observedAt,
            input.TargetIdentity,
            classification,
            primaryBlockingCategory,
            diagnostics
        );

    private static CdcRetry Retry(
        string operationId,
        DateTimeOffset observedAt,
        CdcTargetIdentity targetIdentity,
        CdcRetryClassification classification,
        CdcBlockingCategory primaryBlockingCategory,
        CdcDiagnosticCollector diagnostics
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            operationId,
            observedAt,
            targetIdentity,
            classification,
            ActionFor(classification),
            primaryBlockingCategory,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics.Diagnostics)
        );

    private static CdcRetryAction ActionFor(CdcRetryClassification classification) =>
        classification switch
        {
            CdcRetryClassification.RetryGuardedActivation => CdcRetryAction.Proceed,
            CdcRetryClassification.ResumeProviderTopicConnectorSetup => CdcRetryAction.Proceed,
            CdcRetryClassification.RejectNotInitialWorkflow =>
                CdcRetryAction.RetireUnusedBindingAndReprovision,
            _ => CdcRetryAction.FailClosed,
        };

    private static void AddDiagnostics(
        CdcDiagnosticCollector collector,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        foreach (CdcDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic is not null))
        {
            collector.Add(diagnostic);
        }
    }

    private sealed record EvidenceEvaluation(
        InitialCdcEligibilityObservation? EligibilityObservation,
        CdcDiagnosticCollector Diagnostics,
        bool ValidExceptRows
    );
}
