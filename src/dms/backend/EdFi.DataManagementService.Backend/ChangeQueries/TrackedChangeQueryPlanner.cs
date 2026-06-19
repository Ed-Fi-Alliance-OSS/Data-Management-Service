// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal sealed class TrackedChangeQueryPlanner(SqlDialect dialect)
{
    private const string MinChangeVersionParameterName = "@MinChangeVersion";
    private const string MaxChangeVersionParameterName = "@MaxChangeVersion";
    private const string LimitParameterName = "@Limit";
    private const string OffsetParameterName = "@Offset";
    private const string DiscriminatorParameterName = "@Discriminator";
    private const string QualifiedDiscriminatorParameterName = "@QualifiedDiscriminator";
    private const string DescriptorDiscriminatorParameterPrefix = "@DescriptorDiscriminator";
    private const string DescriptorDiscriminatorQualifiedParameterPrefix =
        "@DescriptorDiscriminatorQualified";

    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _descriptorNamespaceColumn = new("Namespace");
    private static readonly DbColumnName _descriptorCodeValueColumn = new("CodeValue");
    private static readonly DbColumnName _descriptorDiscriminatorColumn = new("Discriminator");

    private readonly SqlDialect _dialect = dialect;

    public TrackedChangeQueryPlan Plan(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fields);

        return request.Operation switch
        {
            ChangeQueryEndpointOperation.Deletes => BuildDeletesPlan(request, fields),
            ChangeQueryEndpointOperation.KeyChanges when IsEmptyKeyChangesTable(request.TrackedChangeTable) =>
                TrackedChangeQueryPlan.Empty(request.PaginationParameters.TotalCount ? 0 : null),
            ChangeQueryEndpointOperation.KeyChanges => BuildKeyChangesPlan(request, fields),
            _ => throw new InvalidOperationException(
                $"Unsupported tracked-change query operation '{request.Operation}'."
            ),
        };
    }

    internal static TrackedChangeSystemColumnInfo RequireSystemColumn(
        TrackedChangeTableInfo table,
        TrackedChangeSystemColumnRole role
    )
    {
        ArgumentNullException.ThrowIfNull(table);

        TrackedChangeSystemColumnInfo[] matchingColumns = table
            .SystemColumns.Where(column => column.Role == role)
            .ToArray();
        if (matchingColumns.Length != 1)
        {
            throw new InvalidOperationException(
                $"Tracked-change table '{table.Table}' must have exactly one system column with role "
                    + $"'{role}', but found {matchingColumns.Length}."
            );
        }

        return matchingColumns[0];
    }

    internal static TrackedChangeColumnInfo RequireRepresentativeIdentityColumn(TrackedChangeTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        TrackedChangeColumnInfo? representativeColumn = table.ValueColumnsInTableOrder.FirstOrDefault(
            column =>
                column.Origin.HasFlag(TrackedChangeColumnOrigin.Identity)
                && column.Role is not TrackedChangeColumnRole.PersonDocumentId
        );
        if (representativeColumn is null)
        {
            throw new InvalidOperationException(
                $"Tracked-change table '{table.Table}' must have at least one non-person identity column."
            );
        }

        return representativeColumn;
    }

    private static bool IsEmptyKeyChangesTable(TrackedChangeTableInfo table)
    {
        return table.Kind
            is TrackedChangeTableKind.SharedDescriptor
                or TrackedChangeTableKind.ConcreteAbstract;
    }

    private TrackedChangeQueryPlan BuildDeletesPlan(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields
    )
    {
        PlanColumns columns = ResolvePlanColumns(request.TrackedChangeTable);
        FilteredDeletesSql filteredDeletesSql = BuildFilteredDeletesSql(request, fields, columns);

        string commandText = request.PaginationParameters.TotalCount
            ? $"{BuildDeletesCountSql(filteredDeletesSql)};\n{BuildDeletesPageSql(fields, filteredDeletesSql, columns)}"
            : BuildDeletesPageSql(fields, filteredDeletesSql, columns);

        return new TrackedChangeQueryPlan(
            new RelationalCommand(commandText, BuildDeletesParameters(request, filteredDeletesSql)),
            fields,
            IncludesTotalCount: request.PaginationParameters.TotalCount,
            IsEmpty: false,
            TotalCount: null
        );
    }

    private TrackedChangeQueryPlan BuildKeyChangesPlan(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields
    )
    {
        PlanColumns columns = ResolvePlanColumns(request.TrackedChangeTable);
        string commonCteSql = BuildKeyChangesCommonCteSql(request, columns);
        string commandText = request.PaginationParameters.TotalCount
            ? $"{BuildKeyChangesCountSql(commonCteSql)};\n{BuildKeyChangesPageSql(fields, commonCteSql, columns)}"
            : BuildKeyChangesPageSql(fields, commonCteSql, columns);

        return new TrackedChangeQueryPlan(
            new RelationalCommand(commandText, BuildPagingParameters(request)),
            fields,
            IncludesTotalCount: request.PaginationParameters.TotalCount,
            IsEmpty: false,
            TotalCount: null
        );
    }

    private string BuildKeyChangesCommonCteSql(
        IRelationalTrackedChangeQueryRequest request,
        PlanColumns columns
    )
    {
        TrackedChangeSystemColumnInfo idColumn = columns.IdColumn;
        TrackedChangeSystemColumnInfo changeVersionColumn = columns.ChangeVersionColumn;
        TrackedChangeColumnInfo representativeIdentityColumn = columns.RepresentativeIdentityColumn;

        return $"""
            WITH FilteredChanges AS (
                SELECT c.*
                FROM {Quote(request.TrackedChangeTable.Table)} c
                WHERE c.{Quote(representativeIdentityColumn.NewColumnName)} IS NOT NULL
                  AND ({MinChangeVersionParameterName} IS NULL OR c.{Quote(
                changeVersionColumn.ColumnName
            )} >= {MinChangeVersionParameterName})
                  AND ({MaxChangeVersionParameterName} IS NULL OR c.{Quote(
                changeVersionColumn.ColumnName
            )} <= {MaxChangeVersionParameterName})
            ),
            ChangeWindow AS (
                SELECT c.{Quote(idColumn.ColumnName)},
                       MIN(c.{Quote(changeVersionColumn.ColumnName)}) AS {QuoteIdentifier(
                "__FirstChangeVersion"
            )},
                       MAX(c.{Quote(changeVersionColumn.ColumnName)}) AS {QuoteIdentifier(
                "__LastChangeVersion"
            )}
                FROM FilteredChanges c
                GROUP BY c.{Quote(idColumn.ColumnName)}
            )
            """;
    }

    private string BuildKeyChangesCountSql(string commonCteSql)
    {
        return $"""
            {commonCteSql}
            SELECT COUNT(1) AS {QuoteIdentifier("__TotalCount")}
            FROM ChangeWindow
            """;
    }

    private string BuildKeyChangesPageSql(
        IReadOnlyList<ChangeQueryResponseField> fields,
        string commonCteSql,
        PlanColumns columns
    )
    {
        TrackedChangeSystemColumnInfo idColumn = columns.IdColumn;
        TrackedChangeSystemColumnInfo changeVersionColumn = columns.ChangeVersionColumn;

        List<string> selectExpressions =
        [
            $"firstChange.{Quote(idColumn.ColumnName)} AS {QuoteIdentifier("__Id")}",
            $"w.{QuoteIdentifier("__LastChangeVersion")} AS {QuoteIdentifier("__ChangeVersion")}",
        ];
        selectExpressions.AddRange(fields.SelectMany(BuildKeyChangeSelectExpressions));

        return $"""
            {commonCteSql}
            SELECT {string.Join(",\n       ", selectExpressions)}
            FROM ChangeWindow w
            JOIN FilteredChanges firstChange ON firstChange.{Quote(idColumn.ColumnName)} = w.{Quote(
                idColumn.ColumnName
            )}
                AND firstChange.{Quote(changeVersionColumn.ColumnName)} = w.{QuoteIdentifier(
                "__FirstChangeVersion"
            )}
            JOIN FilteredChanges lastChange ON lastChange.{Quote(idColumn.ColumnName)} = w.{Quote(
                idColumn.ColumnName
            )}
                AND lastChange.{Quote(changeVersionColumn.ColumnName)} = w.{QuoteIdentifier(
                "__LastChangeVersion"
            )}
            ORDER BY w.{QuoteIdentifier("__LastChangeVersion")} ASC
            {BuildPagingSql()}
            """;
    }

    private FilteredDeletesSql BuildFilteredDeletesSql(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields,
        PlanColumns columns
    )
    {
        TrackedChangeSystemColumnInfo changeVersionColumn = columns.ChangeVersionColumn;
        TrackedChangeColumnInfo representativeIdentityColumn = columns.RepresentativeIdentityColumn;

        List<string> joins = [];
        List<string> predicates =
        [
            $"c.{Quote(representativeIdentityColumn.NewColumnName)} IS NULL",
            $"({MinChangeVersionParameterName} IS NULL OR c.{Quote(changeVersionColumn.ColumnName)} >= {MinChangeVersionParameterName})",
            $"({MaxChangeVersionParameterName} IS NULL OR c.{Quote(changeVersionColumn.ColumnName)} <= {MaxChangeVersionParameterName})",
        ];
        List<DescriptorDiscriminatorParameter> descriptorDiscriminatorParameters = [];

        bool usesDiscriminator = IsSharedDescriptorRequest(request);
        if (usesDiscriminator)
        {
            AppendSharedDescriptorDeleteFilters(request, fields, joins, predicates);
        }
        else
        {
            AppendRegularResourceRecreatedSuppression(
                request,
                fields,
                joins,
                predicates,
                descriptorDiscriminatorParameters
            );
        }

        var sqlBuilder = new StringBuilder();
        sqlBuilder.Append("SELECT c.*\n");
        sqlBuilder.Append("FROM ");
        sqlBuilder.Append(Quote(request.TrackedChangeTable.Table));
        sqlBuilder.Append(" c");

        foreach (string join in joins)
        {
            sqlBuilder.Append('\n');
            sqlBuilder.Append(join);
        }

        sqlBuilder.Append("\nWHERE ");
        sqlBuilder.Append(string.Join("\n  AND ", predicates));

        return new FilteredDeletesSql(
            sqlBuilder.ToString(),
            usesDiscriminator,
            descriptorDiscriminatorParameters
        );
    }

    private string BuildDeletesCountSql(FilteredDeletesSql filteredDeletesSql)
    {
        return $"""
            SELECT COUNT(1) AS {QuoteIdentifier("__TotalCount")}
            FROM (
            {Indent(filteredDeletesSql.Sql)}
            ) filtered
            """;
    }

    private string BuildDeletesPageSql(
        IReadOnlyList<ChangeQueryResponseField> fields,
        FilteredDeletesSql filteredDeletesSql,
        PlanColumns columns
    )
    {
        TrackedChangeSystemColumnInfo idColumn = columns.IdColumn;
        TrackedChangeSystemColumnInfo changeVersionColumn = columns.ChangeVersionColumn;

        List<string> selectExpressions =
        [
            $"c.{Quote(idColumn.ColumnName)} AS {QuoteIdentifier("__Id")}",
            $"c.{Quote(changeVersionColumn.ColumnName)} AS {QuoteIdentifier("__ChangeVersion")}",
        ];
        selectExpressions.AddRange(fields.SelectMany(BuildOldValueSelectExpressions));

        return $"""
            SELECT {string.Join(",\n       ", selectExpressions)}
            FROM (
            {Indent(filteredDeletesSql.Sql)}
            ) c
            ORDER BY c.{Quote(changeVersionColumn.ColumnName)} ASC
            {BuildPagingSql()}
            """;
    }

    private static IReadOnlyList<RelationalParameter> BuildDeletesParameters(
        IRelationalTrackedChangeQueryRequest request,
        FilteredDeletesSql filteredDeletesSql
    )
    {
        List<RelationalParameter> parameters = BuildPagingParameters(request);

        if (filteredDeletesSql.UsesDiscriminator)
        {
            parameters.Add(new RelationalParameter(DiscriminatorParameterName, BuildDiscriminator(request)));
            parameters.Add(
                new RelationalParameter(
                    QualifiedDiscriminatorParameterName,
                    BuildQualifiedDiscriminator(request)
                )
            );
        }
        parameters.AddRange(
            filteredDeletesSql.DescriptorDiscriminatorParameters.Select(parameter => new RelationalParameter(
                parameter.Name,
                parameter.Value
            ))
        );

        return parameters;
    }

    private static List<RelationalParameter> BuildPagingParameters(
        IRelationalTrackedChangeQueryRequest request
    )
    {
        PaginationParameters pagination = request.PaginationParameters;
        ChangeVersionRange changeVersionRange = request.ChangeVersionRange;
        return
        [
            new(MinChangeVersionParameterName, changeVersionRange.MinChangeVersion),
            new(MaxChangeVersionParameterName, changeVersionRange.MaxChangeVersion),
            new(LimitParameterName, (long)(pagination.Limit ?? pagination.MaximumPageSize)),
            new(OffsetParameterName, (long)(pagination.Offset ?? 0)),
        ];
    }

    private void AppendRegularResourceRecreatedSuppression(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields,
        List<string> joins,
        List<string> predicates,
        List<DescriptorDiscriminatorParameter> descriptorDiscriminatorParameters
    )
    {
        if (fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Delete planning for tracked-change table '{request.TrackedChangeTable.Table}' requires at least one identity response field."
            );
        }

        List<string> liveJoinConditions = [];
        var descriptorJoinIndex = 0;
        foreach (ChangeQueryResponseField field in fields)
        {
            switch (field.Kind)
            {
                case ChangeQueryResponseFieldKind.Scalar:
                    DbColumnName liveColumn = ResolveLiveScalarIdentityColumn(
                        request.ResourceModel,
                        field.OldColumn
                    );
                    liveJoinConditions.Add(
                        $"live.{Quote(liveColumn)} = c.{Quote(field.OldColumn.OldColumnName)}"
                    );
                    break;

                case ChangeQueryResponseFieldKind.Descriptor:
                    string discriminatorParameterName =
                        $"{DescriptorDiscriminatorParameterPrefix}{descriptorJoinIndex}";
                    string qualifiedDiscriminatorParameterName =
                        $"{DescriptorDiscriminatorQualifiedParameterPrefix}{descriptorJoinIndex}";
                    DescriptorIdentityJoin descriptorJoin = BuildDescriptorIdentityJoin(
                        request,
                        field,
                        descriptorJoinIndex,
                        discriminatorParameterName,
                        qualifiedDiscriminatorParameterName
                    );
                    descriptorJoinIndex++;
                    joins.Add(descriptorJoin.DescriptorJoinSql);
                    liveJoinConditions.Add(descriptorJoin.LiveJoinCondition);
                    descriptorDiscriminatorParameters.Add(
                        new DescriptorDiscriminatorParameter(
                            discriminatorParameterName,
                            descriptorJoin.DescriptorResource.ResourceName
                        )
                    );
                    descriptorDiscriminatorParameters.Add(
                        new DescriptorDiscriminatorParameter(
                            qualifiedDiscriminatorParameterName,
                            BuildQualifiedDiscriminator(descriptorJoin.DescriptorResource)
                        )
                    );
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Change Query response field kind '{field.Kind}'."
                    );
            }
        }

        joins.Add(
            $"LEFT JOIN {Quote(request.ResourceModel.RelationalModel.Root.Table)} live ON {string.Join(" AND ", liveJoinConditions)}"
        );
        predicates.Add($"live.{Quote(_documentIdColumn)} IS NULL");
    }

    private DescriptorIdentityJoin BuildDescriptorIdentityJoin(
        IRelationalTrackedChangeQueryRequest request,
        ChangeQueryResponseField field,
        int descriptorJoinIndex,
        string discriminatorParameterName,
        string qualifiedDiscriminatorParameterName
    )
    {
        TrackedChangeColumnInfo codeValueColumn =
            field.OldDescriptorCodeValueColumn
            ?? throw new InvalidOperationException(
                $"Descriptor Change Query response field '{field.QueryFieldName}' must include an old code value column."
            );
        string descriptorAlias = $"descriptor_{descriptorJoinIndex}";
        DescriptorIdentityMetadata descriptorIdentityMetadata = ResolveDescriptorIdentityMetadata(
            request,
            field.OldColumn
        );
        string descriptorJoinSql =
            $"LEFT JOIN {Quote(_descriptorTable)} {descriptorAlias} ON "
            + $"{descriptorAlias}.{Quote(_descriptorDiscriminatorColumn)} IN ({discriminatorParameterName}, {qualifiedDiscriminatorParameterName}) "
            + $"AND {descriptorAlias}.{Quote(_descriptorNamespaceColumn)} = c.{Quote(field.OldColumn.OldColumnName)} "
            + $"AND {descriptorAlias}.{Quote(_descriptorCodeValueColumn)} = c.{Quote(codeValueColumn.OldColumnName)}";

        return new DescriptorIdentityJoin(
            descriptorJoinSql,
            $"live.{Quote(descriptorIdentityMetadata.LiveDescriptorFkColumn)} = {descriptorAlias}.{Quote(_documentIdColumn)}",
            descriptorIdentityMetadata.DescriptorResource
        );
    }

    private void AppendSharedDescriptorDeleteFilters(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields,
        List<string> joins,
        List<string> predicates
    )
    {
        TrackedChangeSystemColumnInfo discriminatorColumn = RequireSystemColumn(
            request.TrackedChangeTable,
            TrackedChangeSystemColumnRole.Discriminator
        );
        TrackedChangeColumnInfo namespaceColumn = ResolveSharedDescriptorTrackedColumn(
            request,
            fields,
            SharedDescriptorTrackedColumnKind.Namespace
        );
        TrackedChangeColumnInfo codeValueColumn = ResolveSharedDescriptorTrackedColumn(
            request,
            fields,
            SharedDescriptorTrackedColumnKind.CodeValue
        );

        predicates.Add(
            $"c.{Quote(discriminatorColumn.ColumnName)} IN ({DiscriminatorParameterName}, {QualifiedDiscriminatorParameterName})"
        );
        joins.Add(
            $"LEFT JOIN {Quote(_descriptorTable)} live ON "
                + $"live.{Quote(_descriptorDiscriminatorColumn)} IN ({DiscriminatorParameterName}, {QualifiedDiscriminatorParameterName}) "
                + $"AND live.{Quote(_descriptorNamespaceColumn)} = c.{Quote(namespaceColumn.OldColumnName)} "
                + $"AND live.{Quote(_descriptorCodeValueColumn)} = c.{Quote(codeValueColumn.OldColumnName)}"
        );
        predicates.Add($"live.{Quote(_documentIdColumn)} IS NULL");
    }

    private static DbColumnName ResolveLiveScalarIdentityColumn(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo trackedColumn
    )
    {
        if (trackedColumn.CanonicalStorageColumn is { } canonicalStorageColumn)
        {
            bool rootTableContainsCanonicalColumn = resourceModel.RelationalModel.Root.Columns.Any(column =>
                column.ColumnName == canonicalStorageColumn
            );
            if (!rootTableContainsCanonicalColumn)
            {
                throw new InvalidOperationException(
                    $"Tracked path '{trackedColumn.SourceJsonPath}' declares canonical storage column "
                        + $"'{canonicalStorageColumn.Value}', but that column was not found on live root table "
                        + $"'{resourceModel.RelationalModel.Root.Table}' for resource "
                        + $"'{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
                );
            }

            return canonicalStorageColumn;
        }

        DbColumnModel[] matches = resourceModel
            .RelationalModel.Root.Columns.Where(column =>
                column.SourceJsonPath?.Canonical == trackedColumn.SourceJsonPath
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].ColumnName,
            0 => throw new InvalidOperationException(
                $"Unable to resolve live root identity column for tracked path '{trackedColumn.SourceJsonPath}' "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            ),
            _ => throw new InvalidOperationException(
                $"Tracked path '{trackedColumn.SourceJsonPath}' maps to {matches.Length} live root columns "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            ),
        };
    }

    private static DescriptorIdentityMetadata ResolveDescriptorIdentityMetadata(
        IRelationalTrackedChangeQueryRequest request,
        TrackedChangeColumnInfo namespaceColumn
    )
    {
        ConcreteResourceModel resourceModel = request.ResourceModel;
        if (namespaceColumn.DescriptorJoinName is { } descriptorJoinName)
        {
            TrackedChangeDescriptorJoinInfo[] trackedJoinMatches = request
                .TrackedChangeTable.DescriptorJoins.Where(join =>
                    string.Equals(join.DescriptorJoinName, descriptorJoinName, StringComparison.Ordinal)
                )
                .ToArray();
            if (trackedJoinMatches.Length == 1)
            {
                return ValidateLiveDescriptorFkColumn(
                    resourceModel,
                    namespaceColumn,
                    new DescriptorIdentityMetadata(
                        trackedJoinMatches[0].SourceColumn,
                        trackedJoinMatches[0].DescriptorResource
                    )
                );
            }

            if (trackedJoinMatches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' references descriptor join "
                        + $"'{descriptorJoinName}', but tracked-change table '{request.TrackedChangeTable.Table}' "
                        + $"contains {trackedJoinMatches.Length} matching descriptor joins."
                );
            }
        }

        DbTableName rootTable = resourceModel.RelationalModel.Root.Table;
        DescriptorEdgeSource[] edgeMatches = resourceModel
            .RelationalModel.DescriptorEdgeSources.Where(edge =>
                edge.Table == rootTable
                && edge.DescriptorValuePath.Canonical == namespaceColumn.SourceJsonPath
            )
            .ToArray();

        if (edgeMatches.Length == 1)
        {
            return ValidateLiveDescriptorFkColumn(
                resourceModel,
                namespaceColumn,
                new DescriptorIdentityMetadata(edgeMatches[0].FkColumn, edgeMatches[0].DescriptorResource)
            );
        }

        if (edgeMatches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' maps to {edgeMatches.Length} live descriptor edges "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            );
        }

        DbColumnModel[] columnMatches = resourceModel
            .RelationalModel.Root.Columns.Where(column =>
                column.Kind is ColumnKind.DescriptorFk
                && column.SourceJsonPath?.Canonical == namespaceColumn.SourceJsonPath
            )
            .ToArray();

        if (columnMatches.Length == 1 && columnMatches[0].TargetResource is { } descriptorResource)
        {
            return ValidateLiveDescriptorFkColumn(
                resourceModel,
                namespaceColumn,
                new DescriptorIdentityMetadata(columnMatches[0].ColumnName, descriptorResource)
            );
        }

        if (columnMatches.Length == 1)
        {
            throw new InvalidOperationException(
                $"Unable to resolve expected descriptor resource for tracked path '{namespaceColumn.SourceJsonPath}' "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            );
        }

        throw columnMatches.Length switch
        {
            0 => new InvalidOperationException(
                $"Unable to resolve live descriptor FK for tracked path '{namespaceColumn.SourceJsonPath}' "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            ),
            _ => new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' maps to {columnMatches.Length} live root descriptor columns "
                    + $"on resource '{resourceModel.RelationalModel.Resource.ProjectName}:{resourceModel.RelationalModel.Resource.ResourceName}'."
            ),
        };
    }

    private static DescriptorIdentityMetadata ValidateLiveDescriptorFkColumn(
        ConcreteResourceModel resourceModel,
        TrackedChangeColumnInfo namespaceColumn,
        DescriptorIdentityMetadata descriptorIdentityMetadata
    )
    {
        DbColumnModel[] matches = resourceModel
            .RelationalModel.Root.Columns.Where(column =>
                column.ColumnName == descriptorIdentityMetadata.LiveDescriptorFkColumn
            )
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' for resource "
                    + $"'{FormatResource(resourceModel.RelationalModel.Resource)}' resolved descriptor FK column "
                    + $"'{descriptorIdentityMetadata.LiveDescriptorFkColumn.Value}' for expected descriptor resource "
                    + $"'{FormatResource(descriptorIdentityMetadata.DescriptorResource)}', but that column was not found "
                    + $"on live root table '{resourceModel.RelationalModel.Root.Table}'."
            );
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' for resource "
                    + $"'{FormatResource(resourceModel.RelationalModel.Resource)}' resolved descriptor FK column "
                    + $"'{descriptorIdentityMetadata.LiveDescriptorFkColumn.Value}' for expected descriptor resource "
                    + $"'{FormatResource(descriptorIdentityMetadata.DescriptorResource)}', but live root table "
                    + $"'{resourceModel.RelationalModel.Root.Table}' has {matches.Length} columns with that name."
            );
        }

        DbColumnModel liveColumn = matches[0];
        if (liveColumn.Kind is not ColumnKind.DescriptorFk)
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' for resource "
                    + $"'{FormatResource(resourceModel.RelationalModel.Resource)}' resolved descriptor FK column "
                    + $"'{descriptorIdentityMetadata.LiveDescriptorFkColumn.Value}' for expected descriptor resource "
                    + $"'{FormatResource(descriptorIdentityMetadata.DescriptorResource)}', but live root table "
                    + $"'{resourceModel.RelationalModel.Root.Table}' has column kind '{liveColumn.Kind}' instead of "
                    + $"'{ColumnKind.DescriptorFk}'."
            );
        }

        if (
            liveColumn.TargetResource is { } targetResource
            && targetResource != descriptorIdentityMetadata.DescriptorResource
        )
        {
            throw new InvalidOperationException(
                $"Descriptor identity path '{namespaceColumn.SourceJsonPath}' for resource "
                    + $"'{FormatResource(resourceModel.RelationalModel.Resource)}' resolved descriptor FK column "
                    + $"'{descriptorIdentityMetadata.LiveDescriptorFkColumn.Value}' for expected descriptor resource "
                    + $"'{FormatResource(descriptorIdentityMetadata.DescriptorResource)}', but live root table "
                    + $"'{resourceModel.RelationalModel.Root.Table}' targets descriptor resource "
                    + $"'{FormatResource(targetResource)}'."
            );
        }

        return descriptorIdentityMetadata;
    }

    private static TrackedChangeColumnInfo ResolveSharedDescriptorTrackedColumn(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ChangeQueryResponseField> fields,
        SharedDescriptorTrackedColumnKind kind
    )
    {
        string queryFieldName =
            kind is SharedDescriptorTrackedColumnKind.Namespace ? "namespace" : "codeValue";
        string sourceJsonPath =
            kind is SharedDescriptorTrackedColumnKind.Namespace ? "$.namespace" : "$.codeValue";
        string storageColumn =
            kind is SharedDescriptorTrackedColumnKind.Namespace ? "Namespace" : "CodeValue";

        ChangeQueryResponseField[] matches = fields
            .Where(field =>
                field.QueryFieldName == queryFieldName
                || field.OldColumn.SourceJsonPath == sourceJsonPath
                || field.OldColumn.CanonicalStorageColumn?.Value == storageColumn
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].OldColumn,
            0 => throw new InvalidOperationException(
                $"Unable to resolve tracked old {storageColumn} column for shared descriptor table "
                    + $"'{request.TrackedChangeTable.Table}'."
            ),
            _ => throw new InvalidOperationException(
                $"Shared descriptor table '{request.TrackedChangeTable.Table}' has {matches.Length} candidate tracked "
                    + $"{storageColumn} columns."
            ),
        };
    }

    private IEnumerable<string> BuildOldValueSelectExpressions(ChangeQueryResponseField field)
    {
        yield return $"c.{Quote(field.OldColumn.OldColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__old")}";

        if (field.Kind is ChangeQueryResponseFieldKind.Descriptor)
        {
            TrackedChangeColumnInfo codeValueColumn =
                field.OldDescriptorCodeValueColumn
                ?? throw new InvalidOperationException(
                    $"Descriptor Change Query response field '{field.QueryFieldName}' must include an old code value column."
                );
            yield return $"c.{Quote(codeValueColumn.OldColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__oldCodeValue")}";
        }
    }

    private IEnumerable<string> BuildKeyChangeSelectExpressions(ChangeQueryResponseField field)
    {
        yield return $"firstChange.{Quote(field.OldColumn.OldColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__old")}";
        yield return $"lastChange.{Quote(field.NewColumn.NewColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__new")}";

        if (field.Kind is ChangeQueryResponseFieldKind.Descriptor)
        {
            TrackedChangeColumnInfo oldCodeValueColumn =
                field.OldDescriptorCodeValueColumn
                ?? throw new InvalidOperationException(
                    $"Descriptor Change Query response field '{field.QueryFieldName}' must include an old code value column."
                );
            TrackedChangeColumnInfo newCodeValueColumn =
                field.NewDescriptorCodeValueColumn
                ?? throw new InvalidOperationException(
                    $"Descriptor Change Query response field '{field.QueryFieldName}' must include a new code value column."
                );
            yield return $"firstChange.{Quote(oldCodeValueColumn.OldColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__oldCodeValue")}";
            yield return $"lastChange.{Quote(newCodeValueColumn.NewColumnName)} AS {QuoteIdentifier($"{field.QueryFieldName}__newCodeValue")}";
        }
    }

    private string BuildPagingSql() =>
        _dialect switch
        {
            SqlDialect.Pgsql =>
                $"{Environment.NewLine}LIMIT {LimitParameterName} OFFSET {OffsetParameterName}",
            SqlDialect.Mssql =>
                $"OFFSET {OffsetParameterName} ROWS FETCH NEXT {LimitParameterName} ROWS ONLY",
            _ => throw new InvalidOperationException($"Unsupported SQL dialect '{_dialect}'."),
        };

    private static bool IsSharedDescriptorRequest(IRelationalTrackedChangeQueryRequest request) =>
        request.TrackedChangeTable.Kind is TrackedChangeTableKind.SharedDescriptor
        || request.ResourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable;

    private static string BuildDiscriminator(IRelationalTrackedChangeQueryRequest request) =>
        request.ResourceInfo.ResourceName.Value;

    private static string BuildQualifiedDiscriminator(IRelationalTrackedChangeQueryRequest request) =>
        $"{request.ResourceInfo.ProjectName.Value}:{request.ResourceInfo.ResourceName.Value}";

    private static string BuildQualifiedDiscriminator(QualifiedResourceName descriptorResource) =>
        $"{descriptorResource.ProjectName}:{descriptorResource.ResourceName}";

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}:{resource.ResourceName}";

    private string Quote(DbTableName table) => SqlIdentifierQuoter.QuoteTableName(_dialect, table);

    private string Quote(DbColumnName column) => SqlIdentifierQuoter.QuoteIdentifier(_dialect, column);

    private string QuoteIdentifier(string identifier) =>
        SqlIdentifierQuoter.QuoteIdentifier(_dialect, identifier);

    private static string Indent(string sql) =>
        "    " + sql.Replace("\n", "\n    ", StringComparison.Ordinal);

    private static PlanColumns ResolvePlanColumns(TrackedChangeTableInfo table) =>
        new(
            RequireSystemColumn(table, TrackedChangeSystemColumnRole.Id),
            RequireSystemColumn(table, TrackedChangeSystemColumnRole.ChangeVersion),
            RequireRepresentativeIdentityColumn(table)
        );

    private readonly record struct PlanColumns(
        TrackedChangeSystemColumnInfo IdColumn,
        TrackedChangeSystemColumnInfo ChangeVersionColumn,
        TrackedChangeColumnInfo RepresentativeIdentityColumn
    );

    private sealed record FilteredDeletesSql(
        string Sql,
        bool UsesDiscriminator,
        IReadOnlyList<DescriptorDiscriminatorParameter> DescriptorDiscriminatorParameters
    );

    private sealed record DescriptorIdentityJoin(
        string DescriptorJoinSql,
        string LiveJoinCondition,
        QualifiedResourceName DescriptorResource
    );

    private sealed record DescriptorIdentityMetadata(
        DbColumnName LiveDescriptorFkColumn,
        QualifiedResourceName DescriptorResource
    );

    private sealed record DescriptorDiscriminatorParameter(string Name, string Value);

    private enum SharedDescriptorTrackedColumnKind
    {
        Namespace,
        CodeValue,
    }
}
