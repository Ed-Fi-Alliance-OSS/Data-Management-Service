// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class LocalCdcWorkflowJournalStore
{
    public sealed partial class Session
    {
        internal Task<CdcWorkflowJournal> RecordRetirementIntentAsync(
            CdcBinding binding,
            Guid workflowId,
            Guid operationId,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    var journal = await ReadOwnedAsync(
                        binding.ToTargetIdentity(),
                        workflowId,
                        cancellationToken
                    );
                    Require(!journal.RetirementIntended, CdcWorkflowStateFailure.Contradictory);
                    var operation = new CdcWorkflowOperation(
                        operationId,
                        CdcWorkflowEffect.Retire,
                        _store.Now(),
                        [],
                        []
                    )
                    {
                        Retirement = [new(binding, [])],
                    };
                    var next = journal with { Operations = journal.Operations.Add(operation) };
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );

        /// <summary>Intent before each effect; verified checkpoints are written only after adapter read-back.</summary>
        internal Task<CdcWorkflowJournal> RecordRetirementStepAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            CdcRetirementStepKind kind,
            bool verified,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    var journal = await ReadOwnedAsync(target, workflowId, cancellationToken);
                    int index = FindOperation(journal, operationId);
                    var operation = journal.Operations[index];
                    Require(operation.Effect == CdcWorkflowEffect.Retire && operation.Retirement.Length == 1);
                    var retirement = operation.Retirement.Single();
                    int stepIndex = Array.FindIndex(retirement.Steps.ToArray(), s => s.Kind == kind);
                    if (stepIndex >= 0 && (!verified || !retirement.Steps[stepIndex].VerifiedAt.IsEmpty))
                    {
                        return journal;
                    }
                    Require(operation.Completions.IsEmpty);
                    Require(!verified || stepIndex >= 0);
                    var steps = verified
                        ? retirement.Steps.SetItem(
                            stepIndex,
                            retirement.Steps[stepIndex] with
                            {
                                VerifiedAt = [_store.Now()],
                            }
                        )
                        : retirement.Steps.Add(new(kind, _store.Now(), []));
                    var next = journal with
                    {
                        Operations = journal.Operations.SetItem(
                            index,
                            operation with
                            {
                                Retirement = [retirement with { Steps = steps }],
                            }
                        ),
                    };
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );
    }
}
