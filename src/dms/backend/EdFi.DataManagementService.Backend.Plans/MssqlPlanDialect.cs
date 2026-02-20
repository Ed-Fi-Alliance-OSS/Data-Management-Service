// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL Server-specific plan/query dialect helpers.
/// </summary>
internal sealed class MssqlPlanDialect : IPlanSqlDialect
{
    /// <summary>
    /// Appends a SQL Server <c>OFFSET</c>/<c>FETCH NEXT</c> paging clause.
    /// </summary>
    /// <remarks>
    /// SQL Server requires an <c>ORDER BY</c> clause when using <c>OFFSET</c>/<c>FETCH</c>.
    /// </remarks>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    public void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer
            .Append("OFFSET ")
            .AppendParameter(offsetParameterName)
            .Append(" ROWS FETCH NEXT ")
            .AppendParameter(limitParameterName)
            .AppendLine(" ROWS ONLY");
    }
}
