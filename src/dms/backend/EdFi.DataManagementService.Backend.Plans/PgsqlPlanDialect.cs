// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// PostgreSQL-specific plan/query dialect helpers.
/// </summary>
internal sealed class PgsqlPlanDialect : IPlanSqlDialect
{
    /// <summary>
    /// Appends a PostgreSQL <c>LIMIT</c>/<c>OFFSET</c> paging clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    public void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer
            .Append("LIMIT ")
            .AppendParameter(limitParameterName)
            .Append(" OFFSET ")
            .AppendParameter(offsetParameterName)
            .AppendLine();
    }
}
