// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input specification for compiling the single-record ownership authorization SQL.
/// </summary>
/// <param name="Check">The planned ownership check. Exactly one per operation.</param>
/// <param name="OwnershipTokenParameterization">Dialect-specific ownership-token parameterization.</param>
/// <param name="DocumentIdParameterName">Bare parameter name for the stored row's DocumentId.</param>
/// <param name="RowGuardPredicateSql">
/// Optional raw predicate appended as a <c>WHERE</c> clause to the emitted check select. When it is false the
/// check's result set is empty and none of its branches — the abort device included — evaluates, which is how
/// a check co-batched behind a captured target stays vacuous for a write that resolved to a create. That is
/// what makes a POST-create immune to ownership denial without needing a second command shape.
/// </param>
public sealed record OwnershipAuthorizationSqlSpec(
    OwnershipAuthorizationCheckSpec Check,
    OwnershipTokenParameterization OwnershipTokenParameterization,
    string DocumentIdParameterName,
    string? RowGuardPredicateSql = null
);

/// <summary>
/// Compiled single-record ownership authorization SQL plan.
/// </summary>
/// <param name="AuthorizationSql">The compiled SQL command body.</param>
/// <param name="ParametersInOrder">Deterministic parameter metadata in plan order.</param>
public sealed record OwnershipAuthorizationSqlPlan(
    string AuthorizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder
);

/// <summary>
/// Compiles the single-record ownership authorization check as one <c>SELECT CASE</c> statement. A failure
/// raises <c>AUTH1</c> with payload <c>own1|configuredIndex|kind</c> and aborts the rest of the command.
/// </summary>
/// <remarks>
/// <para>
/// The subject is <c>dms.Document.CreatedByOwnershipTokenId</c> addressed by <c>DocumentId</c>, which is why
/// this compiler needs no resource model: unlike the namespace and custom view-based checks, the ownership
/// check's table and column are the same for every resource.
/// </para>
/// <para>
/// The three failure branches exist so the response can distinguish §2.14 from §2.13 and a stale target from
/// either. Their order matters: authorized first, then stored-null, then no-row, then mismatch as the
/// fallthrough. Testing stored-null before no-row keeps a deleted row from being reported as an
/// uninitialized token.
/// </para>
/// </remarks>
public sealed class OwnershipAuthorizationSqlCompiler(SqlDialect dialect)
{
    private const string DocumentAlias = "d";

    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _ownershipTokenColumn = new("CreatedByOwnershipTokenId");

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    public OwnershipAuthorizationSqlPlan Compile(OwnershipAuthorizationSqlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Check);
        ArgumentNullException.ThrowIfNull(spec.OwnershipTokenParameterization);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.DocumentIdParameterName,
            nameof(spec.DocumentIdParameterName)
        );

        OwnershipTokenParameterizationValidator.ValidateOrThrow(
            spec.OwnershipTokenParameterization,
            _dialect,
            nameof(OwnershipAuthorizationSqlSpec.OwnershipTokenParameterization),
            "Single-record ownership authorization SQL"
        );

        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            // Authorized: the row exists, carries a token, and that token is one of the caller's.
            writer.Append("WHEN EXISTS (");
            AppendStoredRowSelect(
                writer,
                spec,
                predicateWriter =>
                {
                    OwnershipTokenSqlHelper.AppendIsNotNull(
                        predicateWriter,
                        DocumentAlias,
                        _ownershipTokenColumn
                    );
                    predicateWriter.Append(" AND ");
                    OwnershipTokenSqlHelper.AppendMembershipPredicate(
                        predicateWriter,
                        DocumentAlias,
                        _ownershipTokenColumn,
                        spec.OwnershipTokenParameterization
                    );
                }
            );
            writer.AppendLine(") THEN 1");

            // Stored token is null → 'u' (§2.14). Checked before the no-row branch so a row that exists
            // with an unassigned token is never reported as a missing target.
            writer.Append("WHEN EXISTS (");
            AppendStoredRowSelect(
                writer,
                spec,
                predicateWriter =>
                    OwnershipTokenSqlHelper.AppendIsNull(
                        predicateWriter,
                        DocumentAlias,
                        _ownershipTokenColumn
                    )
            );
            writer.Append(") THEN ");
            AppendAuth1Throw(
                writer,
                spec.Check,
                OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized
            );
            writer.AppendLine();

            // No row for the target DocumentId → 's' (stale). The target was deleted between the unlocked
            // lookup and this check. Read paths re-resolve the target and surface the resulting 404; locked
            // write and delete paths row-lock the target first, so they never reach this branch.
            writer.Append("WHEN NOT EXISTS (");
            AppendStoredRowByDocumentId(writer, spec.DocumentIdParameterName);
            writer.Append(") THEN ");
            AppendAuth1Throw(writer, spec.Check, OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing);
            writer.AppendLine();

            // Otherwise → 'm' (§2.13). The row exists with a non-null token matching none of the caller's.
            writer.Append("ELSE ");
            AppendAuth1Throw(
                writer,
                spec.Check,
                OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch
            );
            writer.AppendLine();
        }

        writer.Append("END");

        if (spec.RowGuardPredicateSql is { } rowGuardPredicateSql)
        {
            writer.Append(" WHERE ");
            writer.Append(rowGuardPredicateSql);
        }

        writer.AppendLine(";");

        return new OwnershipAuthorizationSqlPlan(writer.ToString(), BuildParametersInOrder(spec));
    }

    private static void AppendStoredRowSelect(
        SqlWriter writer,
        OwnershipAuthorizationSqlSpec spec,
        Action<SqlWriter> appendOwnershipPredicate
    )
    {
        AppendStoredRowByDocumentId(writer, spec.DocumentIdParameterName);
        writer.Append(" AND ");
        appendOwnershipPredicate(writer);
    }

    private static void AppendStoredRowByDocumentId(SqlWriter writer, string documentIdParameterName)
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(_documentTable));
        writer.Append($" {DocumentAlias} WHERE {DocumentAlias}.");
        writer.AppendQuoted(_documentIdColumn.Value);
        writer.Append(" = ");
        writer.AppendParameter(documentIdParameterName);
    }

    private void AppendAuth1Throw(
        SqlWriter writer,
        OwnershipAuthorizationCheckSpec check,
        OwnershipAuthorizationAuth1FailureKind failureKind
    )
    {
        var payload = OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
            new OwnershipAuthorizationAuth1FailurePayload(check.RawConfiguredIndex, failureKind)
        );

        switch (_dialect)
        {
            case SqlDialect.Pgsql:
                writer.AppendQuoted("dms");
                writer.Append(".");
                writer.AppendQuoted("throw_error");
                writer.Append("('");
                writer.Append(OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append("', '");
                writer.Append(payload);
                writer.Append("')");
                return;

            case SqlDialect.Mssql:
                writer.Append("CAST('");
                writer.Append(OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append(" - ");
                writer.Append(payload);
                writer.Append("' AS INT)");
                return;

            default:
                throw new NotSupportedException(
                    $"Single-record ownership authorization SQL does not support SQL dialect '{_dialect}'."
                );
        }
    }

    private static IReadOnlyList<QuerySqlParameter> BuildParametersInOrder(
        OwnershipAuthorizationSqlSpec spec
    ) =>
        [
            new QuerySqlParameter(QuerySqlParameterRole.Filter, spec.DocumentIdParameterName),
            .. OwnershipTokenSqlHelper.BuildFilterParametersInOrder(spec.OwnershipTokenParameterization),
        ];
}
