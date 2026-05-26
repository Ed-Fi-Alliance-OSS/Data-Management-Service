// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles stored-value relationship authorization SQL for one already-resolved root document.
/// </summary>
public sealed class SingleRecordRelationshipAuthorizationSqlCompiler(SqlDialect dialect)
{
    private const string RootAlias = "r";
    private const string DocumentAlias = "d";
    private const string TargetCte = "target";
    private const string SubjectFailuresCte = "subject_failures";
    private const string FailedSubjectsCte = "failed_subjects";
    private const string FailurePayloadCte = "failure_payload";
    private const string StrategyOrdinalColumn = "StrategyOrdinal";
    private const string SubjectOrdinalColumn = "SubjectOrdinal";
    private const string FailureKindColumn = "FailureKind";
    private const string FailedColumn = "Failed";
    private const string PayloadColumn = "Payload";
    private const string AuthorizationResultColumn = "AuthorizationResult";
    private const string ContentVersionColumn = "ContentVersion";
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    public SingleRecordRelationshipAuthorizationSqlPlan Compile(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        ArgumentNullException.ThrowIfNull(spec);

        var normalizedSpec = NormalizeSpec(spec);
        var storedTarget = (RelationshipAuthorizationCheckTarget.Stored)
            normalizedSpec.CheckSpecs[0].CheckTarget;
        var parametersInOrder = BuildParametersInOrder(normalizedSpec);

        return new SingleRecordRelationshipAuthorizationSqlPlan(
            BuildSql(normalizedSpec, storedTarget),
            parametersInOrder
        );
    }

