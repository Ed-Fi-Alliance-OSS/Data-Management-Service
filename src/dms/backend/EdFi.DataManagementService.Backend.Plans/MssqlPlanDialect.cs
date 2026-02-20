// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed class MssqlPlanDialect : IPlanSqlDialect
{
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
