// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Rejects a SQL Server string query filter that its target column cannot hold.
/// </summary>
/// <remarks>
/// Core validates a filter against the field's declared <c>maxLength</c>, counted in text elements. A SQL
/// Server <c>nvarchar(n)</c> column counts UTF-16 code units, so a value can pass that validation and still
/// be wider than the column: three supplementary characters are three text elements but six code units.
/// Such a filter is rejected as an invalid filter value, with the message Core uses, before any command is
/// planned, so it can never be bound into a parameter sized to the column. The rule is SQL Server-specific;
/// PostgreSQL filter validation and binding are unchanged.
/// </remarks>
internal static class QueryFilterStorageWidth
{
    /// <summary>
    /// Whether <paramref name="value"/> is wider than a SQL Server string column of
    /// <paramref name="columnLength"/>. Always false for PostgreSQL and for an unbounded column.
    /// </summary>
    public static bool ExceedsSqlServerColumn(SqlDialect dialect, string value, int? columnLength) =>
        dialect == SqlDialect.Mssql
        && columnLength is { } length
        && NaturalKeyValueConverter.StoredLength(dialect, value) > length;

    /// <summary>
    /// The invalid-filter failure for <paramref name="queryElement"/>, keyed and worded as Core keys and
    /// words an invalid filter value.
    /// </summary>
    public static WriteValidationFailure InvalidValueFailure(QueryElement queryElement) =>
        new(
            queryElement.DocumentPaths[0],
            $"The value '{queryElement.Value}' is not valid for {queryElement.QueryFieldName}."
        );
}
