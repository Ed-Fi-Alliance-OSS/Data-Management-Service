// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal static class CustomViewAuthorizationValidator
{
    public static async Task ValidateAsync(
        IRelationalCommandExecutor commandExecutor,
        SqlDialect dialect,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        if (customViewChecks is null || customViewChecks.Count == 0)
        {
            return;
        }

        RelationalCommand command = new(BuildCommandText(dialect, customViewChecks));

        try
        {
            await commandExecutor
                .ExecuteReaderAsync(
                    command,
                    static async (reader, cancellationToken) =>
                    {
                        do
                        {
                            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                        return true;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }
    }

    /// <summary>
    /// Validates the views behind single-record checks. Used where a check's answer is decided in C# and no
    /// membership statement will run: without this, a misconfigured view would be reported as the denial that
    /// decision produces instead of the documented <c>urn:ed-fi:api:system</c> 500.
    /// </summary>
    public static Task ValidateSingleRecordAsync(
        IRelationalCommandExecutor commandExecutor,
        SqlDialect dialect,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec>? checks,
        CancellationToken cancellationToken = default
    )
    {
        if (checks is null || checks.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ValidateAsync(
            commandExecutor,
            dialect,
            [.. checks.Select(AdaptForValidation)],
            cancellationToken
        );
    }

    private static PageDocumentIdAuthorizationCustomViewCheck AdaptForValidation(
        SingleRecordCustomViewAuthorizationCheckSpec check
    )
    {
        // The first path step starts at the subject's root row, so it names the root table and the column the
        // walk begins from — which is what the validation join needs.
        var firstStep = check.PathToBasisResource[0];

        return new PageDocumentIdAuthorizationCustomViewCheck(
            check.ConfiguredStrategy.StrategyName,
            check.ConfiguredStrategy.RawConfiguredIndex,
            check.AuthView,
            check.AuthViewDocumentIdColumn,
            check.PathToBasisResource,
            firstStep.SourceTable,
            firstStep.SourceColumnName
        );
    }

    internal static string BuildCommandText(
        SqlDialect dialect,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> customViewChecks
    )
    {
        ArgumentNullException.ThrowIfNull(customViewChecks);

        var builder = new StringBuilder();

        for (var index = 0; index < customViewChecks.Count; index++)
        {
            PageDocumentIdAuthorizationCustomViewCheck check = customViewChecks[index];

            if (index > 0)
            {
                builder.AppendLine(";");
            }

            if (dialect is SqlDialect.Mssql)
            {
                AppendMssqlSchemaValidation(builder, check);
                continue;
            }

            AppendPgsqlSchemaValidation(builder, check);
        }

        return builder.ToString();
    }

    private static void AppendPgsqlSchemaValidation(
        StringBuilder builder,
        PageDocumentIdAuthorizationCustomViewCheck check
    )
    {
        // A join against the view (below) naturally raises for a missing view (undefined_table), a
        // missing DocumentId column (undefined_column), and text-like DocumentId types (the bigint = text
        // operator does not exist). It does NOT, however, catch a table masquerading as an authorization
        // view or numeric DocumentId types such as integer or numeric: PostgreSQL provides valid bigint =
        // integer / bigint = numeric operators, so an empty invalid object could silently yield an empty
        // 200 result. The catalog guards close those gaps and mirror the MSSQL view/type contract check.
        // Both regular ('v') and materialized ('m') views satisfy the object-kind contract. The column
        // type check reads pg_catalog.pg_attribute rather than information_schema.columns because a
        // materialized view's columns are not exposed through information_schema.
        string schemaLiteral = QuotePgsqlStringLiteral(check.AuthView.Schema.Value);
        string viewLiteral = QuotePgsqlStringLiteral(check.AuthView.Name);
        string documentIdColumnLiteral = QuotePgsqlStringLiteral(check.AuthViewDocumentIdColumn.Value);

        // The schema/view/column names are embedded in the DO block as string literals, so a fixed $$
        // delimiter would be terminated early by a name that itself contains the delimiter — leaving the
        // rest of the block to be parsed as top-level SQL. Derive a tag that provably does not occur in
        // the block body instead.
        string dollarQuoteTag = BuildPgsqlDollarQuoteTag(schemaLiteral, viewLiteral, documentIdColumnLiteral);

        builder
            .Append("DO ")
            .AppendLine(dollarQuoteTag)
            .AppendLine("BEGIN")
            .AppendLine("    IF EXISTS (")
            .AppendLine("        SELECT 1")
            .AppendLine("        FROM pg_catalog.pg_class c")
            .AppendLine("        INNER JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace")
            .Append("        WHERE n.nspname = ")
            .Append(schemaLiteral)
            .AppendLine()
            .Append("          AND c.relname = ")
            .Append(viewLiteral)
            .AppendLine()
            .AppendLine("          AND c.relkind NOT IN ('v', 'm')")
            .AppendLine("    ) THEN")
            .AppendLine("        RAISE EXCEPTION 'Invalid custom authorization view DocumentId contract.';")
            .AppendLine("    END IF;")
            .AppendLine()
            .AppendLine("    IF EXISTS (")
            .AppendLine("        SELECT 1")
            .AppendLine("        FROM pg_catalog.pg_attribute a")
            .AppendLine("        INNER JOIN pg_catalog.pg_class c ON c.oid = a.attrelid")
            .AppendLine("        INNER JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace")
            .Append("        WHERE n.nspname = ")
            .Append(schemaLiteral)
            .AppendLine()
            .Append("          AND c.relname = ")
            .Append(viewLiteral)
            .AppendLine()
            .AppendLine("          AND c.relkind IN ('v', 'm')")
            .Append("          AND a.attname = ")
            .Append(documentIdColumnLiteral)
            .AppendLine()
            .AppendLine("          AND a.attnum > 0")
            .AppendLine("          AND NOT a.attisdropped")
            .AppendLine("          AND a.atttypid <> 'pg_catalog.int8'::regtype")
            .AppendLine("    ) THEN")
            .AppendLine("        RAISE EXCEPTION 'Invalid custom authorization view DocumentId contract.';")
            .AppendLine("    END IF;")
            .Append("END ")
            .Append(dollarQuoteTag)
            .AppendLine(";");

        string authViewDocumentIdColumn = SqlIdentifierQuoter.QuoteIdentifier(
            SqlDialect.Pgsql,
            check.AuthViewDocumentIdColumn
        );
        string rootDocumentIdColumn = SqlIdentifierQuoter.QuoteIdentifier(
            SqlDialect.Pgsql,
            check.RootDocumentIdColumn
        );

        builder
            .AppendLine()
            .Append("SELECT cv.")
            .Append(authViewDocumentIdColumn)
            .Append(" FROM ")
            .Append(SqlIdentifierQuoter.QuoteTableName(SqlDialect.Pgsql, check.AuthView))
            .Append(" cv INNER JOIN ")
            .Append(SqlIdentifierQuoter.QuoteTableName(SqlDialect.Pgsql, check.RootTable))
            .Append(" root ON root.")
            .Append(rootDocumentIdColumn)
            .Append(" = cv.")
            .Append(authViewDocumentIdColumn)
            // LIMIT 0 keeps the probe row-free: planning still binds the view, the columns, and the
            // join operator — which is what raises undefined_table, undefined_column and missing
            // operator errors — without scanning for a match or exhausting an empty/disjoint view.
            .Append(" LIMIT 0");
    }

    /// <summary>
    /// The binary collation forced onto the SQL Server catalog name comparisons. <c>sys</c> name columns
    /// are <c>sysname</c>, carrying the database collation, which is case-insensitive by default — so a
    /// plain <c>=</c> would accept a mis-cased schema, view, or DocumentId column, and the bracketed bind
    /// probe below resolves identifiers case-insensitively too. Both would then pass and the request would
    /// return a filtered 200 against an object that is not the configured <c>auth.{StrategyName}</c>.
    /// PostgreSQL already compares <c>pg_catalog</c> names case-sensitively, so this keeps the documented
    /// contract identical on both engines.
    /// </summary>
    private const string MssqlCatalogNameCollation = " COLLATE Latin1_General_100_BIN2";

    // The catalog guard proves the auth object is a view exposing a bigint DocumentId, and the row-free
    // bind probe proves the view and column resolve. Like PostgreSQL, the resolved root-to-basis join
    // path is left to the actual page query, so a path error surfaces there on both engines rather than
    // from an engine-specific extra probe.
    private static void AppendMssqlSchemaValidation(
        StringBuilder builder,
        PageDocumentIdAuthorizationCustomViewCheck check
    )
    {
        string schemaName = QuoteMssqlUnicodeStringLiteral(check.AuthView.Schema.Value);
        string viewName = QuoteMssqlUnicodeStringLiteral(check.AuthView.Name);
        string documentIdColumn = QuoteMssqlUnicodeStringLiteral(check.AuthViewDocumentIdColumn.Value);
        string bindViewSql =
            "SELECT TOP (0) cv."
            + SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, check.AuthViewDocumentIdColumn)
            + " FROM "
            + SqlIdentifierQuoter.QuoteTableName(SqlDialect.Mssql, check.AuthView)
            + " cv WHERE cv."
            + SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, check.AuthViewDocumentIdColumn)
            + " IS NOT NULL";

        builder
            .AppendLine("IF NOT EXISTS (")
            .AppendLine("    SELECT 1")
            .AppendLine("    FROM sys.views v")
            .AppendLine("    INNER JOIN sys.schemas s ON s.schema_id = v.schema_id")
            .AppendLine("    INNER JOIN sys.columns c ON c.object_id = v.object_id")
            .AppendLine("    INNER JOIN sys.types t ON t.user_type_id = c.user_type_id")
            .Append("    WHERE s.name")
            .Append(MssqlCatalogNameCollation)
            .Append(" = ")
            .Append(schemaName)
            .AppendLine()
            .Append("      AND v.name")
            .Append(MssqlCatalogNameCollation)
            .Append(" = ")
            .Append(viewName)
            .AppendLine()
            .Append("      AND c.name")
            .Append(MssqlCatalogNameCollation)
            .Append(" = ")
            .Append(documentIdColumn)
            .AppendLine()
            .AppendLine("      AND c.system_type_id = TYPE_ID(N'bigint')")
            .AppendLine(")")
            .AppendLine("    THROW 50000, 'Invalid custom authorization view DocumentId contract.', 1;")
            .Append("EXEC sys.sp_executesql ")
            .Append(QuoteMssqlUnicodeStringLiteral(bindViewSql));
    }

    /// <summary>
    /// Builds a PostgreSQL dollar-quote delimiter that cannot appear inside the <c>DO</c> block body.
    /// Starts at <c>$dmscv$</c> and appends <c>x</c> to the tag name until no supplied literal contains it,
    /// so a schema, view, or column name carrying <c>$$</c> — or even <c>$dmscv$</c> itself — cannot
    /// terminate the block early and expose the remainder of the body as top-level SQL.
    /// </summary>
    internal static string BuildPgsqlDollarQuoteTag(params string[] embeddedLiterals)
    {
        ArgumentNullException.ThrowIfNull(embeddedLiterals);

        var tagName = new StringBuilder("dmscv");

        while (true)
        {
            var candidate = $"${tagName}$";

            if (
                !Array.Exists(
                    embeddedLiterals,
                    literal => literal.Contains(candidate, StringComparison.Ordinal)
                )
            )
            {
                return candidate;
            }

            tagName.Append('x');
        }
    }

    private static string QuoteMssqlUnicodeStringLiteral(string value) =>
        $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuotePgsqlStringLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
