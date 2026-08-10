// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// One candidate specification shared by every candidate-mode fixture, so a mode comparison varies
/// only the mode.
/// </summary>
internal static class CandidateModeTestSpecs
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "School");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    public static PageDocumentIdQuerySpec CreateSpec(
        PageCandidateMode mode,
        string filterParameterName = "schoolYear",
        PageDocumentIdAuthorizationSpec? authorization = null
    ) =>
        new(
            RootTable: _rootTable,
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("SchoolYear"),
                    QueryComparisonOperator.Equal,
                    filterParameterName
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Mode: mode,
            Authorization: authorization
        );

    public static PageDocumentIdAuthorizationSpec CreateNamespaceAuthorization(SqlDialect dialect) =>
        new(
            Strategies: [],
            NamespaceChecks:
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    _rootTable,
                    _namespaceColumn
                ),
            ],
            NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                dialect,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );
}
