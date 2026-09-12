// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcRetirementStepKind
{
    ConnectSourceOffsets,
    KafkaConnectConnector,
    PostgresqlLogicalSlot,
    PostgresqlPublication,
    SqlServerCaptureInstanceDocument,
    SqlServerCaptureInstanceDocumentCache,
    SqlServerCaptureInstanceCdcHeartbeat,
    SqlServerCdcGatingRole,
    SqlServerJobs,
    PublicTopicAcls,
    ProgressTopicAcls,
    SchemaHistoryTopicAcls,
    PublicTopic,
    ProgressTopic,
    SchemaHistoryTopic,
    DeleteState,
}

/// <summary>Historical reconciliation, never a substitute for a fresh artifact inspection.</summary>
public sealed record CdcRetirementStep(
    CdcRetirementStepKind Kind,
    DateTimeOffset IntendedAt,
    ImmutableArray<DateTimeOffset> VerifiedAt
);

/// <summary>
/// Exact cleanup scope retained under the controller lock. For reserved workflows only a durable
/// DeleteState intent permits finishing deletion without the original binding file. Never-reserved
/// workflows retain their proposed scope and revalidate internal-only history and binding absence.
/// No credentials, offsets or raw source identity are stored here.
/// </summary>
public sealed record CdcRetirementJournal(CdcBinding Binding, ImmutableArray<CdcRetirementStep> Steps);

internal static partial class CdcWorkflowJournalValidation
{
    internal static CdcRetirementStepKind[] RetirementSteps(CdcProvider provider) =>
        [
            CdcRetirementStepKind.ConnectSourceOffsets,
            CdcRetirementStepKind.KafkaConnectConnector,
            .. provider == CdcProvider.Postgresql
                ? new[]
                {
                    CdcRetirementStepKind.PostgresqlLogicalSlot,
                    CdcRetirementStepKind.PostgresqlPublication,
                }
                : new[]
                {
                    CdcRetirementStepKind.SqlServerCaptureInstanceDocument,
                    CdcRetirementStepKind.SqlServerCaptureInstanceDocumentCache,
                    CdcRetirementStepKind.SqlServerCaptureInstanceCdcHeartbeat,
                    CdcRetirementStepKind.SqlServerCdcGatingRole,
                    CdcRetirementStepKind.SqlServerJobs,
                },
            CdcRetirementStepKind.PublicTopicAcls,
            CdcRetirementStepKind.ProgressTopicAcls,
            .. provider == CdcProvider.SqlServer
                ? new[] { CdcRetirementStepKind.SchemaHistoryTopicAcls }
                : [],
            CdcRetirementStepKind.PublicTopic,
            CdcRetirementStepKind.ProgressTopic,
            .. provider == CdcProvider.SqlServer ? new[] { CdcRetirementStepKind.SchemaHistoryTopic } : [],
            CdcRetirementStepKind.DeleteState,
        ];

    private static void ValidateRetirement(
        CdcWorkflowOperation operation,
        CdcWorkflowJournal journal,
        DateTimeOffset now
    )
    {
        Require(!operation.Retirement.IsDefault);
        Require(
            operation.Effect == CdcWorkflowEffect.Retire
                ? operation.Retirement.Length <= 1
                : operation.Retirement.IsEmpty
        );
        // Legacy generic retirement records cannot authorize resumable deletion.
        if (operation.Retirement.IsEmpty)
        {
            return;
        }
        var retirement = operation.Retirement.Single();
        Require(retirement is not null && retirement.Binding is not null);
        Require(CdcBindingValidator.Validate(retirement.Binding).Succeeded);
        Require(retirement.Binding.ToTargetIdentity() == journal.Target);
        Require(
            journal
                .Operations.SelectMany(o => o.Completions)
                .Any(c =>
                    c.Evidence is CdcWorkflowCompletion.Source source
                    && source.PhysicalSourceFingerprint == retirement.Binding.PhysicalSourceFingerprint
                )
        );
        Require(!retirement.Steps.IsDefault);
        var expected = RetirementSteps(journal.Target.Provider);
        Require(retirement.Steps.Length <= expected.Length);
        var previous = operation.IntendedAt;
        for (int i = 0; i < retirement.Steps.Length; i++)
        {
            var step = retirement.Steps[i];
            Require(step is not null && step.Kind == expected[i]);
            Require(
                step.IntendedAt >= previous
                    && step.IntendedAt <= now
                    && step.IntendedAt.Offset == TimeSpan.Zero
            );
            Require(!step.VerifiedAt.IsDefault && step.VerifiedAt.Length <= 1);
            Require(i == retirement.Steps.Length - 1 || step.VerifiedAt.Length == 1);
            foreach (var verified in step.VerifiedAt)
            {
                Require(verified >= step.IntendedAt && verified <= now && verified.Offset == TimeSpan.Zero);
            }
            previous = step.VerifiedAt.IsEmpty ? step.IntendedAt : step.VerifiedAt.Single();
        }
        if (!operation.Completions.IsEmpty)
        {
            Require(
                retirement.Steps.Length == expected.Length
                    && retirement.Steps.All(s => s.VerifiedAt.Length == 1)
            );
            Require(operation.Completions.Single().ReconciledAt >= previous);
        }
    }
}
