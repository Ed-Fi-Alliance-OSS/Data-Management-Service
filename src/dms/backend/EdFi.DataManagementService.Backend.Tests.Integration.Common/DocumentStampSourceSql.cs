// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

/// <summary>
/// Builds the query an integration fixture uses to read a document's authoritative stamps.
/// </summary>
/// <remarks>
/// The resource root row — or the <c>dms.Descriptor</c> row for a descriptor — owns
/// <c>ContentVersion</c>, <c>IdentityVersion</c> and their timestamps: no trigger writes
/// <c>dms.Document</c> any more. A stamp read therefore has to name the owning table. Fixtures that hold
/// only a <c>DocumentId</c> select across every candidate stamp table; <c>DocumentId</c> is the primary
/// key of each, so at most one branch yields a row and the caller can still take the single result.
/// </remarks>
public static class DocumentStampSourceSql
{
    /// <summary>
    /// Builds a PostgreSQL <c>UNION ALL</c> over <paramref name="stampTables"/> projecting the four
    /// stamp columns for the row whose <c>DocumentId</c> matches <c>@documentId</c>.
    /// </summary>
    public static string BuildPostgresqlStampQuery(IEnumerable<(string Schema, string Table)> stampTables) =>
        BuildStampQuery(stampTables, bracketQuoted: false);

    /// <summary>
    /// Builds the SQL Server equivalent of <see cref="BuildPostgresqlStampQuery" />.
    /// </summary>
    public static string BuildMssqlStampQuery(IEnumerable<(string Schema, string Table)> stampTables) =>
        BuildStampQuery(stampTables, bracketQuoted: true);

    private static string BuildStampQuery(
        IEnumerable<(string Schema, string Table)> stampTables,
        bool bracketQuoted
    )
    {
        ArgumentNullException.ThrowIfNull(stampTables);

        var builder = new StringBuilder();
        var first = true;

        foreach (var (schema, table) in stampTables)
        {
            if (!first)
            {
                builder.Append("\nUNION ALL\n");
            }
            first = false;

            builder.Append("SELECT ");
            builder.Append(Quote("ContentVersion", bracketQuoted));
            builder.Append(", ");
            builder.Append(Quote("IdentityVersion", bracketQuoted));
            builder.Append(", ");
            builder.Append(Quote("ContentLastModifiedAt", bracketQuoted));
            builder.Append(", ");
            builder.Append(Quote("IdentityLastModifiedAt", bracketQuoted));
            builder.Append(" FROM ");
            builder.Append(Quote(schema, bracketQuoted));
            builder.Append('.');
            builder.Append(Quote(table, bracketQuoted));
            builder.Append(" WHERE ");
            builder.Append(Quote("DocumentId", bracketQuoted));
            builder.Append(" = @documentId");
        }

        if (first)
        {
            throw new ArgumentException("At least one stamp table must be supplied.", nameof(stampTables));
        }

        builder.Append(';');
        return builder.ToString();
    }

    private static string Quote(string identifier, bool bracketQuoted) =>
        bracketQuoted ? $"[{identifier}]" : $"\"{identifier}\"";
}
