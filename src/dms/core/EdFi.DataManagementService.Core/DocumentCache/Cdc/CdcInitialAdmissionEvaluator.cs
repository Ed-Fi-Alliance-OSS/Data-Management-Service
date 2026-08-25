// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcInitialAdmissionEvaluationInput(
    string OperationId,
    DateTimeOffset ObservedAt,
    DateTimeOffset NowUtc,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint,
    InitialCdcProvisioningProof? ProvisioningProof,
    InitialCdcEligibilityObservation? EligibilityObservation,
    CdcBindingStateContract? BindingState
)
{
    public CdcProviderSetupObservation? ProviderSetup { get; init; }

    public CdcKafkaPolicyObservation? KafkaPolicy { get; init; }

    public CdcConnectOffsetStorePolicyObservation? ConnectOffsetStore { get; init; }

    public CdcConnectorConfigurationObservation? ConnectorConfig { get; init; }

    public CdcConnectorRuntimeObservation? ConnectorRuntime { get; init; }

    public CdcProjectionCorrelationObservation? FirstProjectionCaughtUp { get; init; }

    public CdcProviderBarrierObservation? ProviderBarrier { get; init; }

    public CdcSourceHistoryObservation? SourceHistory { get; init; }

    public CdcProjectionCorrelationObservation? SecondProjectionCaughtUp { get; init; }

    public CdcConnectorLagObservation? Lag { get; init; }

    public IReadOnlyList<CdcDiagnostic> StateStoreDiagnostics { get; init; } = [];
}

public static class CdcInitialAdmissionEvaluator
{
    public static CdcAdmission Evaluate(CdcInitialAdmissionEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TargetIdentity);

        DateTimeOffset observedAt = input.ObservedAt.ToUniversalTime();
        DateTimeOffset nowUtc = input.NowUtc.ToUniversalTime();
        CdcDiagnosticCollector diagnostics = new();

