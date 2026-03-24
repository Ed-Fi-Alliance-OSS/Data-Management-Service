// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Dialect-specific plan/query SQL emission helpers.
/// </summary>
internal interface IPlanSqlDialect
{
    /// <summary>
    /// Appends a dialect-specific paging clause.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="offsetParameterName">The bare offset parameter name.</param>
    /// <param name="limitParameterName">The bare limit parameter name.</param>
    void AppendPagingClause(SqlWriter writer, string offsetParameterName, string limitParameterName);

    /// <summary>
    /// Appends a dialect-specific <c>CREATE TEMP TABLE</c> DDL statement for the keyset table.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    void AppendCreateKeysetTempTable(SqlWriter writer, KeysetTableContract keyset);

    /// <summary>
    /// Appends a <c>SELECT</c> statement that joins <c>dms.Document</c> metadata to the
    /// materialized keyset table, returning document metadata columns for the page.
    /// </summary>
    /// <param name="writer">The SQL writer to append to.</param>
    /// <param name="keyset">The keyset table contract specifying table and column names.</param>
    void AppendDocumentMetadataSelect(SqlWriter writer, KeysetTableContract keyset);
}
