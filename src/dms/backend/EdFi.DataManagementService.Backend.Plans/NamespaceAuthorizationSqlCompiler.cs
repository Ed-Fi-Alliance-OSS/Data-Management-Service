// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input specification for compiling single-record namespace authorization SQL. Carries the planned
/// namespace checks (typically one for read/delete, one or two for write), the dialect-specific
/// namespace prefix parameterization, and the parameter names used for stored-row identification and
/// the proposed-value namespace.
/// </summary>
/// <param name="Checks">The planned namespace authorization checks in emission order.</param>
/// <param name="NamespacePrefixParameterization">Dialect-specific namespace prefix parameterization.</param>
/// <param name="DocumentIdParameterName">Bare parameter name used for the stored row's DocumentId.</param>
/// <param name="ProposedNamespaceParameterName">Bare parameter name carrying the proposed namespace value.</param>
public sealed record NamespaceAuthorizationSqlSpec(
    IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks,
    NamespacePrefixParameterization NamespacePrefixParameterization,
    string DocumentIdParameterName,
    string ProposedNamespaceParameterName
);

/// <summary>
/// Compiled single-record namespace authorization SQL plan.
/// </summary>
/// <param name="AuthorizationSql">The compiled SQL command body.</param>
/// <param name="ParametersInOrder">Deterministic parameter metadata in plan order.</param>
public sealed record NamespaceAuthorizationSqlPlan(
    string AuthorizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder
);

