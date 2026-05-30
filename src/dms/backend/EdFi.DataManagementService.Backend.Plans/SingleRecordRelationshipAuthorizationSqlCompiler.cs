// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles relationship authorization SQL for one stored or proposed root record.
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
    private const string ProposedValueParameterPrefix = "relationshipAuthorization";
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly string[] _defaultReservedParameterNames = ["documentUuid", "resourceKeyId"];
    private static readonly string _pgsqlEdOrgSubjectValueSqlType = ResolvePgsqlEdOrgSubjectValueSqlType();

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);

    private sealed record NormalizedSqlSpec(
        SingleRecordRelationshipAuthorizationSqlSpec Spec,
        RelationshipAuthorizationCheckTarget CheckTarget,
        IReadOnlyList<RelationshipAuthorizationProposedValueSqlParameter> ProposedValueParametersInOrder
    );

    public SingleRecordRelationshipAuthorizationSqlPlan Compile(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        ArgumentNullException.ThrowIfNull(spec);

        var normalizedSpec = NormalizeSpec(spec);
        var parametersInOrder = BuildParametersInOrder(normalizedSpec);

        return new SingleRecordRelationshipAuthorizationSqlPlan(
            BuildSql(normalizedSpec),
            parametersInOrder,
            normalizedSpec.ProposedValueParametersInOrder
        );
    }

    private NormalizedSqlSpec NormalizeSpec(SingleRecordRelationshipAuthorizationSqlSpec spec)
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

        if (spec.CheckSpecs.Any(static checkSpec => checkSpec.Subjects.Count == 0))
        {
            throw new ArgumentException(
                "Single-record relationship authorization check specs require at least one subject.",
                nameof(spec)
            );
        }

        RelationshipAuthorizationEndpointExecutionBoundary.ThrowIfUnsupportedForSingleRecordSql(
            spec.CheckSpecs
        );
        var rootTarget = NormalizeCheckTargets(spec);

        ValidateSubjectRootTables(spec.CheckSpecs, rootTarget);

        ValidateAuthorizationClaimParameterization(spec.ClaimEducationOrganizationIdParameterization);
        var proposedValueParametersInOrder =
            rootTarget is RelationshipAuthorizationCheckTarget.Proposed
                ? BuildProposedValueParametersInOrder(spec)
                : [];
        ValidateParameterNameCollisions(spec, proposedValueParametersInOrder);

        return new NormalizedSqlSpec(spec, rootTarget, proposedValueParametersInOrder);
    }

    private static RelationshipAuthorizationCheckTarget NormalizeCheckTargets(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        var storedTargets = spec
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .OfType<RelationshipAuthorizationCheckTarget.Stored>()
            .ToArray();
        var proposedTargets = spec
            .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
            .OfType<RelationshipAuthorizationCheckTarget.Proposed>()
            .ToArray();

        if (
            storedTargets.Length == spec.CheckSpecs.Count
            && spec.CheckSpecs.All(static checkSpec =>
                checkSpec.ValueSource is RelationshipAuthorizationValueSource.Stored
            )
        )
        {
            return NormalizeStoredTargets(storedTargets);
        }

        if (
            proposedTargets.Length == spec.CheckSpecs.Count
            && spec.CheckSpecs.All(static checkSpec =>
                checkSpec.ValueSource is RelationshipAuthorizationValueSource.Proposed
            )
        )
        {
            return NormalizeProposedTargets(spec, proposedTargets);
        }

        throw new ArgumentException(
            "Single-record relationship authorization check specs must be all stored-value or all proposed-value; mixed batches are not supported.",
            nameof(spec)
        );
    }

    private static RelationshipAuthorizationCheckTarget.Stored NormalizeStoredTargets(
        IReadOnlyList<RelationshipAuthorizationCheckTarget.Stored> storedTargets
    )
    {
        var rootTarget = storedTargets[0];

        if (
            storedTargets.Any(target =>
                !target.RootTable.Equals(rootTarget.RootTable)
                || !target.DocumentIdColumn.Equals(rootTarget.DocumentIdColumn)
            )
        )
        {
            throw new ArgumentException(
                "Single-record relationship authorization stored check specs must share one root target.",
                nameof(storedTargets)
            );
        }

        return rootTarget;
    }

    private static RelationshipAuthorizationCheckTarget.Proposed NormalizeProposedTargets(
        SingleRecordRelationshipAuthorizationSqlSpec spec,
        IReadOnlyList<RelationshipAuthorizationCheckTarget.Proposed> proposedTargets
    )
    {
        var rootTarget = proposedTargets[0];

        if (proposedTargets.Any(target => !target.RootTable.Equals(rootTarget.RootTable)))
        {
            throw new ArgumentException(
                "Single-record relationship authorization proposed check specs must share one root target.",
                nameof(spec)
            );
        }

        for (var strategyOrdinal = 0; strategyOrdinal < spec.CheckSpecs.Count; strategyOrdinal++)
        {
            var checkSpec = spec.CheckSpecs[strategyOrdinal];
            var proposedTarget = proposedTargets[strategyOrdinal];

            if (proposedTarget.SubjectBindingsInOrder.Count != checkSpec.Subjects.Count)
            {
                throw new ArgumentException(
                    $"Proposed relationship authorization check spec '{strategyOrdinal}' has {checkSpec.Subjects.Count} subjects but {proposedTarget.SubjectBindingsInOrder.Count} root bindings.",
                    nameof(spec)
                );
            }

            for (var subjectOrdinal = 0; subjectOrdinal < checkSpec.Subjects.Count; subjectOrdinal++)
            {
                var subject = checkSpec.Subjects[subjectOrdinal];
                var binding = proposedTarget.SubjectBindingsInOrder[subjectOrdinal];

                if (!binding.Table.Equals(rootTarget.RootTable))
                {
                    throw new ArgumentException(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets table '{binding.Table}', but the root target is '{rootTarget.RootTable}'.",
                        nameof(spec)
                    );
                }

                if (!binding.Column.Equals(subject.Column))
                {
                    throw new ArgumentException(
                        $"Proposed relationship authorization binding '{strategyOrdinal}:{subjectOrdinal}' targets column '{binding.Column.Value}', but the subject targets column '{subject.Column.Value}'.",
                        nameof(spec)
                    );
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(binding.ParameterSeed);
            }
        }

        return rootTarget;
    }

    private static string ResolvePgsqlEdOrgSubjectValueSqlType()
    {
        var sourceColumn = ResolveAuthEdOrgTableColumn(AuthNames.SourceEdOrgId);
        var targetColumn = ResolveAuthEdOrgTableColumn(AuthNames.TargetEdOrgId);

        if (!StringComparer.OrdinalIgnoreCase.Equals(sourceColumn.SqlType, targetColumn.SqlType))
        {
            throw new InvalidOperationException(
                $"Auth EdOrg hierarchy columns '{sourceColumn.Name.Value}' and '{targetColumn.Name.Value}' must have the same SQL type."
            );
        }

        return sourceColumn.SqlType;
    }

    private static AuthTableColumn ResolveAuthEdOrgTableColumn(DbColumnName columnName)
    {
        var columns = AuthObjectDefinitions
            .AuthEdOrgTable.Columns.Where(column => column.Name.Equals(columnName))
            .Take(1)
            .ToArray();

        if (columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"Auth EdOrg hierarchy table '{AuthObjectDefinitions.AuthEdOrgTable.Table}' does not define column '{columnName.Value}'."
            );
        }

        return columns[0];
    }

    private static DbTableName GetRootTable(RelationshipAuthorizationCheckTarget target) =>
        target switch
        {
            RelationshipAuthorizationCheckTarget.Stored stored => stored.RootTable,
            RelationshipAuthorizationCheckTarget.Proposed proposed => proposed.RootTable,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

    private static void ValidateSubjectRootTables(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        RelationshipAuthorizationCheckTarget rootTarget
    )
    {
        var rootTable = GetRootTable(rootTarget);

        foreach (var subject in checkSpecs.SelectMany(static checkSpec => checkSpec.Subjects))
        {
            if (subject.PersonMetadata is { } personMetadata)
            {
                if (!personMetadata.StoredAnchor.RootTable.Equals(rootTable))
                {
                    throw new ArgumentException(
                        $"People authorization subject root table '{personMetadata.StoredAnchor.RootTable}' does not match root table '{rootTable}'.",
                        nameof(checkSpecs)
                    );
                }

                continue;
            }

            if (!subject.Table.Equals(rootTable))
            {
                throw new ArgumentException(
                    $"Authorization subject table '{subject.Table}' does not match root table '{rootTable}'.",
                    nameof(checkSpecs)
                );
            }
        }
    }

    private static IReadOnlyList<DbColumnName> BuildStoredTargetRootColumns(
        SingleRecordRelationshipAuthorizationSqlSpec spec,
        RelationshipAuthorizationCheckTarget.Stored storedTarget
    ) =>
        [
            .. spec
                .CheckSpecs.SelectMany(static checkSpec => checkSpec.Subjects)
                .SelectMany(subject => GetStoredTargetRootColumns(storedTarget.RootTable, subject))
                .Distinct()
                .OrderBy(static column => column.Value, StringComparer.Ordinal),
        ];

    private static IReadOnlyList<DbColumnName> GetStoredTargetRootColumns(
        DbTableName rootTable,
        RelationshipAuthorizationSubject subject
    )
    {
        if (subject.PersonMetadata is not { } personMetadata)
        {
            return [subject.Column];
        }

        if (!personMetadata.StoredAnchor.RootTable.Equals(rootTable))
        {
            throw new ArgumentException(
                $"People authorization subject root table '{personMetadata.StoredAnchor.RootTable}' does not match root table '{rootTable}'.",
                nameof(subject)
            );
        }

        switch (personMetadata.Path.Kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId:
                ValidateSelfPersonPath(subject, personMetadata);
                return [personMetadata.StoredAnchor.RootDocumentIdColumn];
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn:
                return [GetDirectRootPersonDocumentIdColumn(subject, personMetadata)];
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath:
                ValidateTransitivePersonPath(subject, personMetadata, personMetadata.Path.Steps);
                return [personMetadata.StoredAnchor.RootDocumentIdColumn];
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(subject),
                    personMetadata.Path.Kind,
                    "Unsupported People relationship authorization subject path kind."
                );
        }
    }

    private static void ValidateSelfPersonPath(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata
    )
    {
        if (
            !subject.Table.Equals(personMetadata.StoredAnchor.RootTable)
            || !subject.Column.Equals(personMetadata.StoredAnchor.RootDocumentIdColumn)
        )
        {
            throw new InvalidOperationException(
                $"Self People authorization subject column '{subject.Table}.{subject.Column}' does not match root DocumentId column '{personMetadata.StoredAnchor.RootTable}.{personMetadata.StoredAnchor.RootDocumentIdColumn}'."
            );
        }
    }

    private static DbColumnName GetDirectRootPersonDocumentIdColumn(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata
    )
    {
        var step = personMetadata.Path.Steps[0];

        if (!step.SourceTable.Equals(personMetadata.StoredAnchor.RootTable))
        {
            throw new InvalidOperationException(
                $"Direct People authorization subject table '{step.SourceTable}' does not match root table '{personMetadata.StoredAnchor.RootTable}'."
            );
        }

        if (!subject.Table.Equals(step.SourceTable) || !subject.Column.Equals(step.SourceColumnName))
        {
            throw new InvalidOperationException(
                $"People authorization subject column '{subject.Table}.{subject.Column}' does not match path root column '{step.SourceTable}.{step.SourceColumnName}'."
            );
        }

        return step.SourceColumnName;
    }

    private static void ValidateTransitivePersonPath(
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        IReadOnlyList<ColumnPathStep> pathSteps
    )
    {
        var expectedSourceTable = personMetadata.StoredAnchor.RootTable;

        for (var stepIndex = 0; stepIndex < pathSteps.Count - 1; stepIndex++)
        {
            var step = pathSteps[stepIndex];

            if (!step.SourceTable.Equals(expectedSourceTable))
            {
                throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex} source table '{step.SourceTable}' does not match expected table '{expectedSourceTable}'."
                );
            }

            var targetTable =
                step.TargetTable
                ?? throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex} is missing a target table."
                );

            if (!pathSteps[stepIndex + 1].SourceTable.Equals(targetTable))
            {
                throw new InvalidOperationException(
                    $"Transitive People authorization path step {stepIndex + 1} source table '{pathSteps[stepIndex + 1].SourceTable}' does not match previous target table '{targetTable}'."
                );
            }

            expectedSourceTable = targetTable;
        }

        var terminalStep = pathSteps[^1];

        if (!terminalStep.SourceTable.Equals(expectedSourceTable))
        {
            throw new InvalidOperationException(
                $"Transitive People authorization terminal source table '{terminalStep.SourceTable}' does not match expected table '{expectedSourceTable}'."
            );
        }

        if (
            !subject.Table.Equals(terminalStep.SourceTable)
            || !subject.Column.Equals(terminalStep.SourceColumnName)
        )
        {
            throw new InvalidOperationException(
                $"People authorization subject column '{subject.Table}.{subject.Column}' does not match transitive terminal path column '{terminalStep.SourceTable}.{terminalStep.SourceColumnName}'."
            );
        }
    }

    private static IReadOnlyList<QuerySqlParameter> BuildParametersInOrder(NormalizedSqlSpec normalizedSpec)
    {
        List<QuerySqlParameter> parameters = [];

        if (normalizedSpec.CheckTarget is RelationshipAuthorizationCheckTarget.Stored)
        {
            parameters.Add(
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    normalizedSpec.Spec.DocumentIdParameterName
                )
            );
        }

        parameters.AddRange(
            normalizedSpec.ProposedValueParametersInOrder.Select(
                static proposedParameter => new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    proposedParameter.ParameterName
                )
            )
        );

        parameters.AddRange(
            AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder(
                normalizedSpec.Spec.ClaimEducationOrganizationIdParameterization
            )
        );

        return parameters;
    }

    private static IReadOnlyList<RelationshipAuthorizationProposedValueSqlParameter> BuildProposedValueParametersInOrder(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        var usedParameterNames = BuildReservedParameterNameSet(spec);
        List<RelationshipAuthorizationProposedValueSqlParameter> proposedValueParameters = [];

        for (var strategyOrdinal = 0; strategyOrdinal < spec.CheckSpecs.Count; strategyOrdinal++)
        {
            var proposedTarget = (RelationshipAuthorizationCheckTarget.Proposed)
                spec.CheckSpecs[strategyOrdinal].CheckTarget;

            for (
                var subjectOrdinal = 0;
                subjectOrdinal < proposedTarget.SubjectBindingsInOrder.Count;
                subjectOrdinal++
            )
            {
                var binding = proposedTarget.SubjectBindingsInOrder[subjectOrdinal];
                var candidate = PlanNamingConventions.SanitizeBareParameterName(
                    $"{ProposedValueParameterPrefix}_{strategyOrdinal}_{subjectOrdinal}_{binding.ParameterSeed}"
                );
                var parameterName = AllocateParameterName(candidate, usedParameterNames);

                proposedValueParameters.Add(
                    new RelationshipAuthorizationProposedValueSqlParameter(
                        strategyOrdinal,
                        subjectOrdinal,
                        parameterName
                    )
                );
            }
        }

        return proposedValueParameters;
    }

    private static HashSet<string> BuildReservedParameterNameSet(
        SingleRecordRelationshipAuthorizationSqlSpec spec
    )
    {
        HashSet<string> usedParameterNames = new(StringComparer.OrdinalIgnoreCase);

        AddReservedParameterName(usedParameterNames, spec.DocumentIdParameterName);

        foreach (var parameterName in _defaultReservedParameterNames)
        {
            AddReservedParameterName(usedParameterNames, parameterName);
        }

        foreach (var parameterName in spec.ReservedParameterNames ?? Array.Empty<string>())
        {
            AddReservedParameterName(usedParameterNames, parameterName);
        }

        AddReservedParameterName(
            usedParameterNames,
            spec.ClaimEducationOrganizationIdParameterization.BaseParameterName
        );

        foreach (var parameterName in spec.ClaimEducationOrganizationIdParameterization.ParameterNamesInOrder)
        {
            AddReservedParameterName(usedParameterNames, parameterName);
        }

        foreach (
            var binding in spec
                .CheckSpecs.Select(static checkSpec => checkSpec.CheckTarget)
                .OfType<RelationshipAuthorizationCheckTarget.Proposed>()
                .SelectMany(static target => target.SubjectBindingsInOrder)
        )
        {
            AddReservedParameterName(usedParameterNames, binding.ParameterSeed);
        }

        return usedParameterNames;
    }

    private static void AddReservedParameterName(HashSet<string> usedParameterNames, string parameterName)
    {
        PlanSqlWriterExtensions.ValidateBareParameterName(parameterName, nameof(parameterName));
        usedParameterNames.Add(parameterName);
    }

    private static string AllocateParameterName(string candidate, HashSet<string> usedParameterNames)
    {
        if (usedParameterNames.Add(candidate))
        {
            return candidate;
        }

        var suffix = 2;
        var nextCandidate = $"{candidate}_{suffix}";

        while (!usedParameterNames.Add(nextCandidate))
        {
            suffix++;
            nextCandidate = $"{candidate}_{suffix}";
        }

        return nextCandidate;
    }

    private string BuildSql(NormalizedSqlSpec normalizedSpec)
    {
        return normalizedSpec.CheckTarget switch
        {
            RelationshipAuthorizationCheckTarget.Stored storedTarget => BuildStoredSql(
                normalizedSpec.Spec,
                storedTarget
            ),
            RelationshipAuthorizationCheckTarget.Proposed => BuildProposedSql(normalizedSpec),
            _ => throw new ArgumentOutOfRangeException(
                nameof(normalizedSpec),
                normalizedSpec.CheckTarget,
                null
            ),
        };
    }

    private string BuildStoredSql(
        SingleRecordRelationshipAuthorizationSqlSpec spec,
        RelationshipAuthorizationCheckTarget.Stored storedTarget
    )
    {
        var writer = new SqlWriter(_sqlDialect);
        var rootColumns = BuildStoredTargetRootColumns(spec, storedTarget);

        AppendTargetCte(writer, storedTarget, rootColumns, spec.DocumentIdParameterName);
        writer.AppendLine(",");
        AppendSubjectFailuresCte(writer, spec);
        writer.AppendLine(",");
        AppendFailedSubjectsCte(writer);
        writer.AppendLine(",");
        AppendFailurePayloadCte(writer, spec.EmittedAuth1Index);
        writer.AppendLine();
        AppendFinalSelect(writer, spec.CheckSpecs.Count, includeContentVersion: true, fromTarget: true);

        return writer.ToString();
    }

    private string BuildProposedSql(NormalizedSqlSpec normalizedSpec)
    {
        var writer = new SqlWriter(_sqlDialect);

        writer.AppendLine("WITH");
        AppendProposedSubjectFailuresCte(writer, normalizedSpec);
        writer.AppendLine(",");
        AppendFailedSubjectsCte(writer);
        writer.AppendLine(",");
        AppendFailurePayloadCte(writer, normalizedSpec.Spec.EmittedAuth1Index);
        writer.AppendLine();
        AppendFinalSelect(
            writer,
            normalizedSpec.Spec.CheckSpecs.Count,
            includeContentVersion: false,
            fromTarget: false
        );

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

        if (subject.PersonMetadata is { } personMetadata)
        {
            AppendStoredPeopleSubjectFailureSelect(
                writer,
                strategyOrdinal,
                subjectOrdinal,
                subject,
                personMetadata,
                authorizationClaimParameterization
            );
            return;
        }

        AppendStoredEdOrgSubjectFailureSelect(
            writer,
            strategyOrdinal,
            subjectOrdinal,
            checkSpec,
            authorizationClaimParameterization
        );
    }

    private static void AppendStoredEdOrgSubjectFailureSelect(
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
            writer.Append(" IS NULL OR NOT ");
            AppendAuthorizationSuccessSql(
                writer,
                subject.AuthObject,
                subjectValueWriter => AppendTargetColumn(subjectValueWriter, subject.Column),
                authorizationClaimParameterization,
                authAlias
            );
            writer.AppendLine(" THEN 1 ELSE 0 END");
        }

        writer.AppendLine();
        writer.AppendLine($"FROM {TargetCte}");
    }

    private static void AppendStoredPeopleSubjectFailureSelect(
        SqlWriter writer,
        int strategyOrdinal,
        int subjectOrdinal,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        var authAlias = $"a{strategyOrdinal}_{subjectOrdinal}";

        writer.AppendLine("SELECT");

        using (writer.Indent())
        {
            writer.AppendLine($"{strategyOrdinal},");
            writer.AppendLine($"{subjectOrdinal},");
            writer.Append("CASE WHEN ");
            AppendStoredPeopleSubjectInvalidDataSql(
                writer,
                subject,
                personMetadata,
                strategyOrdinal,
                subjectOrdinal
            );
            writer.Append(" THEN 's' ELSE 'n' END,");
            writer.AppendLine();
            writer.Append("CASE WHEN NOT ");
            AppendStoredPeopleAuthorizationSuccessSql(
                writer,
                subject,
                personMetadata,
                authorizationClaimParameterization,
                authAlias,
                strategyOrdinal,
                subjectOrdinal
            );
            writer.AppendLine(" THEN 1 ELSE 0 END");
        }

        writer.AppendLine();
        writer.AppendLine($"FROM {TargetCte}");
    }

    private static void AppendStoredPeopleSubjectInvalidDataSql(
        SqlWriter writer,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        int strategyOrdinal,
        int subjectOrdinal
    )
    {
        switch (personMetadata.Path.Kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId:
                AppendTargetColumn(writer, personMetadata.StoredAnchor.RootDocumentIdColumn);
                writer.Append(" IS NULL");
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn:
                AppendTargetColumn(writer, GetDirectRootPersonDocumentIdColumn(subject, personMetadata));
                writer.Append(" IS NULL");
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath:
                writer.Append("NOT EXISTS (");
                AppendStoredTransitivePersonPathSelectSql(
                    writer,
                    subject,
                    personMetadata,
                    strategyOrdinal,
                    subjectOrdinal,
                    appendMembershipCheck: null
                );
                writer.Append(")");
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(personMetadata),
                    personMetadata.Path.Kind,
                    "Unsupported People relationship authorization subject path kind."
                );
        }
    }

    private static void AppendStoredPeopleAuthorizationSuccessSql(
        SqlWriter writer,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias,
        int strategyOrdinal,
        int subjectOrdinal
    )
    {
        switch (personMetadata.Path.Kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId:
                writer.Append("EXISTS (");
                AppendAuthorizationExistsSelectSql(
                    writer,
                    subject.AuthObject,
                    subjectValueWriter =>
                        AppendTargetColumn(
                            subjectValueWriter,
                            personMetadata.StoredAnchor.RootDocumentIdColumn
                        ),
                    authorizationClaimParameterization,
                    authAlias
                );
                writer.Append(")");
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn:
                writer.Append("EXISTS (");
                AppendAuthorizationExistsSelectSql(
                    writer,
                    subject.AuthObject,
                    subjectValueWriter =>
                        AppendTargetColumn(
                            subjectValueWriter,
                            GetDirectRootPersonDocumentIdColumn(subject, personMetadata)
                        ),
                    authorizationClaimParameterization,
                    authAlias
                );
                writer.Append(")");
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath:
                writer.Append("EXISTS (");
                AppendStoredTransitivePersonPathSelectSql(
                    writer,
                    subject,
                    personMetadata,
                    strategyOrdinal,
                    subjectOrdinal,
                    terminalValueWriter =>
                    {
                        writer.Append("EXISTS (");
                        AppendAuthorizationExistsSelectSql(
                            writer,
                            subject.AuthObject,
                            terminalValueWriter,
                            authorizationClaimParameterization,
                            authAlias
                        );
                        writer.Append(")");
                    }
                );
                writer.Append(")");
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(personMetadata),
                    personMetadata.Path.Kind,
                    "Unsupported People relationship authorization subject path kind."
                );
        }
    }

    private static void AppendStoredTransitivePersonPathSelectSql(
        SqlWriter writer,
        RelationshipAuthorizationSubject subject,
        RelationshipAuthorizationPersonSubjectMetadata personMetadata,
        int strategyOrdinal,
        int subjectOrdinal,
        Action<Action<SqlWriter>>? appendMembershipCheck
    )
    {
        var pathSteps = personMetadata.Path.Steps;
        ValidateTransitivePersonPath(subject, personMetadata, pathSteps);

        var rootAlias = $"p{strategyOrdinal}_{subjectOrdinal}_0";
        var pathJoinAliases = Enumerable
            .Range(1, pathSteps.Count - 1)
            .Select(index => $"p{strategyOrdinal}_{subjectOrdinal}_{index}")
            .ToArray();

        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(personMetadata.StoredAnchor.RootTable));
        writer.Append($" {rootAlias}");

        var currentSourceAlias = rootAlias;

        for (var stepIndex = 0; stepIndex < pathSteps.Count - 1; stepIndex++)
        {
            var step = pathSteps[stepIndex];
            var targetTable =
                step.TargetTable
                ?? throw new InvalidOperationException(
                    "Transitive People authorization path steps must include a target table for intermediate joins."
                );
            var targetColumn =
                step.TargetColumnName
                ?? throw new InvalidOperationException(
                    "Transitive People authorization path steps must include a target column for intermediate joins."
                );
            var joinAlias = pathJoinAliases[stepIndex];

            writer.Append(" JOIN ");
            writer.AppendRelation(new SqlRelationRef.PhysicalTable(targetTable));
            writer.Append($" {joinAlias} ON {joinAlias}.");
            writer.AppendQuoted(targetColumn.Value);
            writer.Append($" = {currentSourceAlias}.");
            writer.AppendQuoted(step.SourceColumnName.Value);

            currentSourceAlias = joinAlias;
        }

        var terminalStep = pathSteps[^1];

        writer.Append($" WHERE {rootAlias}.");
        writer.AppendQuoted(personMetadata.StoredAnchor.RootDocumentIdColumn.Value);
        writer.Append(" = ");
        AppendTargetColumn(writer, personMetadata.StoredAnchor.RootDocumentIdColumn);
        writer.Append($" AND {currentSourceAlias}.");
        writer.AppendQuoted(terminalStep.SourceColumnName.Value);
        writer.Append(" IS NOT NULL");

        if (appendMembershipCheck is null)
        {
            return;
        }

        writer.Append(" AND ");
        appendMembershipCheck(terminalValueWriter =>
        {
            terminalValueWriter.Append($"{currentSourceAlias}.");
            terminalValueWriter.AppendQuoted(terminalStep.SourceColumnName.Value);
        });
    }

    private static void AppendProposedSubjectFailuresCte(SqlWriter writer, NormalizedSqlSpec normalizedSpec)
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

            for (
                var strategyOrdinal = 0;
                strategyOrdinal < normalizedSpec.Spec.CheckSpecs.Count;
                strategyOrdinal++
            )
            {
                var checkSpec = normalizedSpec.Spec.CheckSpecs[strategyOrdinal];

                for (var subjectOrdinal = 0; subjectOrdinal < checkSpec.Subjects.Count; subjectOrdinal++)
                {
                    if (emittedSubjectIndex > 0)
                    {
                        writer.AppendLine("UNION ALL");
                    }

                    var proposedValueParameter = normalizedSpec.ProposedValueParametersInOrder[
                        emittedSubjectIndex
                    ];

                    AppendProposedSubjectFailureSelect(
                        writer,
                        strategyOrdinal,
                        subjectOrdinal,
                        checkSpec,
                        proposedValueParameter.ParameterName,
                        normalizedSpec.Spec.ClaimEducationOrganizationIdParameterization
                    );
                    emittedSubjectIndex++;
                }
            }
        }

        writer.Append(")");
    }

    private static void AppendProposedSubjectFailureSelect(
        SqlWriter writer,
        int strategyOrdinal,
        int subjectOrdinal,
        RelationshipAuthorizationCheckSpec checkSpec,
        string proposedValueParameterName,
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
            AppendProposedSubjectValueSql(writer, proposedValueParameterName);
            writer.Append(" IS NULL THEN 'p' ELSE 'n' END,");
            writer.AppendLine();
            writer.Append("CASE WHEN ");
            AppendProposedSubjectValueSql(writer, proposedValueParameterName);
            writer.Append(" IS NULL OR NOT ");
            AppendAuthorizationSuccessSql(
                writer,
                subject.AuthObject,
                subjectValueWriter =>
                    AppendProposedSubjectValueSql(subjectValueWriter, proposedValueParameterName),
                authorizationClaimParameterization,
                authAlias
            );
            writer.AppendLine(" THEN 1 ELSE 0 END");
        }

        writer.AppendLine();
    }

    private static void AppendProposedSubjectValueSql(SqlWriter writer, string proposedValueParameterName)
    {
        if (writer.Dialect.Rules.Dialect is SqlDialect.Pgsql)
        {
            writer.Append("CAST(");
            writer.AppendParameter(proposedValueParameterName);
            writer.Append(" AS ");
            writer.Append(_pgsqlEdOrgSubjectValueSqlType);
            writer.Append(")");
            return;
        }

        writer.AppendParameter(proposedValueParameterName);
    }

    private static void AppendAuthorizationSuccessSql(
        SqlWriter writer,
        RelationshipAuthorizationAuthObject authObject,
        Action<SqlWriter> appendSubjectValue,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        if (authObject.AllowsDirectClaimMatch)
        {
            writer.Append("(");
            appendSubjectValue(writer);
            AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
                writer,
                authorizationClaimParameterization
            );
            writer.Append(" OR EXISTS (");
            AppendAuthorizationExistsSelectSql(
                writer,
                authObject,
                appendSubjectValue,
                authorizationClaimParameterization,
                authAlias
            );
            writer.Append("))");
            return;
        }

        writer.Append("EXISTS (");
        AppendAuthorizationExistsSelectSql(
            writer,
            authObject,
            appendSubjectValue,
            authorizationClaimParameterization,
            authAlias
        );
        writer.Append(")");
    }

    private static void AppendAuthorizationExistsSelectSql(
        SqlWriter writer,
        RelationshipAuthorizationAuthObject authObject,
        Action<SqlWriter> appendSubjectValue,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        writer.Append("SELECT 1 FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(authObject.Name));
        writer.Append($" {authAlias} WHERE {authAlias}.");
        writer.AppendQuoted(authObject.SubjectValueColumn.Value);
        writer.Append(" = ");
        appendSubjectValue(writer);
        writer.Append($" AND {authAlias}.");
        writer.AppendQuoted(authObject.ClaimEducationOrganizationIdColumn.Value);
        AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
            writer,
            authorizationClaimParameterization
        );
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

    private void AppendFinalSelect(
        SqlWriter writer,
        int strategyCount,
        bool includeContentVersion,
        bool fromTarget
    )
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

        if (includeContentVersion)
        {
            writer.AppendLine(",");
            writer.AppendQuoted(ContentVersionColumn);
        }

        writer.AppendLine();

        if (fromTarget)
        {
            writer.AppendLine($"FROM {TargetCte};");
            return;
        }

        writer.AppendLine(";");
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

    private void ValidateAuthorizationClaimParameterization(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        AuthorizationClaimEducationOrganizationIdParameterizationValidator.ValidateOrThrow(
            authorizationClaimParameterization,
            _dialect,
            nameof(SingleRecordRelationshipAuthorizationSqlSpec.ClaimEducationOrganizationIdParameterization),
            "Single-record relationship authorization SQL"
        );
    }

    private static void ValidateParameterNameCollisions(
        SingleRecordRelationshipAuthorizationSqlSpec spec,
        IReadOnlyList<RelationshipAuthorizationProposedValueSqlParameter> proposedValueParametersInOrder
    )
    {
        foreach (var parameterName in spec.ReservedParameterNames ?? Array.Empty<string>())
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                parameterName,
                nameof(SingleRecordRelationshipAuthorizationSqlSpec.ReservedParameterNames)
            );
        }

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

        var duplicateProposedParameter = proposedValueParametersInOrder
            .GroupBy(static parameter => parameter.ParameterName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicateProposedParameter is not null)
        {
            throw new ArgumentException(
                $"Proposed relationship authorization parameter name '{duplicateProposedParameter.Key}' is allocated more than once.",
                nameof(spec)
            );
        }

        var reservedParameterNames = BuildReservedParameterNameSet(spec);

        foreach (
            var proposedParameterName in proposedValueParametersInOrder.Select(static parameter =>
                parameter.ParameterName
            )
        )
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(
                proposedParameterName,
                nameof(proposedValueParametersInOrder)
            );

            if (reservedParameterNames.Contains(proposedParameterName))
            {
                throw new ArgumentException(
                    $"Proposed relationship authorization parameter name '{proposedParameterName}' collides with a reserved parameter name.",
                    nameof(spec)
                );
            }
        }
    }
}
