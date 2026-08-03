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
/// <c>ContentVersion</c>, <c>IdentityVersion</c> and their timestamps. A stamp read therefore has to
/// name the owning table. Fixtures that hold
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

    /// <summary>
    /// Builds a PostgreSQL query counting the documents carrying <c>@documentUuid</c> across every stamp
    /// table. The resource root row (or the <c>dms.Descriptor</c> row) <em>is</em> the document now —
    /// <c>DocumentId</c> comes from <c>dms.DocumentIdSequence</c> — so "does this document exist" is
    /// answered by the owning table, and each table's
    /// <c>UX_&lt;Root&gt;_DocumentUuid</c> keeps the count at most 1 per branch.
    /// </summary>
    public static string BuildPostgresqlDocumentExistsQuery(
        IEnumerable<(string Schema, string Table)> stampTables
    ) => BuildDocumentExistsQuery(stampTables, bracketQuoted: false);

    /// <summary>
    /// The SQL Server equivalent of <see cref="BuildPostgresqlDocumentExistsQuery" />.
    /// </summary>
    public static string BuildMssqlDocumentExistsQuery(
        IEnumerable<(string Schema, string Table)> stampTables
    ) => BuildDocumentExistsQuery(stampTables, bracketQuoted: true);

    /// <summary>
    /// Builds a PostgreSQL query for the whole document-state row keyed by <c>@documentUuid</c>: the
    /// identity and stamp columns come from whichever stamp table owns the uuid. There is no
    /// <c>ResourceKeyId</c> — that column lived only on <c>dms.Document</c>; a row's resource identity
    /// is now the table it lives in.
    /// </summary>
    public static string BuildPostgresqlDocumentStateQuery(
        IEnumerable<(string Schema, string Table)> stampTables
    )
    {
        ArgumentNullException.ThrowIfNull(stampTables);

        var builder = new StringBuilder();
        var first = true;

        foreach (var (schema, table) in stampTables)
        {
            if (!first)
            {
                builder.Append("\n    UNION ALL\n");
            }
            first = false;

            builder.Append(
                "    SELECT \"DocumentId\", \"DocumentUuid\", \"ContentVersion\", \"IdentityVersion\", "
                    + "\"ContentLastModifiedAt\", \"IdentityLastModifiedAt\", \"CreatedAt\"\n    FROM "
            );
            builder.Append($"\"{schema}\".\"{table}\"");
        }

        if (first)
        {
            throw new ArgumentException("At least one stamp table must be supplied.", nameof(stampTables));
        }

        return $"""
            SELECT
                root."DocumentId",
                root."DocumentUuid",
                root."ContentVersion",
                root."IdentityVersion",
                root."ContentLastModifiedAt",
                root."IdentityLastModifiedAt",
                root."CreatedAt"
            FROM (
            {builder}
            ) root
            WHERE root."DocumentUuid" = @documentUuid;
            """;
    }

    /// <summary>
    /// The SQL Server equivalent of <see cref="BuildPostgresqlDocumentStateQuery" />.
    /// </summary>
    public static string BuildMssqlDocumentStateQuery(IEnumerable<(string Schema, string Table)> stampTables)
    {
        ArgumentNullException.ThrowIfNull(stampTables);

        var builder = new StringBuilder();
        var first = true;

        foreach (var (schema, table) in stampTables)
        {
            if (!first)
            {
                builder.Append("\n    UNION ALL\n");
            }
            first = false;

            builder.Append(
                "    SELECT [DocumentId], [DocumentUuid], [ContentVersion], [IdentityVersion], "
                    + "[ContentLastModifiedAt], [IdentityLastModifiedAt], [CreatedAt]\n    FROM "
            );
            builder.Append($"[{schema}].[{table}]");
        }

        if (first)
        {
            throw new ArgumentException("At least one stamp table must be supplied.", nameof(stampTables));
        }

        return $"""
            SELECT
                root.[DocumentId],
                root.[DocumentUuid],
                root.[ContentVersion],
                root.[IdentityVersion],
                root.[ContentLastModifiedAt],
                root.[IdentityLastModifiedAt],
                root.[CreatedAt]
            FROM (
            {builder}
            ) root
            WHERE root.[DocumentUuid] = @documentUuid;
            """;
    }

    private static string BuildDocumentExistsQuery(
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
                builder.Append("\n    UNION ALL\n");
            }
            first = false;

            builder.Append("    SELECT ");
            builder.Append(Quote("DocumentUuid", bracketQuoted));
            builder.Append(" FROM ");
            builder.Append(Quote(schema, bracketQuoted));
            builder.Append('.');
            builder.Append(Quote(table, bracketQuoted));
        }

        if (first)
        {
            throw new ArgumentException("At least one stamp table must be supplied.", nameof(stampTables));
        }

        var count = bracketQuoted ? "COUNT_BIG(*)" : "COUNT(*)::bigint";
        var documentUuid = Quote("DocumentUuid", bracketQuoted);

        return $"""
            SELECT {count}
            FROM (
            {builder}
            ) document
            WHERE document.{documentUuid} = @documentUuid;
            """;
    }

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