/// <summary>
/// Compiles co-batched single-record namespace authorization SQL. Each planned check becomes one
/// <c>SELECT CASE</c> statement; the first failure raises AUTH1 with payload
/// <c>ns1|&lt;index&gt;|&lt;kind&gt;</c> and aborts the batch.
/// </summary>
/// <remarks>
/// The null-guarded prefix LIKE predicate is built by <see cref="NamespacePrefixSqlHelper"/> — the same
/// builder used by GET-many — so PostgreSQL and SQL Server emit the same shape on both paths.
/// </remarks>
public sealed class NamespaceAuthorizationSqlCompiler(SqlDialect dialect)
{
    private const string RootAlias = "r";

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    public NamespaceAuthorizationSqlPlan Compile(NamespaceAuthorizationSqlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Checks);
        ArgumentNullException.ThrowIfNull(spec.NamespacePrefixParameterization);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.DocumentIdParameterName,
            nameof(spec.DocumentIdParameterName)
        );
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.ProposedNamespaceParameterName,
            nameof(spec.ProposedNamespaceParameterName)
        );

        if (spec.Checks.Count == 0)
        {
            throw new ArgumentException(
                "Single-record namespace authorization requires at least one check spec.",
                nameof(spec)
            );
        }

        NamespacePrefixParameterizationValidator.ValidateOrThrow(
            spec.NamespacePrefixParameterization,
            _dialect,
            nameof(NamespaceAuthorizationSqlSpec.NamespacePrefixParameterization),
            "Single-record namespace authorization SQL"
        );

        var rootTable = spec.Checks[0].RootTable;
        var mismatchedTable = spec
            .Checks.Select(static check => check.RootTable)
            .FirstOrDefault(table => !table.Equals(rootTable));

        if (mismatchedTable != default)
        {
            throw new ArgumentException(
                $"Namespace authorization check specs must share one root table. Found '{mismatchedTable}' and '{rootTable}'.",
                nameof(spec)
            );
        }

        var writer = new SqlWriter(_sqlDialect);
        var hasStored = spec.Checks.Any(static check =>
            check.ValueSource is NamespaceAuthorizationCheckValueSource.Stored
        );
        var hasProposed = spec.Checks.Any(static check =>
            check.ValueSource is NamespaceAuthorizationCheckValueSource.Proposed
        );

        for (var index = 0; index < spec.Checks.Count; index++)
        {
            var check = spec.Checks[index];

            switch (check.ValueSource)
            {
                case NamespaceAuthorizationCheckValueSource.Stored:
                    AppendStoredCheckSql(writer, check, spec);
                    break;
                case NamespaceAuthorizationCheckValueSource.Proposed:
                    AppendProposedCheckSql(writer, check, spec);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(spec),
                        check.ValueSource,
                        "Unsupported namespace authorization value source."
                    );
            }

            writer.AppendLine(";");
        }

        return new NamespaceAuthorizationSqlPlan(
            writer.ToString(),
            BuildParametersInOrder(spec, hasStored, hasProposed)
        );
    }

    private void AppendStoredCheckSql(
        SqlWriter writer,
        NamespaceAuthorizationCheckSpec check,
        NamespaceAuthorizationSqlSpec spec
    )
    {
        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            // Authorized: row exists with non-null namespace matching at least one prefix.
            writer.Append("WHEN EXISTS (");
            AppendStoredRowSelect(
                writer,
                check,
                spec.DocumentIdParameterName,
                appendNamespacePredicate: predicateWriter =>
                {
                    NamespacePrefixSqlHelper.AppendRootTableNamespacePredicate(
                        predicateWriter,
                        RootAlias,
                        check.NamespaceColumn,
                        spec.NamespacePrefixParameterization
                    );
                }
            );
            writer.AppendLine(") THEN 1");

            // Stored namespace is null or empty → 'u'. Empty strings classify identically to NULL
            // so legacy rows with empty namespace map to the uninitialized branch rather than
            // mismatch.
            writer.Append("WHEN EXISTS (");
            AppendStoredRowSelect(
                writer,
                check,
                spec.DocumentIdParameterName,
                appendNamespacePredicate: predicateWriter =>
                {
                    predicateWriter.Append("(");
                    predicateWriter.Append($"{RootAlias}.").AppendQuoted(check.NamespaceColumn.Value);
                    predicateWriter.Append(" IS NULL OR ");
                    predicateWriter.Append($"{RootAlias}.").AppendQuoted(check.NamespaceColumn.Value);
                    predicateWriter.Append(" = '')");
                }
            );
            writer.Append(") THEN ");
            AppendAuth1Throw(
                writer,
                check.Index,
                NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized
            );
            writer.AppendLine();

            // No row for the target DocumentId at all → 's' (stale). The target was deleted between the
            // unlocked target lookup and this check. Read paths re-resolve the target and surface the
            // resulting 404 rather than a namespace mismatch; locked write/delete paths row-lock the
            // target before this check, so they never reach this branch.
            writer.Append("WHEN NOT EXISTS (");
            AppendStoredRowByDocumentId(writer, check, spec.DocumentIdParameterName);
            writer.Append(") THEN ");
            AppendAuth1Throw(writer, check.Index, NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing);
            writer.AppendLine();

            // Otherwise → 'm'. The row exists with a non-null namespace that matches no configured prefix.
            writer.Append("ELSE ");
            AppendAuth1Throw(writer, check.Index, NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch);
            writer.AppendLine();
        }

        writer.Append("END");
    }

    private void AppendProposedCheckSql(
        SqlWriter writer,
        NamespaceAuthorizationCheckSpec check,
        NamespaceAuthorizationSqlSpec spec
    )
    {
        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            // Proposed value is null or empty → 'r'.
            writer.Append("WHEN ");
            NamespacePrefixSqlHelper.AppendParameterIsNullOrEmpty(
                writer,
                spec.ProposedNamespaceParameterName
            );
            writer.Append(" THEN ");
            AppendAuth1Throw(
                writer,
                check.Index,
                NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
            );
            writer.AppendLine();

            // Proposed value matches at least one prefix → authorized.
            writer.Append("WHEN ");
            NamespacePrefixSqlHelper.AppendLikeMatch(
                writer,
                lhsWriter => lhsWriter.AppendParameter(spec.ProposedNamespaceParameterName),
                spec.NamespacePrefixParameterization
            );
            writer.AppendLine(" THEN 1");

            // Otherwise → 'm'.
            writer.Append("ELSE ");
            AppendAuth1Throw(writer, check.Index, NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch);
            writer.AppendLine();
        }

        writer.Append("END");
    }

    private void AppendAuth1Throw(
        SqlWriter writer,
        int emittedIndex,
        NamespaceAuthorizationAuth1FailureKind failureKind
    )
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(emittedIndex, failureKind)
        );

        switch (_dialect)
        {
            case SqlDialect.Pgsql:
                writer.AppendQuoted("dms");
                writer.Append(".");
                writer.AppendQuoted("throw_error");
                writer.Append("('");
                writer.Append(NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append("', '");
                writer.Append(payload);
                writer.Append("')");
                return;

            case SqlDialect.Mssql:
                writer.Append("CAST('");
                writer.Append(NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append(" - ");
                writer.Append(payload);
                writer.Append("' AS INT)");
                return;

            default:
                throw new NotSupportedException(
                    $"Single-record namespace authorization SQL does not support SQL dialect '{_dialect}'."
                );
        }
    }

    private static void AppendStoredRowSelect(
        SqlWriter writer,
        NamespaceAuthorizationCheckSpec check,
        string documentIdParameterName,
        Action<SqlWriter> appendNamespacePredicate
    )
    {
        AppendStoredRowByDocumentId(writer, check, documentIdParameterName);
        writer.Append(" AND ");
        appendNamespacePredicate(writer);
    }

    private static void AppendStoredRowByDocumentId(
        SqlWriter writer,
        NamespaceAuthorizationCheckSpec check,
        string documentIdParameterName
    )
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(check.RootTable));
        writer.Append($" {RootAlias} WHERE {RootAlias}.");
        writer.AppendQuoted("DocumentId");
        writer.Append(" = ");
        writer.AppendParameter(documentIdParameterName);
    }

    private static IReadOnlyList<QuerySqlParameter> BuildParametersInOrder(
        NamespaceAuthorizationSqlSpec spec,
        bool hasStored,
        bool hasProposed
    )
    {
        List<QuerySqlParameter> parameters = [];

        if (hasStored)
        {
            parameters.Add(new QuerySqlParameter(QuerySqlParameterRole.Filter, spec.DocumentIdParameterName));
        }

        if (hasProposed)
        {
            parameters.Add(
                new QuerySqlParameter(QuerySqlParameterRole.Filter, spec.ProposedNamespaceParameterName)
            );
        }

        parameters.AddRange(
            NamespacePrefixSqlHelper.BuildFilterParametersInOrder(spec.NamespacePrefixParameterization)
        );

        return parameters;
    }
}
