// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record ProposedRelationshipAuthorizationRuntimeCheck(
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index,
    IReadOnlyList<ProposedRelationshipAuthorizationRuntimeStrategy> Strategies
);

internal sealed record ProposedRelationshipAuthorizationRuntimeStrategy(
    int StrategyOrdinal,
    RelationshipAuthorizationCheckSpec CheckSpec,
    IReadOnlyList<ProposedRelationshipAuthorizationRuntimeSubject> Subjects
);

internal sealed record ProposedRelationshipAuthorizationRuntimeSubject(
    int SubjectOrdinal,
    RelationshipAuthorizationSubject Subject,
    RelationshipAuthorizationProposedValueBinding Binding,
    object? Value
);

internal abstract record ProposedRelationshipAuthorizationExtractionResult
{
    private ProposedRelationshipAuthorizationExtractionResult() { }

    public sealed record Ready(ProposedRelationshipAuthorizationRuntimeCheck RuntimeCheck)
        : ProposedRelationshipAuthorizationExtractionResult;

    public sealed record InvalidAuthorizationPlan(string FailureMessage)
        : ProposedRelationshipAuthorizationExtractionResult;
}

internal static class RelationshipAuthorizationProposedValueExtractor
{
    public static ProposedRelationshipAuthorizationExtractionResult Extract(
        RelationshipAuthorizationResult.Authorized authorized,
        RootWriteRowBuffer rootRow,
        int emittedAuth1Index
    )
    {
        ArgumentNullException.ThrowIfNull(authorized);
        ArgumentNullException.ThrowIfNull(rootRow);
        ArgumentOutOfRangeException.ThrowIfNegative(emittedAuth1Index);

        if (authorized.CheckSpecs.Count == 0)
        {
            return Invalid("Proposed relationship authorization requires at least one check spec.");
        }

        var claimParameterization = authorized.ClaimEducationOrganizationIdParameterization;

        if (claimParameterization is null)
        {
            return Invalid(
                "Proposed relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
            );
        }

        List<ProposedRelationshipAuthorizationRuntimeStrategy> runtimeStrategies = [];
        var rootTable = rootRow.TableWritePlan.TableModel.Table;

        for (var strategyOrdinal = 0; strategyOrdinal < authorized.CheckSpecs.Count; strategyOrdinal++)
        {
            var checkSpec = authorized.CheckSpecs[strategyOrdinal];

            if (checkSpec.ValueSource is not RelationshipAuthorizationValueSource.Proposed)
            {
                return Invalid(
                    $"Proposed relationship authorization cannot extract check spec '{strategyOrdinal}' because it uses value source '{checkSpec.ValueSource}'."
                );
            }

            if (checkSpec.CheckTarget is not RelationshipAuthorizationCheckTarget.Proposed proposedTarget)
            {
                return Invalid(
                    $"Proposed relationship authorization check spec '{strategyOrdinal}' does not use a proposed-value target."
                );
            }

            if (!proposedTarget.RootTable.Equals(rootTable))
            {
                return Invalid(
                    $"Proposed relationship authorization check spec '{strategyOrdinal}' targets root table '{proposedTarget.RootTable}', but the finalized root row is for '{rootTable}'."
                );
            }

            if (proposedTarget.SubjectBindingsInOrder.Count != checkSpec.Subjects.Count)
            {
                return Invalid(
                    $"Proposed relationship authorization check spec '{strategyOrdinal}' has {checkSpec.Subjects.Count} subjects but {proposedTarget.SubjectBindingsInOrder.Count} root bindings."
                );
            }

            List<ProposedRelationshipAuthorizationRuntimeSubject> runtimeSubjects = [];

            for (var subjectOrdinal = 0; subjectOrdinal < checkSpec.Subjects.Count; subjectOrdinal++)
            {
                var subject = checkSpec.Subjects[subjectOrdinal];
                var binding = proposedTarget.SubjectBindingsInOrder[subjectOrdinal];

                if (!binding.Table.Equals(rootTable))
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets table '{binding.Table}', but the finalized root row is for '{rootTable}'."
                    );
                }

                if (!binding.Column.Equals(subject.Column))
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets column '{binding.Column.Value}', but the subject targets column '{subject.Column.Value}'."
                    );
                }

                if (binding.BindingIndex < 0 || binding.BindingIndex >= rootRow.Values.Length)
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' uses root binding index {binding.BindingIndex}, but the finalized root row has {rootRow.Values.Length} values."
                    );
                }

                var value = GetBoundSqlValue(rootRow.Values[binding.BindingIndex]);

                runtimeSubjects.Add(
                    new ProposedRelationshipAuthorizationRuntimeSubject(
                        subjectOrdinal,
                        subject,
                        binding,
                        value
                    )
                );
            }

            runtimeStrategies.Add(
                new ProposedRelationshipAuthorizationRuntimeStrategy(
                    strategyOrdinal,
                    checkSpec,
                    runtimeSubjects
                )
            );
        }

        return new ProposedRelationshipAuthorizationExtractionResult.Ready(
            new ProposedRelationshipAuthorizationRuntimeCheck(
                authorized.CheckSpecs,
                claimParameterization,
                emittedAuth1Index,
                runtimeStrategies
            )
        );
    }

    private static object? GetBoundSqlValue(FlattenedWriteValue value)
    {
        if (value is FlattenedWriteValue.Literal { Value: { } literalValue } && literalValue is not DBNull)
        {
            return literalValue;
        }

        return null;
    }

    private static ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan Invalid(
        string message
    ) => new(message);
}
