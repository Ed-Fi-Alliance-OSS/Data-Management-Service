// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates dialect-specific single-document hydration SQL population on relational read plans.
/// </summary>
internal static class ReadPlanSingleDocumentSqlValidator
{
    /// <summary>
    /// Validates single-document SQL fields or throws the exception created by <paramref name="createException" />.
    /// </summary>
    public static void ValidateOrThrow(
        ResourceReadPlan readPlan,
        SqlDialect dialect,
        Func<string, Exception> createException
    )
    {
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(createException);

        if (readPlan.Model.StorageKind != ResourceStorageKind.RelationalTables)
        {
            return;
        }

        switch (dialect)
        {
            case SqlDialect.Pgsql:
                ValidatePgsqlSingleDocumentSql(readPlan, createException);
                return;

            case SqlDialect.Mssql:
                ValidateMssqlSingleDocumentSql(readPlan, createException);
                return;

            default:
                throw createException($"unsupported SQL dialect '{dialect}'");
        }
    }

    private static void ValidatePgsqlSingleDocumentSql(
        ResourceReadPlan readPlan,
        Func<string, Exception> createException
    )
    {
        for (
            var tablePlanIndex = 0;
            tablePlanIndex < readPlan.TablePlansInDependencyOrder.Length;
            tablePlanIndex++
        )
        {
            var tablePlan = readPlan.TablePlansInDependencyOrder[tablePlanIndex];

            ThrowIfMissingSql(
                tablePlan.SelectBySingleDocumentSql,
                $"table read plan at index '{tablePlanIndex}' for table '{tablePlan.TableModel.Table}'",
                createException
            );
        }

        for (
            var descriptorPlanIndex = 0;
            descriptorPlanIndex < readPlan.DescriptorProjectionPlansInOrder.Length;
            descriptorPlanIndex++
        )
        {
            ThrowIfMissingSql(
                readPlan.DescriptorProjectionPlansInOrder[descriptorPlanIndex].SelectBySingleDocumentSql,
                $"descriptor projection plan at index '{descriptorPlanIndex}'",
                createException
            );
        }

        if (readPlan.DocumentReferenceLookup is { } lookup)
        {
            ThrowIfMissingSql(
                lookup.SelectBySingleDocumentSql,
                "document-reference lookup plan",
                createException
            );
        }
    }

    private static void ValidateMssqlSingleDocumentSql(
        ResourceReadPlan readPlan,
        Func<string, Exception> createException
    )
    {
        for (
            var tablePlanIndex = 0;
            tablePlanIndex < readPlan.TablePlansInDependencyOrder.Length;
            tablePlanIndex++
        )
        {
            var tablePlan = readPlan.TablePlansInDependencyOrder[tablePlanIndex];

            ThrowIfUnexpectedSql(
                tablePlan.SelectBySingleDocumentSql,
                $"table read plan at index '{tablePlanIndex}' for table '{tablePlan.TableModel.Table}'",
                createException
            );
        }

        for (
            var descriptorPlanIndex = 0;
            descriptorPlanIndex < readPlan.DescriptorProjectionPlansInOrder.Length;
            descriptorPlanIndex++
        )
        {
            ThrowIfUnexpectedSql(
                readPlan.DescriptorProjectionPlansInOrder[descriptorPlanIndex].SelectBySingleDocumentSql,
                $"descriptor projection plan at index '{descriptorPlanIndex}'",
                createException
            );
        }

        if (readPlan.DocumentReferenceLookup is { } lookup)
        {
            ThrowIfUnexpectedSql(
                lookup.SelectBySingleDocumentSql,
                "document-reference lookup plan",
                createException
            );
        }
    }

    private static void ThrowIfMissingSql(
        string? selectBySingleDocumentSql,
        string planDescription,
        Func<string, Exception> createException
    )
    {
        if (!string.IsNullOrWhiteSpace(selectBySingleDocumentSql))
        {
            return;
        }

        throw createException(
            $"{planDescription} is missing required SelectBySingleDocumentSql for PostgreSQL read plans"
        );
    }

    private static void ThrowIfUnexpectedSql(
        string? selectBySingleDocumentSql,
        string planDescription,
        Func<string, Exception> createException
    )
    {
        if (selectBySingleDocumentSql is null)
        {
            return;
        }

        throw createException(
            $"{planDescription} has unexpected SelectBySingleDocumentSql for SQL Server read plans"
        );
    }
}
