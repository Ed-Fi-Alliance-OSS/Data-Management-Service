// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcTargetStatusEvaluationInput(
    string OperationId,
    DateTimeOffset ObservedAt,
    CdcTargetIdentity TargetIdentity,
    string? PhysicalSourceFingerprint
)
{
    public CdcBindingStateContract? BindingState { get; init; }

    public CdcProjectionCorrelationObservation? Projection { get; init; }

    public CdcProviderSetupObservation? ProviderSetup { get; init; }

    public CdcProviderBarrierObservation? ProviderBarrier { get; init; }

    public CdcSourceHistoryObservation? SourceHistory { get; init; }

    public CdcKafkaPolicyObservation? KafkaPolicy { get; init; }

    public CdcConnectOffsetStorePolicyObservation? ConnectOffsetStore { get; init; }

    public CdcConnectorConfigurationObservation? ConnectorConfig { get; init; }

    public CdcConnectorRuntimeObservation? ConnectorRuntime { get; init; }

    public CdcConnectorLagObservation? Lag { get; init; }

    public IReadOnlyList<CdcDiagnostic> StateStoreDiagnostics { get; init; } = [];
}

public static class CdcTargetStatusEvaluator
{
    private const int MaximumDiagnostics = 16;

    private static readonly CdcBlockingCategory[] BlockingPrecedence =
    [
        CdcBlockingCategory.BindingMissing,
        CdcBlockingCategory.BindingMismatch,
        CdcBlockingCategory.SourceMismatch,
        CdcBlockingCategory.SourceHistoryLost,
        CdcBlockingCategory.ProjectionNonOperational,
        CdcBlockingCategory.ProviderSetupInvalid,
        CdcBlockingCategory.KafkaPolicyInvalid,
        CdcBlockingCategory.ConnectOffsetStoreInvalid,
        CdcBlockingCategory.ConnectorConfigInvalid,
        CdcBlockingCategory.ConnectorNotRunning,
        CdcBlockingCategory.SnapshotIncomplete,
        CdcBlockingCategory.ProjectionBacklog,
        CdcBlockingCategory.ProviderHistoryUnknown,
        CdcBlockingCategory.ProviderBarrierNotReached,
        CdcBlockingCategory.LagExceeded,
        CdcBlockingCategory.StatusObservationUnavailable,
    ];

    public static CdcTargetStatus Evaluate(CdcTargetStatusEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TargetIdentity);

        DateTimeOffset observedAt = input.ObservedAt.ToUniversalTime();
        CdcDiagnosticCollector diagnostics = new();
        AddDiagnostics(diagnostics, input.StateStoreDiagnostics);

        BindingEvaluation binding = EvaluateBindingState(input, observedAt, diagnostics);
        CdcObservationValidationContext context = new(
            input.OperationId,
            input.TargetIdentity,
            input.PhysicalSourceFingerprint ?? binding.Binding?.PhysicalSourceFingerprint,
            observedAt
        );

        CdcComponent projection = EvaluateProjection(input.Projection, context, diagnostics);
        CdcComponent providerSetup = EvaluateProviderSetup(input.ProviderSetup, context, diagnostics);
        CdcComponent providerBarrier = EvaluateProviderBarrier(input.ProviderBarrier, context, diagnostics);
        CdcSourceHistoryComponent sourceHistory = EvaluateSourceHistory(
            input.SourceHistory,
            binding.Binding,
            binding.ValidIncident,
            context,
            diagnostics
        );
        CdcComponent kafkaPolicy = EvaluateKafkaPolicy(
            input.KafkaPolicy,
            binding.Binding,
            context,
            diagnostics
        );
        CdcComponent connectOffsetStore = EvaluateConnectOffsetStore(
            input.ConnectOffsetStore,
            context,
            diagnostics
        );
        CdcComponent connectorConfig = EvaluateConnectorConfig(
            input.ConnectorConfig,
            binding.Binding,
            context,
            diagnostics
        );
        CdcComponent connectorRuntime = EvaluateConnectorRuntime(
            input.ConnectorRuntime,
            binding.Binding,
            context,
            diagnostics
        );
        CdcComponent lag = EvaluateLag(input.Lag, context, diagnostics);

