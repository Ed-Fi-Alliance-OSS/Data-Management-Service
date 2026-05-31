// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class RelationshipAuthorizationPeoplePathValidation
{
    public static void ValidateStoredAnchorRootTable(
        DbTableName rootTable,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        string rootTableDescription,
        string? parameterName = null
    )
    {
        if (!personMetadata.StoredAnchor.RootTable.Equals(rootTable))
        {
            var message =
                $"People authorization subject root table '{personMetadata.StoredAnchor.RootTable}' does not match {rootTableDescription} '{rootTable}'.";

            if (parameterName is not null)
            {
                throw new ArgumentException(message, parameterName);
            }

            throw new InvalidOperationException(message);
        }
    }

    public static void ValidateSelfRootDocumentIdPath(
        DbTableName subjectTable,
        DbColumnName subjectColumn,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata
    )
    {
        if (
            !subjectTable.Equals(personMetadata.StoredAnchor.RootTable)
            || !subjectColumn.Equals(personMetadata.StoredAnchor.RootDocumentIdColumn)
        )
        {
            throw new InvalidOperationException(
                $"Self People authorization subject column '{subjectTable}.{subjectColumn}' does not match root DocumentId column '{personMetadata.StoredAnchor.RootTable}.{personMetadata.StoredAnchor.RootDocumentIdColumn}'."
            );
        }
    }

    public static DbColumnName GetDirectRootPersonDocumentIdColumn(
        DbTableName rootTable,
        DbTableName subjectTable,
        DbColumnName subjectColumn,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        string rootTableDescription
    )
    {
        var step = personMetadata.Path.Steps[0];

        if (!step.SourceTable.Equals(rootTable))
        {
            throw new InvalidOperationException(
                $"Direct People authorization subject table '{step.SourceTable}' does not match {rootTableDescription} '{rootTable}'."
            );
        }

        if (!subjectTable.Equals(step.SourceTable) || !subjectColumn.Equals(step.SourceColumnName))
        {
            throw new InvalidOperationException(
                $"People authorization subject column '{subjectTable}.{subjectColumn}' does not match path root column '{step.SourceTable}.{step.SourceColumnName}'."
            );
        }

        return step.SourceColumnName;
    }

    public static void ValidateTransitivePersonPath(
        DbTableName rootTable,
        DbTableName subjectTable,
        DbColumnName subjectColumn,
        IReadOnlyList<ColumnPathStep> pathSteps
    )
    {
        var expectedSourceTable = rootTable;

        for (var stepIndex = 0; stepIndex < pathSteps.Count - 1; stepIndex++)
        {
            var step = pathSteps[stepIndex];

            if (!step.SourceTable.Equals(expectedSourceTable))
            {
                throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex} source table '{step.SourceTable}' does not match expected table '{expectedSourceTable}'."
                );
            }

            var targetTable =
                step.TargetTable
                ?? throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex} is missing a target table."
                );

            if (!pathSteps[stepIndex + 1].SourceTable.Equals(targetTable))
            {
                throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex + 1} source table '{pathSteps[stepIndex + 1].SourceTable}' does not match previous target table '{targetTable}'."
                );
            }

            expectedSourceTable = targetTable;
        }

        var terminalStep = pathSteps[^1];

        if (!terminalStep.SourceTable.Equals(expectedSourceTable))
        {
            throw new InvalidOperationException(
                $"Transitive People authorization terminal source table '{terminalStep.SourceTable}' does not match expected table '{expectedSourceTable}'."
            );
        }

        if (
            !subjectTable.Equals(terminalStep.SourceTable)
            || !subjectColumn.Equals(terminalStep.SourceColumnName)
        )
        {
            throw new InvalidOperationException(
                $"People authorization subject column '{subjectTable}.{subjectColumn}' does not match transitive terminal path column '{terminalStep.SourceTable}.{terminalStep.SourceColumnName}'."
            );
        }
    }
}
