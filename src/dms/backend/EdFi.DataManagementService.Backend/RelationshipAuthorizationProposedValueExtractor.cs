// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal sealed record ProposedRelationshipAuthorizationRuntimeCheck(
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index,
    IReadOnlyList<ProposedRelationshipAuthorizationRuntimeStrategy> Strategies,
    RelationshipAuthorizationExecutableShape? ExecutableShape = null
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
    ProposedRelationshipAuthorizationRuntimeValue RuntimeValue
);

internal abstract record ProposedRelationshipAuthorizationRuntimeValue
{
    private ProposedRelationshipAuthorizationRuntimeValue() { }

    public sealed record SubjectValue(object? Value) : ProposedRelationshipAuthorizationRuntimeValue;

    // This is an anchor into a People path, not the terminal person DocumentId consumed by auth views.
    public sealed record TransitivePeopleFirstHopAnchorValue(object? Value)
        : ProposedRelationshipAuthorizationRuntimeValue;
}

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
    ) => Extract(authorized, rootRow, emittedAuth1Index, targetContext: null);

    public static ProposedRelationshipAuthorizationExtractionResult Extract(
        RelationshipAuthorizationResult.Authorized authorized,
        RootWriteRowBuffer rootRow,
        int emittedAuth1Index,
        RelationalWriteTargetContext? targetContext
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
                var bindingTarget = GetExpectedBindingTarget(subject, strategyOrdinal, subjectOrdinal);

                if (bindingTarget is ProposedBindingTarget.Invalid invalidBindingTarget)
                {
                    return Invalid(invalidBindingTarget.FailureMessage);
                }

                var expectedBindingTarget = (ProposedBindingTarget.Ready)bindingTarget;

                if (!binding.Table.Equals(rootTable))
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets table '{binding.Table}', but the finalized root row is for '{rootTable}'."
                    );
                }

                if (!binding.Table.Equals(expectedBindingTarget.Table))
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets table '{binding.Table}', but the {expectedBindingTarget.Description} targets table '{expectedBindingTarget.Table}'."
                    );
                }

                if (!binding.Column.Equals(expectedBindingTarget.Column))
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets column '{binding.Column.Value}', but the {expectedBindingTarget.Description} targets column '{expectedBindingTarget.Column.Value}'."
                    );
                }

                if (binding.BindingIndex < 0 || binding.BindingIndex >= rootRow.Values.Length)
                {
                    return Invalid(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' uses root binding index {binding.BindingIndex}, but the finalized root row has {rootRow.Values.Length} values."
                    );
                }

                var valueResult = GetRuntimeValue(
                    rootRow.Values[binding.BindingIndex],
                    subject,
                    targetContext,
                    strategyOrdinal,
                    subjectOrdinal
                );

                if (valueResult is ProposedRuntimeValue.Invalid invalidValue)
                {
                    return Invalid(invalidValue.FailureMessage);
                }

                var runtimeValue = ((ProposedRuntimeValue.Ready)valueResult).RuntimeValue;

                runtimeSubjects.Add(
                    new ProposedRelationshipAuthorizationRuntimeSubject(
                        subjectOrdinal,
                        subject,
                        binding,
                        runtimeValue
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
                runtimeStrategies,
                authorized.ExecutableShape
            )
        );
    }

    private static ProposedRuntimeValue GetRuntimeValue(
        FlattenedWriteValue boundValue,
        RelationshipAuthorizationSubject subject,
        RelationalWriteTargetContext? targetContext,
        int strategyOrdinal,
        int subjectOrdinal
    )
    {
        var personMetadata = subject.PersonMetadata;

        if (
            personMetadata?.ProposedAnchor?.Kind
            is RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId
        )
        {
            return targetContext switch
            {
                RelationalWriteTargetContext.ExistingDocument existingDocument =>
                    new ProposedRuntimeValue.Ready(
                        new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(
                            existingDocument.DocumentId
                        )
                    ),
                RelationalWriteTargetContext.CreateNew => new ProposedRuntimeValue.Invalid(
                    $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' requires an existing target DocumentId, but the write targets a new document."
                ),
                null => new ProposedRuntimeValue.Invalid(
                    $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' requires an existing target DocumentId, but no target context was provided."
                ),
                _ => new ProposedRuntimeValue.Invalid(
                    $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' requires an existing target DocumentId, but target context '{targetContext.GetType().Name}' is unsupported."
                ),
            };
        }

        var value = GetBoundSqlValue(boundValue);

        if (
            personMetadata is
            {
                Path.Kind: RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
                ProposedAnchor.Kind: RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
            }
        )
        {
            return new ProposedRuntimeValue.Ready(
                new ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue(value)
            );
        }

        return new ProposedRuntimeValue.Ready(
            new ProposedRelationshipAuthorizationRuntimeValue.SubjectValue(value)
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

    private static ProposedBindingTarget GetExpectedBindingTarget(
        RelationshipAuthorizationSubject subject,
        int strategyOrdinal,
        int subjectOrdinal
    )
    {
        var personMetadata = subject.PersonMetadata;

        if (
            personMetadata?.Path.Kind is not RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath
        )
        {
            if (
                personMetadata?.ProposedAnchor?.Kind
                is RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId
            )
            {
                return new ProposedBindingTarget.Ready(
                    personMetadata.ProposedAnchor.Binding.Table,
                    personMetadata.ProposedAnchor.Binding.Column,
                    "existing-resource self People proposed anchor"
                );
            }

            return new ProposedBindingTarget.Ready(subject.Table, subject.Column, "subject");
        }

        if (personMetadata.ProposedAnchor is not { } proposedAnchor)
        {
            return new ProposedBindingTarget.Invalid(
                $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' requires proposed anchor metadata for transitive People subject column '{subject.Column.Value}'."
            );
        }

        if (proposedAnchor.Kind is not RelationshipAuthorizationPersonProposedAnchorKind.FirstHop)
        {
            return new ProposedBindingTarget.Invalid(
                $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' requires a first-hop proposed anchor for transitive People subject column '{subject.Column.Value}', but found '{proposedAnchor.Kind}'."
            );
        }

        return new ProposedBindingTarget.Ready(
            proposedAnchor.Binding.Table,
            proposedAnchor.Binding.Column,
            "transitive People proposed anchor"
        );
    }

    private static ProposedRelationshipAuthorizationExtractionResult.InvalidAuthorizationPlan Invalid(
        string message
    ) => new(message);

    private abstract record ProposedBindingTarget
    {
        private ProposedBindingTarget() { }

        public sealed record Ready(DbTableName Table, DbColumnName Column, string Description)
            : ProposedBindingTarget;

        public sealed record Invalid(string FailureMessage) : ProposedBindingTarget;
    }

    private abstract record ProposedRuntimeValue
    {
        private ProposedRuntimeValue() { }

        public sealed record Ready(ProposedRelationshipAuthorizationRuntimeValue RuntimeValue)
            : ProposedRuntimeValue;

        public sealed record Invalid(string FailureMessage) : ProposedRuntimeValue;
    }
}