        ComponentSnapshot[] components =
        [
            Snapshot(binding.Component),
            Snapshot(projection),
            Snapshot(providerSetup),
            Snapshot(providerBarrier),
            Snapshot(sourceHistory),
            Snapshot(kafkaPolicy),
            Snapshot(connectOffsetStore),
            Snapshot(connectorConfig),
            Snapshot(connectorRuntime),
            Snapshot(lag),
        ];

        return new(
            input.TargetIdentity,
            DetermineReadiness(components),
            SelectPrimaryBlockingCategory(components),
            binding.Component,
            projection,
            providerSetup,
            providerBarrier,
            sourceHistory,
            kafkaPolicy,
            connectOffsetStore,
            connectorConfig,
            connectorRuntime,
            lag,
            LimitDiagnostics(diagnostics.Diagnostics)
        );
    }

    private static BindingEvaluation EvaluateBindingState(
        CdcTargetStatusEvaluationInput input,
        DateTimeOffset observedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (input.BindingState is null)
        {
            if (input.StateStoreDiagnostics.Count == 0)
            {
                diagnostics.LocalStateUnavailable(
                    "$.bindingState",
                    "CDC binding state observation is unavailable."
                );
            }

            return new(
                CdcComponent.Unknown(
                    CdcBlockingCategory.StatusObservationUnavailable,
                    message: "binding state unavailable"
                ),
                null,
                null
            );
        }

        CdcBindingStateContract bindingState = input.BindingState;
        CdcDiagnosticCollector bindingDiagnostics = new();

        CdcObservationValidationRules.ValidateContractVersion(
            bindingState.ContractVersion,
            "$.bindingState.contractVersion",
            bindingDiagnostics
        );
        CdcObservationValidationRules.ValidateTimestamp(
            bindingState.ObservedAt,
            observedAt,
            "$.bindingState.observedAt",
            bindingDiagnostics
        );
        ValidateBindingStateShape(bindingState, bindingDiagnostics);

        if (bindingDiagnostics.HasDiagnostics)
        {
            AddDiagnostics(diagnostics, bindingDiagnostics.Diagnostics);
            return InvalidBindingState();
        }

        if (bindingState.Binding is not null)
        {
            CdcDiagnosticCollector persistedBindingDiagnostics = new();
            CdcProofValidationRules.ValidateBinding(
                bindingState.Binding,
                "$.bindingState.binding",
                persistedBindingDiagnostics
            );

            if (persistedBindingDiagnostics.HasDiagnostics)
            {
                AddDiagnostics(diagnostics, persistedBindingDiagnostics.Diagnostics);
                return InvalidBindingState();
            }
        }

        return bindingState.State switch
        {
            CdcBindingState.BindingMissing => new(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.BindingMissing,
                    bindingState.ObservedAt,
                    "binding missing"
                ),
                null,
                null
            ),
            CdcBindingState.BindingMismatch => new(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.BindingMismatch,
                    bindingState.ObservedAt,
                    "binding mismatch"
                ),
                bindingState.Binding,
                null
            ),
            CdcBindingState.BindingPresent => EvaluatePresentBinding(
                bindingState.Binding!,
                input,
                bindingState.ObservedAt
            ),
            CdcBindingState.IncidentLatched => EvaluateIncidentLatchedBinding(
                bindingState,
                input,
                observedAt,
                diagnostics
            ),
            _ => InvalidBindingState(),
        };

        static BindingEvaluation InvalidBindingState() =>
            new(
                CdcComponent.Unknown(
                    CdcBlockingCategory.StatusObservationUnavailable,
                    message: "binding state unavailable"
                ),
                null,
                null
            );
    }

    private static void ValidateBindingStateShape(
        CdcBindingStateContract bindingState,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(bindingState.State))
        {
            diagnostics.InvalidEnumValue("$.bindingState.state", "CDC binding-state state is unsupported.");
            return;
        }

        switch (bindingState.State)
        {
            case CdcBindingState.BindingPresent:
                RequireBinding(bindingState.Binding, diagnostics);
                RejectIncident(bindingState.Incident, diagnostics);
                break;
            case CdcBindingState.BindingMissing:
                RejectBinding(bindingState.Binding, diagnostics);
                RejectIncident(bindingState.Incident, diagnostics);
                break;
            case CdcBindingState.BindingMismatch:
                RejectIncident(bindingState.Incident, diagnostics);
                break;
            case CdcBindingState.IncidentLatched:
                RequireBinding(bindingState.Binding, diagnostics);
                if (bindingState.Incident is null)
                {
                    diagnostics.MissingRequiredField("$.bindingState.incident", "incident");
                }

                break;
        }

        static void RequireBinding(CdcBinding? binding, CdcDiagnosticCollector diagnostics)
        {
            if (binding is null)
            {
                diagnostics.MissingRequiredField("$.bindingState.binding", "binding");
            }
        }

        static void RejectBinding(CdcBinding? binding, CdcDiagnosticCollector diagnostics)
        {
            if (binding is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.bindingState.binding",
                    "CDC binding-state missing state must not include a binding."
                );
            }
        }

        static void RejectIncident(CdcIncident? incident, CdcDiagnosticCollector diagnostics)
        {
            if (incident is not null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InvalidObservation,
                    "$.bindingState.incident",
                    "CDC binding-state incident is valid only when state is incidentLatched."
                );
            }
        }
    }

    private static BindingEvaluation EvaluatePresentBinding(
        CdcBinding binding,
        CdcTargetStatusEvaluationInput input,
        DateTimeOffset observedAt
    )
    {
        if (binding.ToTargetIdentity() != input.TargetIdentity)
        {
            return new(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.BindingMismatch,
                    observedAt,
                    "binding identity mismatch"
                ),
                binding,
                null
            );
        }

        if (
            input.PhysicalSourceFingerprint is not null
            && !string.Equals(
                binding.PhysicalSourceFingerprint,
                input.PhysicalSourceFingerprint,
                StringComparison.Ordinal
            )
        )
        {
            return new(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.SourceMismatch,
                    observedAt,
                    "binding source mismatch"
                ),
                binding,
                null
            );
        }

        return new(CdcComponent.Satisfied(observedAt), binding, null);
    }

    private static BindingEvaluation EvaluateIncidentLatchedBinding(
        CdcBindingStateContract bindingState,
        CdcTargetStatusEvaluationInput input,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        BindingEvaluation bindingEvaluation = EvaluatePresentBinding(
            bindingState.Binding!,
            input,
            bindingState.ObservedAt
        );
        if (bindingEvaluation.Component.State != CdcComponentState.Satisfied)
        {
            return bindingEvaluation;
        }

        CdcContractValidationResult incidentValidation = CdcIncidentValidator.ValidateForBinding(
            bindingState.Incident!,
            bindingState.Binding!,
            nowUtc
        );
        if (!incidentValidation.Succeeded)
        {
            foreach (CdcDiagnostic diagnostic in incidentValidation.Diagnostics)
            {
                diagnostics.Add(
                    new(
                        diagnostic.Category,
                        $"$.bindingState.incident{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                        "CDC binding-state incident must match the parsed binding."
                    )
                );
            }

            return new(
                CdcComponent.Unknown(
                    CdcBlockingCategory.StatusObservationUnavailable,
                    bindingState.ObservedAt,
                    "binding incident unavailable"
                ),
                bindingState.Binding,
                null
            );
        }

        return bindingEvaluation with
        {
            ValidIncident = bindingState.Incident,
        };
    }

    private static CdcComponent EvaluateProjection(
        CdcProjectionCorrelationObservation? observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.projection", "projection", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = CdcProjectionCorrelationObservationValidator.Validate(
            observation,
            context
        );
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(validation, observation.ObservedAt, CdcBlockingCategory.None) is
            { } validationComponent
        )
        {
            return validationComponent.Category == CdcBlockingCategory.SourceMismatch
                ? validationComponent
                : CdcComponent.Unknown(
                    CdcBlockingCategory.StatusObservationUnavailable,
                    observation.ObservedAt,
                    "projection observation unavailable"
                );
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

    private static CdcComponent EvaluateProviderSetup(
        CdcProviderSetupObservation? observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.providerSetup", "providerSetup", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = CdcProviderSetupObservationValidator.Validate(
            observation,
            context
        );
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.ProviderSetupInvalid
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Invalid
            || HasProviderSetupState(observation, CdcProviderSetupState.Missing)
            || HasProviderSetupState(observation, CdcProviderSetupState.Mismatched)
        )
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.ProviderSetupInvalid,
                observation.ObservedAt,
                "provider setup invalid"
            );
        }

        if (
            observation.SetupOutcome == CdcProviderSetupOutcome.Unknown
            || HasProviderSetupState(observation, CdcProviderSetupState.Unknown)
        )
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "provider setup status unavailable"
            );
        }

        return CdcComponent.Satisfied(observation.ObservedAt);
    }

    private static CdcComponent EvaluateProviderBarrier(
        CdcProviderBarrierObservation? observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.providerBarrier", "providerBarrier", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = CdcProviderBarrierObservationValidator.Validate(
            observation,
            context
        );
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.ProviderBarrierNotReached
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        return observation.BarrierState switch
        {
            CdcProviderBarrierState.Reached => CdcComponent.Satisfied(observation.ObservedAt),
            CdcProviderBarrierState.NotReached => CdcComponent.NotSatisfied(
                CdcBlockingCategory.ProviderBarrierNotReached,
                observation.ObservedAt,
                "provider barrier not reached"
            ),
            _ => CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "provider barrier unavailable"
            ),
        };
    }

    private static CdcSourceHistoryComponent EvaluateSourceHistory(
        CdcSourceHistoryObservation? observation,
        CdcBinding? binding,
        CdcIncident? validIncident,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (validIncident is not null)
        {
            return CdcSourceHistoryComponent.FromComponent(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.SourceHistoryLost,
                    validIncident.LatchedAt,
                    "source-history incident latched"
                ),
                CdcSourceHistoryContinuity.Lost,
                incidentLatched: true
            );
        }

        if (observation is null)
        {
            return CdcSourceHistoryComponent.FromComponent(
                MissingObservation("$.sourceHistory", "sourceHistory", diagnostics),
                CdcSourceHistoryContinuity.Unknown,
                incidentLatched: false
            );
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = binding is null
            ? CdcSourceHistoryObservationValidator.Validate(observation, context)
            : CdcSourceHistoryObservationValidator.ValidateForBinding(observation, binding, context);
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.SourceHistoryLost
            ) is
            { } validationComponent
        )
        {
            return CdcSourceHistoryComponent.FromComponent(
                validationComponent,
                validationComponent.Category == CdcBlockingCategory.SourceHistoryLost
                    ? CdcSourceHistoryContinuity.Lost
                    : CdcSourceHistoryContinuity.Unknown,
                observation.IncidentLatched
            );
        }

        return observation.Continuity switch
        {
            CdcSourceHistoryContinuity.Healthy => CdcSourceHistoryComponent.FromComponent(
                CdcComponent.Satisfied(observation.ObservedAt),
                CdcSourceHistoryContinuity.Healthy,
                incidentLatched: false
            ),
            CdcSourceHistoryContinuity.Lost => CdcSourceHistoryComponent.FromComponent(
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.SourceHistoryLost,
                    observation.ObservedAt,
                    "source-history continuity lost"
                ),
                CdcSourceHistoryContinuity.Lost,
                observation.IncidentLatched
            ),
            _ => CdcSourceHistoryComponent.FromComponent(
                CdcComponent.Unknown(
                    CdcBlockingCategory.ProviderHistoryUnknown,
                    observation.ObservedAt,
                    "provider history unavailable"
                ),
                CdcSourceHistoryContinuity.Unknown,
                incidentLatched: false
            ),
        };
    }

    private static CdcComponent EvaluateKafkaPolicy(
        CdcKafkaPolicyObservation? observation,
        CdcBinding? binding,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.kafkaPolicy", "kafkaPolicy", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = binding is null
            ? CdcKafkaPolicyObservationValidator.Validate(observation, context)
            : CdcKafkaPolicyObservationValidator.ValidateForBinding(observation, binding, context);
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.KafkaPolicyInvalid
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        if (observation.PolicyState == CdcKafkaPolicyState.Invalid || HasInvalidKafkaItem(observation))
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.KafkaPolicyInvalid,
                observation.ObservedAt,
                "Kafka policy invalid"
            );
        }

        if (observation.PolicyState == CdcKafkaPolicyState.Unknown || HasUnknownKafkaItem(observation))
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "Kafka policy unavailable"
            );
        }

        return CdcComponent.Satisfied(observation.ObservedAt);
    }

    private static CdcComponent EvaluateConnectOffsetStore(
        CdcConnectOffsetStorePolicyObservation? observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.connectOffsetStore", "connectOffsetStore", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = CdcConnectOffsetStorePolicyObservationValidator.Validate(
            observation,
            context
        );
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.ConnectOffsetStoreInvalid
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        if (
            observation.PolicyState == CdcConnectOffsetStorePolicyState.Invalid
            || observation.AclState == CdcConnectOffsetStoreItemState.Invalid
        )
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.ConnectOffsetStoreInvalid,
                observation.ObservedAt,
                "Connect offset-store policy invalid"
            );
        }

        if (
            observation.PolicyState == CdcConnectOffsetStorePolicyState.Unknown
            || observation.AclState == CdcConnectOffsetStoreItemState.Unknown
        )
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "Connect offset-store policy unavailable"
            );
        }

        return CdcComponent.Satisfied(observation.ObservedAt);
    }

    private static CdcComponent EvaluateConnectorConfig(
        CdcConnectorConfigurationObservation? observation,
        CdcBinding? binding,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.connectorConfig", "connectorConfig", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = binding is null
            ? CdcConnectorConfigurationObservationValidator.Validate(observation, context)
            : CdcConnectorConfigurationObservationValidator.ValidateForBinding(observation, binding, context);
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.ConnectorConfigInvalid
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        if (
            observation.ConfigurationState == CdcConnectorConfigurationState.Invalid
            || observation.TaskCount != 1
            || HasConnectorConfigurationItem(observation, CdcConnectorConfigurationItemState.Invalid)
        )
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.ConnectorConfigInvalid,
                observation.ObservedAt,
                "connector configuration invalid"
            );
        }

        if (
            observation.ConfigurationState == CdcConnectorConfigurationState.Unknown
            || observation.TaskCount is null
            || HasConnectorConfigurationItem(observation, CdcConnectorConfigurationItemState.Unknown)
        )
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "connector configuration unavailable"
            );
        }

        return CdcComponent.Satisfied(observation.ObservedAt);
    }

    private static CdcComponent EvaluateConnectorRuntime(
        CdcConnectorRuntimeObservation? observation,
        CdcBinding? binding,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.connectorRuntime", "connectorRuntime", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = binding is null
            ? CdcConnectorRuntimeObservationValidator.Validate(observation, context)
            : CdcConnectorRuntimeObservationValidator.ValidateForBinding(observation, binding, context);
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.ConnectorNotRunning
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        if (
            observation.ConnectorState == CdcConnectorRuntimeState.Unknown
            || observation.SoleTaskState == CdcConnectorRuntimeState.Unknown
            || observation.TaskCount is null
            || observation.RunningTaskCount is null
        )
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "connector runtime unavailable"
            );
        }

        if (
            observation.ConnectorState != CdcConnectorRuntimeState.Running
            || observation.SoleTaskState != CdcConnectorRuntimeState.Running
            || observation.TaskCount != 1
            || observation.RunningTaskCount != 1
        )
        {
            return CdcComponent.NotSatisfied(
                CdcBlockingCategory.ConnectorNotRunning,
                observation.ObservedAt,
                "connector not running"
            );
        }

        return observation.SnapshotState switch
        {
            CdcConnectorSnapshotState.Completed or CdcConnectorSnapshotState.NotApplicable =>
                CdcComponent.Satisfied(observation.ObservedAt),
            CdcConnectorSnapshotState.Unknown => CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "connector snapshot status unavailable"
            ),
            _ => CdcComponent.NotSatisfied(
                CdcBlockingCategory.SnapshotIncomplete,
                observation.ObservedAt,
                "connector snapshot incomplete"
            ),
        };
    }

    private static CdcComponent EvaluateLag(
        CdcConnectorLagObservation? observation,
        CdcObservationValidationContext context,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (observation is null)
        {
            return MissingObservation("$.lag", "lag", diagnostics);
        }

        AddDiagnostics(diagnostics, observation.Diagnostics);
        CdcContractValidationResult validation = CdcConnectorLagObservationValidator.Validate(
            observation,
            context
        );
        AddDiagnostics(diagnostics, validation.Diagnostics);
        if (
            TryClassifyValidationFailure(
                validation,
                observation.ObservedAt,
                CdcBlockingCategory.LagExceeded
            ) is
            { } validationComponent
        )
        {
            return validationComponent;
        }

        return observation.LagState switch
        {
            CdcConnectorLagState.WithinThreshold => CdcComponent.Satisfied(observation.ObservedAt),
            CdcConnectorLagState.Exceeded => CdcComponent.NotSatisfied(
                CdcBlockingCategory.LagExceeded,
                observation.ObservedAt,
                "connector lag exceeded"
            ),
            _ => CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observation.ObservedAt,
                "connector lag unavailable"
            ),
        };
    }

    private static CdcComponent MissingObservation(
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        diagnostics.MissingRequiredField(path, fieldName);
        return CdcComponent.Unknown(
            CdcBlockingCategory.StatusObservationUnavailable,
            message: $"{fieldName} observation unavailable"
        );
    }

    private static CdcComponent? TryClassifyValidationFailure(
        CdcContractValidationResult validation,
        DateTimeOffset observedAt,
        CdcBlockingCategory invalidCategory
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

        if (HasUnavailableDiagnostics(validation.Diagnostics) || invalidCategory == CdcBlockingCategory.None)
        {
            return CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                observedAt,
                "status observation unavailable"
            );
        }

        return CdcComponent.NotSatisfied(invalidCategory, observedAt, "status observation invalid");
    }

    private static bool HasSourceMismatch(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Category
                is CdcDiagnosticCategory.SourceMismatch
                    or CdcDiagnosticCategory.ProviderMismatch
        );

    private static bool HasUnavailableDiagnostics(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Category
                is not CdcDiagnosticCategory.SourceMismatch
                    and not CdcDiagnosticCategory.ProviderMismatch
                    and not CdcDiagnosticCategory.ArtifactNameMismatch
                    and not CdcDiagnosticCategory.InvalidObservation
        );

    private static bool HasProviderSetupState(
        CdcProviderSetupObservation observation,
        CdcProviderSetupState state
    ) =>
        observation.ArtifactInventoryState == state
        || observation.GrantInventoryState == state
        || observation.SourceInventoryState == state
        || observation.HeartbeatState == state
        || observation.ProviderHistoryState == state;

    private static bool HasInvalidKafkaItem(CdcKafkaPolicyObservation observation) =>
        KafkaItemStates(observation).Any(state => state == CdcKafkaPolicyItemState.Invalid);

    private static bool HasUnknownKafkaItem(CdcKafkaPolicyObservation observation) =>
        KafkaItemStates(observation).Any(state => state == CdcKafkaPolicyItemState.Unknown);

    private static IEnumerable<CdcKafkaPolicyItemState> KafkaItemStates(CdcKafkaPolicyObservation observation)
    {
        yield return observation.PublicTopic?.State ?? CdcKafkaPolicyItemState.Unknown;
        yield return observation.ProgressTopic?.State ?? CdcKafkaPolicyItemState.Unknown;
        yield return observation.SchemaHistoryTopic?.State ?? CdcKafkaPolicyItemState.NotApplicable;
        yield return observation.PublicTopicAcls?.State ?? CdcKafkaPolicyItemState.Unknown;
        yield return observation.ProgressTopicAcls?.State ?? CdcKafkaPolicyItemState.Unknown;
        yield return observation.SchemaHistoryTopicAcls?.State ?? CdcKafkaPolicyItemState.NotApplicable;
        yield return observation.RecordSizePolicy?.State ?? CdcKafkaPolicyItemState.Unknown;
    }

    private static bool HasConnectorConfigurationItem(
        CdcConnectorConfigurationObservation observation,
        CdcConnectorConfigurationItemState state
    ) =>
        observation.TransformState == state
        || observation.ConverterState == state
        || observation.ProducerOverrideState == state
        || observation.HeartbeatState == state
        || observation.SourceIncludeListState == state
        || observation.OffsetState == state
        || observation.SchemaHistoryState == state;

    private static CdcReadiness DetermineReadiness(IReadOnlyList<ComponentSnapshot> components)
    {
        if (components.Any(component => component.State == CdcComponentState.NotSatisfied))
        {
            return CdcReadiness.NotReady;
        }

        if (components.Any(component => component.State == CdcComponentState.Unknown))
        {
            return CdcReadiness.Unknown;
        }

        return CdcReadiness.Ready;
    }

    private static CdcBlockingCategory SelectPrimaryBlockingCategory(
        IReadOnlyList<ComponentSnapshot> components
    )
    {
        CdcBlockingCategory notSatisfiedCategory = BlockingPrecedence.FirstOrDefault(
            category =>
                components.Any(component =>
                    component.State == CdcComponentState.NotSatisfied && component.Category == category
                ),
            CdcBlockingCategory.None
        );
        if (notSatisfiedCategory != CdcBlockingCategory.None)
        {
            return notSatisfiedCategory;
        }

        return BlockingPrecedence.FirstOrDefault(
            category =>
                components.Any(component =>
                    component.State == CdcComponentState.Unknown && component.Category == category
                ),
            CdcBlockingCategory.None
        );
    }

    private static ComponentSnapshot Snapshot(CdcComponent component) =>
        new(component.State, component.Category);

    private static ComponentSnapshot Snapshot(CdcSourceHistoryComponent component) =>
        new(component.State, component.Category);

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

    private static IReadOnlyList<CdcDiagnostic> LimitDiagnostics(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        diagnostics.Take(MaximumDiagnostics).ToArray();

    private sealed record BindingEvaluation(
        CdcComponent Component,
        CdcBinding? Binding,
        CdcIncident? ValidIncident
    );

    private sealed record ComponentSnapshot(CdcComponentState State, CdcBlockingCategory Category);
}
