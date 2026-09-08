// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>In-memory evidence collected in one locked pass; never persisted or reused by watch.</summary>
internal sealed class CdcEstablishedStatusProgress(CdcTargetStatusEvaluationInput input)
{
    internal CdcRecoveryObservation Recovery { get; set; } = new(CdcRecoveryBoundary.Unobserved, false);
    internal CdcTargetStatusEvaluationInput Input { get; private set; } = input;
    internal CdcSourceHistoryClassificationResult SourceHistory { get; private set; } = null!;
    internal bool HasPendingRecordSizeIncrease { get; private set; }
    internal CdcTargetStatus CompletedStatus { get; private set; } = null!;

    // Called only next to ReadForEvaluation while its telemetry pass is still alive. The controller
    // may display this historical status but cannot evaluate the retained lag snapshot again.
    internal void Complete() => CompletedStatus = CdcTargetStatusEvaluator.EvaluatePostAdmission(Input);

    internal Func<Task> ContainTerminal { get; set; } = () => Task.CompletedTask;

    internal void Capture(
        CdcTargetStatusEvaluationInput input,
        bool pending,
        CdcSourceHistoryClassificationResult history = null!
    )
    {
        // Even a later unavailable observation in this pass cannot conceal terminal evidence.
        if (SourceHistory?.Observation.Continuity == CdcSourceHistoryContinuity.Lost)
        {
            input = input with
            {
                SourceHistory = SourceHistory.Observation with { OperationId = input.OperationId },
            };
        }
        else if (history is not null)
        {
            SourceHistory = history;
        }
        if (Input.BindingState is { State: CdcBindingState.IncidentLatched } retained)
        {
            input = input with { BindingState = retained };
        }
        Input = input;
        HasPendingRecordSizeIncrease |= pending;
    }
}
