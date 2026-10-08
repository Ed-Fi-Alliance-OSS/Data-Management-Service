// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>Reads canonical namespaces from compact descriptor references without parsing URI text.</summary>
internal static class DescriptorNamespaceSqlHelper
{
    public const string Alias = "dn";
    public static readonly DbTableName Table = new(new DbSchemaName("dms"), "Descriptor");
    public static readonly DbColumnName NamespaceColumn = new("Namespace");

    public static void AppendJoin(SqlWriter writer, string rootAlias, DbColumnName descriptorFkColumn)
    {
        writer.Append(" LEFT JOIN ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(Table));
        writer.Append($" {Alias} ON {Alias}.");
        writer.AppendQuoted("DescriptorId");
        writer.Append($" = {rootAlias}.");
        writer.AppendQuoted(descriptorFkColumn.Value);
    }

    public static void AppendProposedNamespace(SqlWriter writer, string parameterName)
    {
        writer.Append($"(SELECT {Alias}.");
        writer.AppendQuoted(NamespaceColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(Table));
        writer.Append($" {Alias} WHERE {Alias}.");
        writer.AppendQuoted("DescriptorId");
        writer.Append(" = ");
        writer.AppendParameter(parameterName);
        writer.Append(")");
    }
}