        CdcObservationValidationRules.ValidateRequiredToken(
            input.OperationId,
            "$.operationId",
            "operationId",
            diagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(observedAt, nowUtc, "$.observedAt", diagnostics);
        bool topLevelUnavailable = diagnostics.HasDiagnostics;

        CdcTargetStatus status = CdcTargetStatusEvaluator.Evaluate(
            new(input.OperationId, observedAt, input.TargetIdentity, input.PhysicalSourceFingerprint)
            {
                BindingState = input.BindingState,
                Projection = input.SecondProjectionCaughtUp,
                ProviderSetup = input.ProviderSetup,
                ProviderBarrier = input.ProviderBarrier,
                SourceHistory = input.SourceHistory,
                KafkaPolicy = input.KafkaPolicy,
                ConnectOffsetStore = input.ConnectOffsetStore,
                ConnectorConfig = input.ConnectorConfig,
                ConnectorRuntime = input.ConnectorRuntime,
                Lag = input.Lag,
                StateStoreDiagnostics = input.StateStoreDiagnostics,
            }
        );
        AddDiagnostics(diagnostics, status.Diagnostics);

        CdcComponent guardedTrackingActivation = EvaluateGuardedTrackingActivation(
            input,
            observedAt,
            nowUtc,
            diagnostics
        );
        CdcComponent providerSetup = status.ProviderSetup;
        CdcComponent connectorAndTopicValidation = CombineConnectorAndTopicValidation(status, observedAt);
        CdcComponent firstProjectionCaughtUp = EvaluateProjection(
            input.FirstProjectionCaughtUp,
            new(input.OperationId, input.TargetIdentity, input.PhysicalSourceFingerprint, nowUtc),
            "$.firstProjectionCaughtUp",
            "firstProjectionCaughtUp",
            diagnostics
        );
        CdcComponent providerBarrier = status.ProviderBarrier;
        CdcComponent sourceHistory = ToComponent(status.SourceHistory);
        CdcComponent secondProjectionCaughtUp = status.Projection;
        CdcComponent lag = status.Lag;

        (providerBarrier, secondProjectionCaughtUp) = ApplyInitialAdmissionOrdering(
            input.FirstProjectionCaughtUp,
            input.ProviderBarrier,
            input.SecondProjectionCaughtUp,
            providerBarrier,
            secondProjectionCaughtUp,
            diagnostics
        );

        CdcAdmissionSteps steps = new(
            status.Binding,
            guardedTrackingActivation,
            providerSetup,
            connectorAndTopicValidation,
            firstProjectionCaughtUp,
            providerBarrier,
            sourceHistory,
            secondProjectionCaughtUp,
            lag
        );

        List<CdcComponentStatus> componentStatuses =
        [
            Snapshot(steps.Binding),
            Snapshot(steps.GuardedTrackingActivation),
            Snapshot(steps.ProviderSetup),
            Snapshot(steps.ConnectorAndTopicValidation),
            Snapshot(steps.FirstProjectionCaughtUp),
            Snapshot(steps.ProviderBarrier),
            Snapshot(steps.SourceHistory),
            Snapshot(steps.SecondProjectionCaughtUp),
            Snapshot(steps.Lag),
        ];
        if (topLevelUnavailable)
        {
            componentStatuses.Add(
                new(CdcComponentState.Unknown, CdcBlockingCategory.StatusObservationUnavailable)
            );
        }

        CdcReadiness readiness = CdcStatusEvaluationRules.DetermineTargetReadiness(componentStatuses);

        return new(
            CdcJsonContract.CurrentContractVersion,
            input.OperationId,
            observedAt,
            input.TargetIdentity,
            ToAdmissionState(readiness),
            CdcStatusEvaluationRules.SelectTargetPrimaryBlockingCategory(componentStatuses),
            steps,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics.Diagnostics)
        );
    }

    private static CdcComponent EvaluateGuardedTrackingActivation(
        CdcInitialAdmissionEvaluationInput input,
        DateTimeOffset observedAt,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcRetry retry = CdcInitialEnableRetryClassifier.EvaluateRetry(
            new(
                input.OperationId,
                observedAt,
                nowUtc,
                input.TargetIdentity,
                input.PhysicalSourceFingerprint,
                input.ProvisioningProof,
                input.EligibilityObservation,
                input.BindingState
            )
        );
        AddDiagnostics(diagnostics, retry.Diagnostics);

        return retry.RetryClassification switch
        {
            CdcRetryClassification.ResumeProviderTopicConnectorSetup
                when retry.Action == CdcRetryAction.Proceed => CdcComponent.Satisfied(
                retry.ObservedAt,
                "tracking active"
            ),
            CdcRetryClassification.RetryGuardedActivation when retry.Action == CdcRetryAction.Proceed =>
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.ProjectionNonOperational,
                    retry.ObservedAt,
                    "guarded tracking activation pending"
                ),
            _ => retry.PrimaryBlockingCategory == CdcBlockingCategory.StatusObservationUnavailable
                ? CdcComponent.Unknown(
                    CdcBlockingCategory.StatusObservationUnavailable,
                    retry.ObservedAt,
                    "guarded tracking activation unavailable"
                )
                : CdcComponent.NotSatisfied(
                    retry.PrimaryBlockingCategory,
                    retry.ObservedAt,
                    "guarded tracking activation rejected"
                ),
        };
    }

    private static CdcComponent EvaluateProjection(
        CdcProjectionCorrelationObservation? observation,
        CdcObservationValidationContext context,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            diagnostics.MissingRequiredField(path, fieldName);
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                message: $"{fieldName} observation unavailable"
            );
        }

        AddPrefixedDiagnostics(diagnostics, observation.Diagnostics, path);
        CdcContractValidationResult validation = CdcProjectionCorrelationObservationValidator.Validate(
            observation,
            context
        );
        AddPrefixedDiagnostics(diagnostics, validation.Diagnostics, path);
        if (TryClassifyValidationFailure(validation, observation.ObservedAt) is { } validationComponent)
        {
            return validationComponent;
        }

        if (observation.CorrelationState == CdcProjectionCorrelationState.Unavailable)
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "projection observation unavailable"
            );
        }

        if (observation.OperationalHealthStatus == DocumentCacheOperationalHealthStatus.NonOperational)
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.ProjectionNonOperational,
                observation.ObservedAt,
                "projection non-operational"
            );
        }

        if (observation.OperationalHealthStatus == DocumentCacheOperationalHealthStatus.Unknown)
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "projection status unavailable"
            );
        }

        return observation.CaughtUpStatus switch
        {
            DocumentCacheCaughtUpStatus.CaughtUp => CdcComponent.Satisfied(observation.ObservedAt),
            DocumentCacheCaughtUpStatus.NotCaughtUp => CdcComponent.NotSatisfied(
                CdcBlockingCategory.ProjectionBacklog,
                observation.ObservedAt,
                "projection backlog"
            ),
            _ => CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "projection caught-up status unavailable"
            ),
        };
    }

    private static CdcComponent CombineConnectorAndTopicValidation(
        CdcTargetStatus status,
        DateTimeOffset observedAt
    )
    {
        CdcComponentStatus[] componentStatuses =
        [
            Snapshot(status.KafkaPolicy),
            Snapshot(status.ConnectOffsetStore),
            Snapshot(status.ConnectorConfig),
            Snapshot(status.ConnectorRuntime),
        ];
        CdcReadiness readiness = CdcStatusEvaluationRules.DetermineTargetReadiness(componentStatuses);
        DateTimeOffset? latestObservedAt = LatestObservedAt(
            status.KafkaPolicy,
            status.ConnectOffsetStore,
            status.ConnectorConfig,
            status.ConnectorRuntime
        );

        return readiness switch
        {
            CdcReadiness.Ready => CdcComponent.Satisfied(latestObservedAt ?? observedAt),
            CdcReadiness.NotReady => CdcComponent.NotSatisfied(
                CdcStatusEvaluationRules.SelectTargetPrimaryBlockingCategory(componentStatuses),
                latestObservedAt,
                "connector/topic validation failed"
            ),
            _ => CdcComponent.Unknown(
                CdcStatusEvaluationRules.SelectTargetPrimaryBlockingCategory(componentStatuses),
                latestObservedAt,
                "connector/topic validation unavailable"
            ),
        };
    }

    private static (
        CdcComponent ProviderBarrier,
        CdcComponent SecondProjectionCaughtUp
    ) ApplyInitialAdmissionOrdering(
        CdcProjectionCorrelationObservation? firstProjection,
        CdcProviderBarrierObservation? providerBarrierObservation,
        CdcProjectionCorrelationObservation? secondProjection,
        CdcComponent providerBarrier,
        CdcComponent secondProjectionCaughtUp,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (firstProjection is not null && providerBarrierObservation is not null)
        {
            if (
                firstProjection.ProjectionObservedAt
                != providerBarrierObservation.ProjectionCaughtUpObservedAt
            )
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidOrdering,
                    "$.providerBarrier.projectionCaughtUpObservedAt",
                    "CDC initial admission provider barrier must reference the first caught-up projection observation."
                );
                providerBarrier = UnknownForOrdering(
                    providerBarrier,
                    providerBarrierObservation.ObservedAt,
                    "provider barrier ordering unavailable"
                );
            }

            if (firstProjection.ProjectionObservedAt > providerBarrierObservation.BarrierCapturedAt)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidOrdering,
                    "$.firstProjectionCaughtUp.projectionObservedAt",
                    "CDC initial admission first projection caught-up observation must be no later than barrier capture."
                );
                providerBarrier = UnknownForOrdering(
                    providerBarrier,
                    providerBarrierObservation.ObservedAt,
                    "provider barrier ordering unavailable"
                );
            }
        }

        if (
            providerBarrierObservation?.BarrierState == CdcProviderBarrierState.Reached
            && secondProjection is not null
            && secondProjection.ProjectionObservedAt <= providerBarrierObservation.ConnectorOffsetObservedAt
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                "$.secondProjectionCaughtUp.projectionObservedAt",
                "CDC initial admission second projection caught-up observation must be after provider barrier success."
            );
            secondProjectionCaughtUp = UnknownForOrdering(
                secondProjectionCaughtUp,
                secondProjection.ObservedAt,
                "second projection ordering unavailable"
            );
        }

        return (providerBarrier, secondProjectionCaughtUp);
    }

    private static CdcComponent? TryClassifyValidationFailure(
        CdcContractValidationResult validation,
        DateTimeOffset observedAt
    )
    {
        if (validation.Succeeded)
        {
            return null;
        }

        if (HasSourceMismatch(validation.Diagnostics))
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.SourceMismatch,
                observedAt,
                "source mismatch"
            );
        }

        return CdcComponent.Unknown(
            CdcBlockingCategory.StatusObservationUnavailable,
            observedAt,
            "projection observation unavailable"
        );
    }

    private static CdcComponent UnknownForOrdering(
        CdcComponent current,
        DateTimeOffset observedAt,
        string message
    ) =>
        current.Category == CdcBlockingCategory.SourceMismatch
            ? current
            : CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                current.ObservedAt ?? observedAt,
                message
            );

    private static bool HasSourceMismatch(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Category
                is CdcDiagnosticCategory.SourceMismatch
                    or CdcDiagnosticCategory.ProviderMismatch
        );

    private static CdcAdmissionState ToAdmissionState(CdcReadiness readiness) =>
        readiness switch
        {
            CdcReadiness.Ready => CdcAdmissionState.Admitted,
            CdcReadiness.NotReady => CdcAdmissionState.NotAdmitted,
            _ => CdcAdmissionState.Unknown,
        };

    private static CdcComponent ToComponent(CdcSourceHistoryComponent component) =>
        new(component.State, component.Category, component.ObservedAt, component.Message);

    private static CdcComponentStatus Snapshot(CdcComponent component) =>
        new(component.State, component.Category);

    private static DateTimeOffset? LatestObservedAt(params CdcComponent[] components)
    {
        DateTimeOffset[] observedAtValues = components
            .Where(component => component.ObservedAt is not null)
            .Select(component => component.ObservedAt!.Value)
            .ToArray();

        return observedAtValues.Length == 0 ? null : observedAtValues.Max();
    }

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
                diagnostic.WithPath($"{prefix}{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}")
            );
        }
    }
}
