// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class PlanSqlGoldenFixtureQueryPlans
{
    public static PageDocumentIdSqlPlan CompileFoundationsPageDocumentIdPlan(SqlDialect dialect)
    {
        return Compile(dialect, CreateFoundationsQuerySpec());
    }

    public static PageDocumentIdSqlPlan CompileContractsManifestPageDocumentIdPlan(SqlDialect dialect)
    {
        return Compile(dialect, CreateContractsManifestQuerySpec());
    }

    public static PageDocumentIdSqlPlan CompileDescriptorPageDocumentIdPlan(SqlDialect dialect)
    {
        return Compile(dialect, CreateDescriptorQuerySpec());
    }

    private static PageDocumentIdSqlPlan Compile(SqlDialect dialect, PageDocumentIdQuerySpec querySpec)
    {
        return new PageDocumentIdSqlCompiler(dialect).Compile(querySpec);
    }

    private static PageDocumentIdQuerySpec CreateFoundationsQuerySpec()
    {
        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("SchoolYear"),
                    QueryComparisonOperator.GreaterThanOrEqual,
                    "schoolYear"
                ),
                new QueryValuePredicate(
                    new DbColumnName("Student_StudentUniqueId"),
                    QueryComparisonOperator.Equal,
                    "studentUniqueId",
                    ScalarKind.String
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>
            {
                [new DbColumnName("Student_StudentUniqueId")] = new ColumnStorage.UnifiedAlias(
                    new DbColumnName("StudentUniqueId_Unified"),
                    new DbColumnName("Student_DocumentId")
                ),
            }
        );
    }

    private static PageDocumentIdQuerySpec CreateContractsManifestQuerySpec()
    {
        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("AliasB"),
                    QueryComparisonOperator.GreaterThan,
                    "zParam"
                ),
                new QueryValuePredicate(new DbColumnName("AliasA"), QueryComparisonOperator.Equal, "aParam"),
                new QueryValuePredicate(
                    new DbColumnName("AliasC"),
                    QueryComparisonOperator.LessThan,
                    "mParam"
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>
            {
                [new DbColumnName("AliasA")] = new ColumnStorage.UnifiedAlias(
                    new DbColumnName("CanonicalA"),
                    new DbColumnName("PresenceA")
                ),
                [new DbColumnName("AliasB")] = new ColumnStorage.UnifiedAlias(
                    new DbColumnName("CanonicalB"),
                    null
                ),
            },
            IncludeTotalCountSql: true
        );
    }

    private static PageDocumentIdQuerySpec CreateDescriptorQuerySpec()
    {
        return new PageDocumentIdQuerySpec(
            RootTable: new DbTableName(new DbSchemaName("dms"), "Document"),
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
                new QueryValuePredicate(
                    new DbColumnName("DocumentUuid"),
                    QueryComparisonOperator.Equal,
                    "id"
                ),
                new QueryValuePredicate(
                    new QueryPredicateTarget.DescriptorColumn(new DbColumnName("Namespace")),
                    QueryComparisonOperator.Equal,
                    "namespace",
                    ScalarKind.String
                ),
                new QueryValuePredicate(
                    new QueryPredicateTarget.DescriptorColumn(new DbColumnName("EffectiveEndDate")),
                    QueryComparisonOperator.Equal,
                    "effectiveEndDate",
                    ScalarKind.Date
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>()
        );
    }
}
