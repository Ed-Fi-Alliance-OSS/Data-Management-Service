// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class LocalCdcWorkflowJournalStore
{
    public sealed partial class Session
    {
        /// <summary>
        /// The increase controller supplies the exact immutable binding and operational ceilings
        /// after validating provenance and live eligibility. This gate owns only operator confirmation
        /// and durable acknowledgement, never binding adoption, infrastructure mutation or readiness.
        /// </summary>
        public CdcRecordSizeAcknowledgementInvocation BeginRecordSizeAcknowledgement(
            Guid workflowId,
            CdcRecordSizeIncreaseScope scope
        )
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(this, workflowId, scope, _store._time);
        }

        internal Task<CdcWorkflowJournal> RecordConfirmedIncreaseAsync(
            Guid workflowId,
            CdcRecordSizeIncreaseScope scope,
            CdcRecordSizeAcknowledgement acknowledgement,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    var journal = await ReadOwnedAsync(
                        scope.BindingIdentity.ToTargetIdentity(),
                        workflowId,
                        cancellationToken
                    );
                    int index = Array.FindIndex(
                        journal.Operations.ToArray(),
                        operation => operation.OperationId == scope.OperationId
                    );
                    CdcWorkflowJournal next;
                    if (index < 0)
                    {
                        // Intent and the first confirmation are one atomic, durable journal replacement.
                        next = journal with
                        {
                            Operations = journal.Operations.Add(
                                new(
                                    scope.OperationId,
                                    CdcWorkflowEffect.IncreaseRecordSize,
                                    _store.Now(),
                                    [
                                        new(
                                            scope.BindingIdentity,
                                            scope.PreviousMaxRecordBytes,
                                            scope.RequestedMaxRecordBytes,
                                            [acknowledgement]
                                        ),
                                    ],
                                    []
                                )
                            ),
                        };
                    }
                    else
                    {
                        var operation = journal.Operations[index];
                        Require(
                            operation.Effect == CdcWorkflowEffect.IncreaseRecordSize
                                && operation.Completions.IsEmpty,
                            CdcWorkflowStateFailure.Contradictory
                        );
                        var increase = operation.RecordSizeIncrease.Single();
                        Require(
                            increase.BindingIdentity == scope.BindingIdentity
                                && increase.PreviousMaxRecordBytes == scope.PreviousMaxRecordBytes
                                && increase.RequestedMaxRecordBytes == scope.RequestedMaxRecordBytes,
                            CdcWorkflowStateFailure.Contradictory
                        );
                        next = journal with
                        {
                            Operations = journal.Operations.SetItem(
                                index,
                                operation with
                                {
                                    RecordSizeIncrease =
                                    [
                                        increase with
                                        {
                                            Acknowledgements = increase.Acknowledgements.Add(acknowledgement),
                                        },
                                    ],
                                }
                            ),
                        };
                    }
                    // Preserve the same irreversible exposure ordering as other downstream intents.
                    Validate(next, _store.Now());
                    if (index < 0)
                    {
                        await _store.AdvanceSourceHistoryAsync(
                            journal,
                            DocumentCacheDownstreamPublicationStatus.Possible,
                            cancellationToken
                        );
                    }
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );
    }
}
