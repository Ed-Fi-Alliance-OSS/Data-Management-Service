// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// PostgreSQL-specific plan/query dialect helpers.
/// </summary>
internal sealed class PgsqlPlanDialect : IPlanSqlDialect
{
    private static readonly DbTableName DocumentTable = new(new DbSchemaName("dms"), "Document");

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

    /// <inheritdoc />
    public void AppendCreateKeysetTempTable(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer
            .Append("CREATE TEMP TABLE ")
            .AppendRelation(keyset.Table)
            .Append(" (")
            .AppendQuoted(keyset.DocumentIdColumnName.Value)
            .AppendLine(" bigint PRIMARY KEY) ON COMMIT DROP;");
    }

    /// <inheritdoc />
    public void AppendDocumentMetadataSelect(SqlWriter writer, KeysetTableContract keyset)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(keyset);

        writer
            .AppendLine("SELECT")
            .Append("    d.")
            .Append(writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value))
            .AppendLine(",")
            .AppendLine("    d.\"DocumentUuid\",")
            .AppendLine("    d.\"ContentVersion\",")
            .AppendLine("    d.\"IdentityVersion\",")
            .AppendLine("    d.\"ContentLastModifiedAt\",")
            .AppendLine("    d.\"IdentityLastModifiedAt\"")
            .Append("FROM ")
            .AppendTable(DocumentTable)
            .AppendLine(" d")
            .Append("INNER JOIN ")
            .AppendRelation(keyset.Table)
            .Append(" k ON d.")
            .Append(writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value))
            .Append(" = k.")
            .Append(writer.Dialect.QuoteIdentifier(keyset.DocumentIdColumnName.Value))
            .AppendLine(";");
    }
}