    private SingleRecordRelationshipAuthorizationSqlSpec NormalizeSpec(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        ArgumentNullException.ThrowIfNull(spec.CheckSpecs);
        ArgumentNullException.ThrowIfNull(spec.ClaimEducationOrganizationIdParameterization);
        PlanSqlWriterExtensions.ValidateBareParameterName(
            spec.DocumentIdParameterName,
            nameof(spec.DocumentIdParameterName)
        );

        if (spec.EmittedAuth1Index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spec),
                spec.EmittedAuth1Index,
                "Emitted AUTH1 index cannot be negative."
            );
        }

        if (spec.CheckSpecs.Count == 0)
        {
            throw new ArgumentException(
                "Single-record relationship authorization requires at least one check spec.",
                nameof(spec)
            );
        }

        var storedTargets = spec
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .OfType<RelationshipAuthorizationCheckTarget.Stored>()
            .ToArray();

        if (
            storedTargets.Length != spec.CheckSpecs.Count
            || spec.CheckSpecs.Any(static checkSpec =>
                checkSpec.ValueSource is not RelationshipAuthorizationValueSource.Stored
            )
        )
        {
            throw new ArgumentException(
                "Single-record relationship authorization supports only stored-value check specs.",
                nameof(spec)
            );
        }

        var rootTarget = storedTargets[0];

        if (
            Array.Exists(
                storedTargets,
                target =>
                    !target.RootTable.Equals(rootTarget.RootTable)
                    || !target.DocumentIdColumn.Equals(rootTarget.DocumentIdColumn)
            )
        )
        {
            throw new ArgumentException(
                "Single-record relationship authorization check specs must share one root target.",
                nameof(spec)
            );
        }

        if (spec.CheckSpecs.Any(static checkSpec => checkSpec.Subjects.Count == 0))
        {
            throw new ArgumentException(
                "Single-record relationship authorization check specs require at least one subject.",
                nameof(spec)
            );
        }

        var mismatchedSubject = spec
            .CheckSpecs.SelectMany(static checkSpec => checkSpec.Subjects)
            .FirstOrDefault(subject => !subject.Table.Equals(rootTarget.RootTable));

        if (mismatchedSubject is not null)
        {
            throw new ArgumentException(
                $"Authorization subject table '{mismatchedSubject.Table}' does not match root table '{rootTarget.RootTable}'.",
                nameof(spec)
            );
        }

        ValidateAuthorizationClaimParameterization(spec.ClaimEducationOrganizationIdParameterization);
        ValidateParameterNameCollisions(spec);

        return spec;
    }

    private static IReadOnlyList<QuerySqlParameter> BuildParametersInOrder(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        List<QuerySqlParameter> parameters =
        [
            new QuerySqlParameter(QuerySqlParameterRole.Filter, spec.DocumentIdParameterName),
        ];

        parameters.AddRange(
            BuildAuthorizationParametersInOrder(spec.ClaimEducationOrganizationIdParameterization)
        );

        return parameters;
    }

    private static IReadOnlyList<QuerySqlParameter> BuildAuthorizationParametersInOrder(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    ) =>
        authorizationClaimParameterization.Kind switch
        {
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    authorizationClaimParameterization.BaseParameterName,
                    QuerySqlParameterBinding.PgsqlArray
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar =>
            [
                .. authorizationClaimParameterization.ParameterNamesInOrder.Select(
                    static parameterName => new QuerySqlParameter(QuerySqlParameterRole.Filter, parameterName)
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    authorizationClaimParameterization.BaseParameterName,
                    QuerySqlParameterBinding.CreateMssqlStructured(
                        AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterTypeName,
                        AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                    )
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(authorizationClaimParameterization),
                authorizationClaimParameterization.Kind,
                "Unsupported authorization claim EdOrg parameterization kind."
            ),
        };

    private string BuildSql(
        SingleRecordRelationshipAuthorizationSqlSpec spec,
        RelationshipAuthorizationCheckTarget.Stored storedTarget
    )
    {
        var writer = new SqlWriter(_sqlDialect);
        var rootColumns = spec
            .CheckSpecs.SelectMany(static checkSpec => checkSpec.Subjects)
            .Select(static subject => subject.Column)
            .Distinct()
            .OrderBy(static column => column.Value, StringComparer.Ordinal)
            .ToArray();

        AppendTargetCte(writer, storedTarget, rootColumns, spec.DocumentIdParameterName);
        writer.AppendLine(",");
        AppendSubjectFailuresCte(writer, spec);
        writer.AppendLine(",");
        AppendFailedSubjectsCte(writer);
        writer.AppendLine(",");
        AppendFailurePayloadCte(writer, spec.EmittedAuth1Index);
        writer.AppendLine();
        AppendFinalSelect(writer, spec.CheckSpecs.Count);

        return writer.ToString();
    }

    private static void AppendTargetCte(
        SqlWriter writer,
        RelationshipAuthorizationCheckTarget.Stored storedTarget,
        IReadOnlyList<DbColumnName> rootColumns,
        string documentIdParameterName
    )
    {
        writer.AppendLine($"WITH {TargetCte} AS (");

        using (writer.Indent())
        {
            writer.AppendLine("SELECT");

            using (writer.Indent())
            {
                writer.Append($"{DocumentAlias}.");
                writer.AppendQuoted(ContentVersionColumn);
                writer.AppendLine(",");

                for (var columnIndex = 0; columnIndex < rootColumns.Count; columnIndex++)
                {
                    writer.Append($"{RootAlias}.");
                    writer.AppendQuoted(rootColumns[columnIndex].Value);
                    writer.AppendLine(columnIndex + 1 < rootColumns.Count ? "," : string.Empty);
                }
            }

            writer.Append("FROM ");
            writer.AppendRelation(new SqlRelationRef.PhysicalTable(storedTarget.RootTable));
            writer.AppendLine($" {RootAlias}");
            writer.Append("INNER JOIN ");
            writer.AppendRelation(new SqlRelationRef.PhysicalTable(_documentTable));
            writer.AppendLine($" {DocumentAlias}");
            writer.Append($"    ON {DocumentAlias}.");
            writer.AppendQuoted(_documentIdColumn.Value);
            writer.Append($" = {RootAlias}.");
            writer.AppendQuoted(storedTarget.DocumentIdColumn.Value);
            writer.AppendLine();
            writer.Append("WHERE ");
            writer.Append($"{RootAlias}.");
            writer.AppendQuoted(storedTarget.DocumentIdColumn.Value);
            writer.Append(" = ");
            writer.AppendParameter(documentIdParameterName);
            writer.AppendLine();
        }

        writer.Append(")");
    }

    private static void AppendSubjectFailuresCte(
        SqlWriter writer,
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        writer
            .Append($"{SubjectFailuresCte} (")
            .AppendQuoted(StrategyOrdinalColumn)
            .Append(", ")
            .AppendQuoted(SubjectOrdinalColumn)
            .Append(", ")
            .AppendQuoted(FailureKindColumn)
            .Append(", ")
            .AppendQuoted(FailedColumn)
            .AppendLine(") AS (");

        using (writer.Indent())
        {
            var emittedSubjectIndex = 0;

            for (var strategyOrdinal = 0; strategyOrdinal < spec.CheckSpecs.Count; strategyOrdinal++)
            {
                var checkSpec = spec.CheckSpecs[strategyOrdinal];

                for (var subjectOrdinal = 0; subjectOrdinal < checkSpec.Subjects.Count; subjectOrdinal++)
                {
                    if (emittedSubjectIndex > 0)
                    {
                        writer.AppendLine("UNION ALL");
                    }

                    AppendSubjectFailureSelect(
                        writer,
                        strategyOrdinal,
                        subjectOrdinal,
                        checkSpec,
                        spec.ClaimEducationOrganizationIdParameterization
                    );
                    emittedSubjectIndex++;
                }
            }
        }

        writer.Append(")");
    }

    private static void AppendSubjectFailureSelect(
        SqlWriter writer,
        int strategyOrdinal,
        int subjectOrdinal,
        RelationshipAuthorizationCheckSpec checkSpec,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        var subject = checkSpec.Subjects[subjectOrdinal];
        var authAlias = $"a{strategyOrdinal}_{subjectOrdinal}";

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            writer.AppendLine($"{strategyOrdinal},");
            writer.AppendLine($"{subjectOrdinal},");
            writer.Append("CASE WHEN ");
            AppendTargetColumn(writer, subject.Column);
            writer.Append(" IS NULL THEN 's' ELSE 'n' END,");
            writer.AppendLine();
            writer.Append("CASE WHEN ");
            AppendTargetColumn(writer, subject.Column);
            writer.Append(" IS NULL OR NOT EXISTS (");
            AppendAuthorizationExistsSql(
                writer,
                checkSpec,
                subject.Column,
                authorizationClaimParameterization,
                authAlias
            );
            writer.AppendLine(") THEN 1 ELSE 0 END");
        }

        writer.AppendLine();
        writer.AppendLine($"FROM {TargetCte}");
    }

    private static void AppendAuthorizationExistsSql(
        SqlWriter writer,
        RelationshipAuthorizationCheckSpec checkSpec,
        DbColumnName subjectColumn,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(checkSpec.AuthObject.Name));
        writer.Append($" {authAlias} WHERE {authAlias}.");
        writer.AppendQuoted(checkSpec.AuthObject.SubjectValueColumn.Value);
        writer.Append(" = ");
        AppendTargetColumn(writer, subjectColumn);
        writer.Append($" AND {authAlias}.");
        writer.AppendQuoted(checkSpec.AuthObject.ClaimEducationOrganizationIdColumn.Value);
        AppendClaimFilterSql(writer, authorizationClaimParameterization);
    }

    private static void AppendFailedSubjectsCte(SqlWriter writer)
    {
        writer.AppendLine($"{FailedSubjectsCte} AS (");

        using (writer.Indent())
        {
            writer.AppendLine($"SELECT * FROM {SubjectFailuresCte}");
            writer.Append("WHERE ");
            writer.AppendQuoted(FailedColumn);
            writer.AppendLine(" = 1");
        }

        writer.Append(")");
    }

    private void AppendFailurePayloadCte(SqlWriter writer, int emittedAuth1Index)
    {
        writer.AppendLine($"{FailurePayloadCte} AS (");

        using (writer.Indent())
        {
            writer.Append("SELECT ");

            switch (_dialect)
            {
                case SqlDialect.Pgsql:
                    AppendPostgresqlFailurePayloadSql(writer, emittedAuth1Index);
                    break;
                case SqlDialect.Mssql:
                    AppendMssqlFailurePayloadSql(writer, emittedAuth1Index);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Single-record relationship authorization SQL does not support SQL dialect '{_dialect}'."
                    );
            }

            writer.Append(" AS ");
            writer.AppendQuoted(PayloadColumn);
            writer.AppendLine();
            writer.AppendLine($"FROM {FailedSubjectsCte}");
        }

        writer.Append(")");
    }

    private void AppendFinalSelect(SqlWriter writer, int strategyCount)
    {
        writer.AppendLine("SELECT CASE");

        using (writer.Indent())
        {
            writer.Append("WHEN ");
            AppendAnyStrategyAuthorizedSql(writer, strategyCount);
            writer.AppendLine(" THEN 1");
            writer.Append("ELSE ");
            AppendAuth1AbortSql(writer);
            writer.AppendLine();
        }

        writer.Append("END AS ");
        writer.AppendQuoted(AuthorizationResultColumn);
        writer.AppendLine(",");
        writer.AppendQuoted(ContentVersionColumn);
        writer.AppendLine();
        writer.AppendLine($"FROM {TargetCte};");
    }

    private static void AppendPostgresqlFailurePayloadSql(SqlWriter writer, int emittedAuth1Index)
    {
        writer
            .Append("CONCAT('")
            .Append(RelationshipAuthorizationAuth1FailurePayloadCodec.PayloadVersion)
            .Append("|', '")
            .Append(emittedAuth1Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("|', COUNT(1)::text, '|', STRING_AGG(CONCAT(");
        writer.AppendQuoted(StrategyOrdinalColumn);
        writer.Append(", ':', ");
        writer.AppendQuoted(SubjectOrdinalColumn);
        writer.Append(", ':', ");
        writer.AppendQuoted(FailureKindColumn);
        writer.Append("), ',' ORDER BY ");
        writer.AppendQuoted(StrategyOrdinalColumn);
        writer.Append(", ");
        writer.AppendQuoted(SubjectOrdinalColumn);
        writer.Append("))");
    }

    private static void AppendMssqlFailurePayloadSql(SqlWriter writer, int emittedAuth1Index)
    {
        writer
            .Append("CONCAT('")
            .Append(RelationshipAuthorizationAuth1FailurePayloadCodec.PayloadVersion)
            .Append("|', '")
            .Append(emittedAuth1Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("|', COUNT(1), '|', STRING_AGG(CONCAT(");
        writer.AppendQuoted(StrategyOrdinalColumn);
        writer.Append(", ':', ");
        writer.AppendQuoted(SubjectOrdinalColumn);
        writer.Append(", ':', ");
        writer.AppendQuoted(FailureKindColumn);
        writer.Append("), ',') WITHIN GROUP (ORDER BY ");
        writer.AppendQuoted(StrategyOrdinalColumn);
        writer.Append(", ");
        writer.AppendQuoted(SubjectOrdinalColumn);
        writer.Append("))");
    }

    private static void AppendAnyStrategyAuthorizedSql(SqlWriter writer, int strategyCount)
    {
        for (var strategyOrdinal = 0; strategyOrdinal < strategyCount; strategyOrdinal++)
        {
            if (strategyOrdinal > 0)
            {
                writer.Append(" OR ");
            }

            writer.Append("NOT EXISTS (SELECT 1 FROM ");
            writer.Append(FailedSubjectsCte);
            writer.Append(" WHERE ");
            writer.AppendQuoted(StrategyOrdinalColumn);
            writer.Append(" = ");
            writer.Append(strategyOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Append(")");
        }
    }

    private void AppendAuth1AbortSql(SqlWriter writer)
    {
        switch (_dialect)
        {
            case SqlDialect.Pgsql:
                writer.AppendQuoted("dms");
                writer.Append(".");
                writer.AppendQuoted("throw_error");
                writer.Append("('");
                writer.Append(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append("', (SELECT ");
                writer.AppendQuoted(PayloadColumn);
                writer.Append(" FROM ");
                writer.Append(FailurePayloadCte);
                writer.Append("))");
                return;

            case SqlDialect.Mssql:
                writer.Append("CAST(CONCAT('");
                writer.Append(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
                writer.Append(" - ', (SELECT ");
                writer.AppendQuoted(PayloadColumn);
                writer.Append(" FROM ");
                writer.Append(FailurePayloadCte);
                writer.Append(")) AS INT)");
                return;

            default:
                throw new NotSupportedException(
                    $"Single-record relationship authorization SQL does not support SQL dialect '{_dialect}'."
                );
        }
    }

    private static void AppendTargetColumn(SqlWriter writer, DbColumnName column)
    {
        writer.Append($"{TargetCte}.");
        writer.AppendQuoted(column.Value);
    }

    private static void AppendClaimFilterSql(
        SqlWriter writer,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        switch (authorizationClaimParameterization.Kind)
        {
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
                writer.Append(" = ANY(");
                writer.AppendParameter(authorizationClaimParameterization.BaseParameterName);
                writer.Append(")");
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar:
                writer.Append(" IN (");

                for (
                    var parameterIndex = 0;
                    parameterIndex < authorizationClaimParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    if (parameterIndex > 0)
                    {
                        writer.Append(", ");
                    }

                    writer.AppendParameter(
                        authorizationClaimParameterization.ParameterNamesInOrder[parameterIndex]
                    );
                }

                writer.Append(")");
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                writer.Append(" IN (SELECT ");
                writer.AppendQuoted(
                    AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                );
                writer.Append(" FROM ");
                writer.AppendParameter(authorizationClaimParameterization.BaseParameterName);
                writer.Append(")");
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authorizationClaimParameterization),
                    authorizationClaimParameterization.Kind,
                    "Unsupported authorization claim EdOrg parameterization kind."
                );
        }
    }

    private void ValidateAuthorizationClaimParameterization(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        PlanSqlWriterExtensions.ValidateBareParameterName(
            authorizationClaimParameterization.BaseParameterName,
            $"{nameof(SingleRecordRelationshipAuthorizationSqlSpec.ClaimEducationOrganizationIdParameterization)}.{nameof(AuthorizationClaimEducationOrganizationIdParameterization.BaseParameterName)}"
        );

        if (authorizationClaimParameterization.ClaimEducationOrganizationIds.Count == 0)
        {
            throw new ArgumentException(
                "Authorization claim EdOrg parameterization requires at least one claim EdOrg id.",
                nameof(authorizationClaimParameterization)
            );
        }

        foreach (var parameterName in authorizationClaimParameterization.ParameterNamesInOrder)
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                parameterName,
                $"{nameof(SingleRecordRelationshipAuthorizationSqlSpec.ClaimEducationOrganizationIdParameterization)}.{nameof(AuthorizationClaimEducationOrganizationIdParameterization.ParameterNamesInOrder)}"
            );
        }

        switch (_dialect)
        {
            case SqlDialect.Pgsql
                when authorizationClaimParameterization.Kind
                    is not AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
                throw CreateAuthorizationClaimParameterizationDialectMismatchException(
                    authorizationClaimParameterization.Kind
                );
            case SqlDialect.Mssql
                when authorizationClaimParameterization.Kind
                    is not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar
                        and not AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                throw CreateAuthorizationClaimParameterizationDialectMismatchException(
                    authorizationClaimParameterization.Kind
                );
            case SqlDialect.Pgsql:
            case SqlDialect.Mssql:
                return;
            default:
                throw new NotSupportedException(
                    $"Single-record relationship authorization SQL does not support SQL dialect '{_dialect}'."
                );
        }
    }

    private static void ValidateParameterNameCollisions(SingleRecordRelationshipAuthorizationSqlSpec spec)
    {
        if (
            spec.ClaimEducationOrganizationIdParameterization.ParameterNamesInOrder.Contains(
                spec.DocumentIdParameterName,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            throw new ArgumentException(
                "DocumentId parameter name must not collide with claim EdOrg authorization parameter names.",
                nameof(spec)
            );
        }
    }

    private ArgumentException CreateAuthorizationClaimParameterizationDialectMismatchException(
        AuthorizationClaimEducationOrganizationIdParameterizationKind kind
    ) =>
        new(
            $"Authorization claim EdOrg parameterization kind '{kind}' is not supported by SQL dialect '{_dialect}'.",
            nameof(kind)
        );
}
