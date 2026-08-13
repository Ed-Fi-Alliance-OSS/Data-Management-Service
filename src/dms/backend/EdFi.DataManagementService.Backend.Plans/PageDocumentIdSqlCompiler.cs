// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles root-table page queries that return <c>DocumentId</c> keysets for GET-by-query reads.
/// </summary>
/// <remarks>
/// Predicates over unified alias columns are rewritten to canonical storage columns and,
/// when required, include an explicit non-null presence gate.
/// </remarks>
public sealed class PageDocumentIdSqlCompiler(SqlDialect dialect)
{
    private const int CursorBoundCount = 2;
    private const string DocumentIdColumnName = "DocumentId";
    private const string ContentVersionColumnName = "ContentVersion";
    private const string DocumentUuidColumnName = "DocumentUuid";
    private const string MissingPresenceColumnSortValue = "";
    private static readonly string _rootAlias = PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root);
    private static readonly string _documentAlias = PlanNamingConventions.GetFixedAlias(
        PlanSqlAliasRole.Document
    );
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");

    private abstract record OrderedPageAuthorizationAndFilter(int RawConfiguredIndex, int StableTieBreaker)
    {
        public sealed record Namespace(NamespaceAuthorizationCheckSpec Check, int StableTieBreaker)
            : OrderedPageAuthorizationAndFilter(Check.RawConfiguredIndex, StableTieBreaker);

        public sealed record CustomView(
            PageDocumentIdAuthorizationCustomViewCheck Check,
            int StableTieBreaker
        ) : OrderedPageAuthorizationAndFilter(Check.RawConfiguredIndex, StableTieBreaker);
    }

    private readonly SqlDialect _dialect = dialect;
    private readonly ISqlDialect _sqlDialect = SqlDialectFactory.Create(dialect);
    private readonly IPlanSqlDialect _planSqlDialect = PlanSqlDialectFactory.Create(dialect);

    /// <summary>
    /// Compiles page keyset SQL and total-count SQL for the supplied query specification.
    /// </summary>
    /// <param name="spec">The root-table query specification.</param>
    /// <returns>The compiled SQL plan.</returns>
    public PageDocumentIdSqlPlan Compile(PageDocumentIdQuerySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Predicates);
        ArgumentNullException.ThrowIfNull(spec.UnifiedAliasMappingsByColumn);

        if (spec.Predicates.Any(predicate => predicate is null))
        {
            throw CreateNullPredicateEntryException();
        }

        var mode = spec.Mode ?? new PageCandidateMode.Traditional();
        var modeParameters = PageCandidateModeParameters.For(mode);

        ValidateModeParameterNames(modeParameters);

        var rewrittenPredicates = RewriteAndSortPredicates(
            spec.Predicates,
            spec.UnifiedAliasMappingsByColumn
        );
        var authorization = NormalizeAuthorization(spec.Authorization);
        var authorizationClaimParameterization = authorization?.ClaimEducationOrganizationIdParameterization;
        var namespacePrefixParameterization = authorization?.NamespacePrefixParameterization;
        var requiresDocumentUuidJoin = rewrittenPredicates.Any(static predicate =>
            predicate.Target is QueryPredicateTarget.DocumentUuid
        );
        var filterParametersInOrder = BuildFilterParametersInOrder(
            rewrittenPredicates,
            authorizationClaimParameterization,
            namespacePrefixParameterization
        );
        var filterParameterNamesInOrder = filterParametersInOrder
            .Select(static parameter => parameter.ParameterName)
            .ToArray();
        ValidateFilterParameterNamesDoNotCollideWithModeParameters(
            filterParameterNamesInOrder,
            modeParameters
        );
        ValidateFilterParameterNamesAreUnique(filterParameterNamesInOrder);

        var pageSql = BuildPageDocumentIdSql(
            spec,
            mode,
            rewrittenPredicates,
            authorization,
            authorizationClaimParameterization,
            requiresDocumentUuidJoin
        );
        var includeTotalCountSql = mode is PageCandidateMode.Traditional { IncludeTotalCountSql: true };
        var totalCountSql = includeTotalCountSql
            ? BuildTotalCountSql(
                spec.RootTable,
                rewrittenPredicates,
                authorization,
                authorizationClaimParameterization,
                requiresDocumentUuidJoin
            )
            : null;
        var pageParametersInOrder = BuildPageParametersInOrder(filterParametersInOrder, modeParameters);
        var totalCountParametersInOrder = includeTotalCountSql
            ? BuildTotalCountParametersInOrder(filterParametersInOrder)
            : null;

        return new PageDocumentIdSqlPlan(
            pageSql,
            totalCountSql,
            pageParametersInOrder,
            totalCountParametersInOrder
        );
    }

    /// <summary>
    /// Rewrites predicates into canonical storage-column form and sorts by deterministic key.
    /// </summary>
    private static IReadOnlyList<RewrittenPredicate> RewriteAndSortPredicates(
        IReadOnlyList<QueryValuePredicate> predicates,
        IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> aliasMappingsByColumn
    )
    {
        var rewrittenPredicates = predicates
            .Select(predicate => RewritePredicate(predicate, aliasMappingsByColumn))
            .OrderBy(predicate => GetTargetSortKey(predicate.Target), StringComparer.Ordinal)
            .ThenBy(
                predicate => predicate.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparer.Ordinal
            )
            .ThenBy(predicate => predicate.CanonicalColumn.Value, StringComparer.Ordinal)
            .ThenBy(predicate => GetOperatorSortKey(predicate.Operator), StringComparer.Ordinal)
            .ThenBy(predicate => predicate.ParameterName, StringComparer.Ordinal)
            .ToArray();

        var startIndex = 0;
        while (startIndex < rewrittenPredicates.Length)
        {
            var endExclusiveIndex = startIndex + 1;

            while (
                endExclusiveIndex < rewrittenPredicates.Length
                && HasDuplicateSemanticKey(
                    rewrittenPredicates[startIndex],
                    rewrittenPredicates[endExclusiveIndex]
                )
            )
            {
                endExclusiveIndex++;
            }

            if (endExclusiveIndex - startIndex > 1)
            {
                throw CreateDuplicateSemanticPredicateException(
                    rewrittenPredicates,
                    startIndex,
                    endExclusiveIndex
                );
            }

            startIndex = endExclusiveIndex;
        }

        return rewrittenPredicates;
    }

    /// <summary>
    /// Rewrites a single predicate to its canonical storage-column representation.
    /// </summary>
    private static RewrittenPredicate RewritePredicate(
        QueryValuePredicate predicate,
        IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> aliasMappingsByColumn
    )
    {
        PlanSqlWriterExtensions.ValidateBareParameterName(
            predicate.ParameterName,
            nameof(predicate.ParameterName)
        );

        if (predicate.Target is QueryPredicateTarget.DocumentUuid)
        {
            return new RewrittenPredicate(
                predicate.Target,
                new DbColumnName(DocumentUuidColumnName),
                new DbColumnName(DocumentUuidColumnName),
                null,
                predicate.Operator,
                predicate.ParameterName,
                predicate.ScalarKind
            );
        }

        if (predicate.Target is not QueryPredicateTarget.RootColumn(var originalColumn))
        {
            throw new InvalidOperationException(
                $"Unsupported query predicate target '{predicate.Target.GetType().Name}'."
            );
        }

        if (!aliasMappingsByColumn.TryGetValue(originalColumn, out var mapping))
        {
            return new RewrittenPredicate(
                predicate.Target,
                originalColumn,
                originalColumn,
                null,
                predicate.Operator,
                predicate.ParameterName,
                predicate.ScalarKind
            );
        }

        return new RewrittenPredicate(
            predicate.Target,
            originalColumn,
            mapping.CanonicalColumn,
            mapping.PresenceColumn,
            predicate.Operator,
            predicate.ParameterName,
            predicate.ScalarKind
        );
    }

    /// <summary>
    /// Ensures every mode-owned parameter name is a valid bare name and that the names are mutually
    /// distinct (case-insensitive).
    /// </summary>
    private static void ValidateModeParameterNames(IReadOnlyList<PageCandidateModeParameter> modeParameters)
    {
        foreach (var modeParameter in modeParameters)
        {
            PlanSqlWriterExtensions.ValidateBareParameterName(modeParameter.Name, modeParameter.PropertyName);
        }

        for (var index = 0; index < modeParameters.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < modeParameters.Count; otherIndex++)
            {
                if (
                    string.Equals(
                        modeParameters[index].Name,
                        modeParameters[otherIndex].Name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    throw CreateModeParameterCollisionException(
                        modeParameters[index],
                        modeParameters[otherIndex]
                    );
                }
            }
        }
    }

    /// <summary>
    /// Ensures no filter-parameter name equals a mode-owned parameter name (case-insensitive).
    /// </summary>
    /// <remarks>
    /// Keying the mode names requires them to be mutually distinct, which
    /// <see cref="ValidateModeParameterNames" /> has already established by the time this runs.
    /// </remarks>
    private static void ValidateFilterParameterNamesDoNotCollideWithModeParameters(
        IReadOnlyList<string> filterParameterNames,
        IReadOnlyList<PageCandidateModeParameter> modeParameters
    )
    {
        var modeParametersByName = modeParameters.ToDictionary(
            static modeParameter => modeParameter.Name,
            StringComparer.OrdinalIgnoreCase
        );
        var collidingFilterParameterName = filterParameterNames.FirstOrDefault(
            modeParametersByName.ContainsKey
        );

        if (collidingFilterParameterName is not null)
        {
            throw CreateFilterModeParameterCollisionException(
                collidingFilterParameterName,
                modeParametersByName[collidingFilterParameterName],
                nameof(PageDocumentIdQuerySpec.Predicates)
            );
        }
    }

    /// <summary>
    /// Ensures filter-parameter names are unique (case-insensitive).
    /// </summary>
    private static void ValidateFilterParameterNamesAreUnique(IReadOnlyList<string> filterParameterNames)
    {
        var duplicateGroups = filterParameterNames
            .GroupBy(static parameterName => parameterName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group =>
                group.OrderBy(static parameterName => parameterName, StringComparer.Ordinal).ToArray()
            )
            .OrderBy(static group => group[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group[0], StringComparer.Ordinal)
            .ToArray();

        if (duplicateGroups.Length == 0)
        {
            return;
        }

        throw CreateDuplicateFilterParameterNamesException(
            duplicateGroups,
            nameof(PageDocumentIdQuerySpec.Predicates)
        );
    }

    /// <summary>
    /// Builds deterministic filter-parameter metadata in canonical plan order.
    /// Executors bind parameters by name, so this ordering does not need to match placeholder appearance per dialect.
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildFilterParametersInOrder(
        IReadOnlyList<RewrittenPredicate> predicates,
        AuthorizationClaimEducationOrganizationIdParameterization? authorizationClaimParameterization,
        NamespacePrefixParameterization? namespacePrefixParameterization
    )
    {
        List<QuerySqlParameter> filterParametersInOrder =
        [
            .. predicates.Select(static predicate => new QuerySqlParameter(
                QuerySqlParameterRole.Filter,
                predicate.ParameterName
            )),
        ];

        if (namespacePrefixParameterization is not null)
        {
            filterParametersInOrder.AddRange(
                NamespacePrefixSqlHelper.BuildFilterParametersInOrder(namespacePrefixParameterization)
            );
        }

        if (authorizationClaimParameterization is not null)
        {
            filterParametersInOrder.AddRange(
                AuthorizationClaimEducationOrganizationIdSqlHelper.BuildFilterParametersInOrder(
                    authorizationClaimParameterization
                )
            );
        }

        return filterParametersInOrder;
    }

    /// <summary>
    /// Builds deterministic page-query parameter metadata in canonical plan order.
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildPageParametersInOrder(
        IReadOnlyList<QuerySqlParameter> filterParametersInOrder,
        IReadOnlyList<PageCandidateModeParameter> modeParameters
    )
    {
        var boundModeParameters = modeParameters
            .Where(static modeParameter => modeParameter.IsBound)
            .ToArray();
        var pageParametersInOrder = new List<QuerySqlParameter>(
            filterParametersInOrder.Count + boundModeParameters.Length
        );

        pageParametersInOrder.AddRange(filterParametersInOrder);
        pageParametersInOrder.AddRange(
            boundModeParameters.Select(static modeParameter => new QuerySqlParameter(
                modeParameter.Role,
                modeParameter.Name
            ))
        );

        return pageParametersInOrder;
    }

    /// <summary>
    /// Builds deterministic total-count query parameter metadata in canonical plan order (filters only).
    /// </summary>
    private static IReadOnlyList<QuerySqlParameter> BuildTotalCountParametersInOrder(
        IReadOnlyList<QuerySqlParameter> filterParametersInOrder
    )
    {
        return [.. filterParametersInOrder];
    }

    private PageDocumentIdAuthorizationSpec? NormalizeAuthorization(
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        if (authorization is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(authorization.Strategies);

        if (authorization.Strategies.Any(static strategy => strategy is null))
        {
            throw new ArgumentException(
                $"{nameof(PageDocumentIdAuthorizationSpec.Strategies)} must not contain null entries.",
                nameof(authorization)
            );
        }

        var normalizedStrategies = authorization.Strategies.ToArray();

        foreach (var strategy in normalizedStrategies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategy.StrategyName);
            ArgumentNullException.ThrowIfNull(strategy.Subjects);

            if (strategy.Subjects.Any(static subject => subject is null))
            {
                throw new ArgumentException(
                    $"{nameof(PageDocumentIdAuthorizationStrategy.Subjects)} must not contain null entries.",
                    nameof(authorization)
                );
            }

            if (strategy.Subjects.Count == 0)
            {
                throw new ArgumentException(
                    $"Authorization strategy '{strategy.StrategyName}' requires at least one authorization subject.",
                    nameof(authorization)
                );
            }

            foreach (var subject in strategy.Subjects)
            {
                ArgumentNullException.ThrowIfNull(subject.AuthObject);
                ArgumentNullException.ThrowIfNull(subject.Contributors);
            }
        }

        if (
            authorization.NamespaceChecks is not null
            && authorization.NamespaceChecks.Any(static check => check is null)
        )
        {
            throw new ArgumentException(
                $"{nameof(PageDocumentIdAuthorizationSpec.NamespaceChecks)} must not contain null entries.",
                nameof(authorization)
            );
        }

        var normalizedNamespaceChecks = (authorization.NamespaceChecks ?? []).ToArray();

        var hasCustomViewChecks =
            authorization.CustomViewChecks is not null && authorization.CustomViewChecks.Count > 0;

        if (normalizedStrategies.Length == 0 && normalizedNamespaceChecks.Length == 0 && !hasCustomViewChecks)
        {
            return null;
        }

        if (normalizedStrategies.Length > 0)
        {
            ArgumentNullException.ThrowIfNull(authorization.ClaimEducationOrganizationIdParameterization);
            ValidateAuthorizationClaimParameterization(
                authorization.ClaimEducationOrganizationIdParameterization
            );
        }

        if (normalizedNamespaceChecks.Length > 0)
        {
            ArgumentNullException.ThrowIfNull(authorization.NamespacePrefixParameterization);
            NamespacePrefixParameterizationValidator.ValidateOrThrow(
                authorization.NamespacePrefixParameterization,
                _dialect,
                nameof(PageDocumentIdAuthorizationSpec.NamespacePrefixParameterization),
                "Page document-id SQL compilation"
            );
        }

        if (
            authorization.CustomViewChecks is not null
            && authorization.CustomViewChecks.Any(static c => c is null)
        )
        {
            throw new ArgumentException(
                $"{nameof(PageDocumentIdAuthorizationSpec.CustomViewChecks)} must not contain null entries.",
                nameof(authorization)
            );
        }

        return authorization with
        {
            Strategies = normalizedStrategies,
            NamespaceChecks = normalizedNamespaceChecks,
            CustomViewChecks = authorization.CustomViewChecks is null
                ? null
                : authorization.CustomViewChecks.ToArray(),
        };
    }

    private void ValidateAuthorizationClaimParameterization(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        AuthorizationClaimEducationOrganizationIdParameterizationValidator.ValidateOrThrow(
            authorizationClaimParameterization,
            _dialect,
            nameof(PageDocumentIdAuthorizationSpec.ClaimEducationOrganizationIdParameterization),
            "Page document-id SQL compilation"
        );
    }

    /// <summary>
    /// Emits canonical SQL for page-<c>DocumentId</c> selection.
    /// </summary>
    private string BuildPageDocumentIdSql(
        PageDocumentIdQuerySpec spec,
        PageCandidateMode mode,
        IReadOnlyList<RewrittenPredicate> predicates,
        PageDocumentIdAuthorizationSpec? authorization,
        AuthorizationClaimEducationOrganizationIdParameterization? authorizationClaimParameterization,
        bool requiresDocumentUuidJoin
    )
    {
        var writer = new SqlWriter(_sqlDialect);
        var cursor = mode as PageCandidateMode.Cursor;

        writer.Append("SELECT ");

        if (cursor is not null)
        {
            _planSqlDialect.AppendCursorSelectRowLimitPrefix(writer, cursor.PageSizeParameterName);
        }

        writer
            .Append($"{_rootAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .AppendLine()
            .Append("FROM ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(spec.RootTable))
            .AppendLine($" {_rootAlias}");

        AppendDocumentJoin(writer, requiresDocumentUuidJoin);
        AppendWhereClause(
            writer,
            spec.RootTable,
            predicates,
            authorization,
            authorizationClaimParameterization,
            cursor
        );

        // The unpaged candidate relation is deliberately unordered. Its consumer wraps it in a common
        // table expression and applies its own row numbering, and SQL Server rejects ORDER BY in a CTE
        // that has no TOP or OFFSET.
        if (mode is PageCandidateMode.UnpagedCandidates)
        {
            writer.AppendLine(";");

            return writer.ToString();
        }

        writer
            .Append($"ORDER BY {_rootAlias}.")
            .AppendQuoted(ResolveOrderingColumnName(mode))
            .AppendLine(" ASC");

        switch (mode)
        {
            case PageCandidateMode.Cursor cursorMode:
                _planSqlDialect.AppendCursorPagingClause(writer, cursorMode.PageSizeParameterName);
                break;

            case PageCandidateMode.Traditional traditionalMode:
                _planSqlDialect.AppendPagingClause(
                    writer,
                    traditionalMode.OffsetParameterName,
                    traditionalMode.LimitParameterName
                );
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode.GetType().Name,
                    "Unsupported page candidate mode."
                );
        }

        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Resolves the page-selection ordering column. Only traditional paging can order by the mirrored
    /// <c>ContentVersion</c> column; a cursor page is always ordered by <c>DocumentId</c>, because a
    /// token anchored on the highest selected <c>DocumentId</c> is only safe when that is also the
    /// page's ordering key.
    /// </summary>
    private static string ResolveOrderingColumnName(PageCandidateMode mode)
    {
        if (mode is not PageCandidateMode.Traditional traditional)
        {
            return DocumentIdColumnName;
        }

        return traditional.OrderingMode switch
        {
            PageOrderingMode.DocumentId => DocumentIdColumnName,
            PageOrderingMode.ContentVersion => ContentVersionColumnName,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                traditional.OrderingMode,
                "Unsupported page ordering mode."
            ),
        };
    }

    /// <summary>
    /// Emits canonical SQL for total-row count selection.
    /// </summary>
    private string BuildTotalCountSql(
        DbTableName rootTable,
        IReadOnlyList<RewrittenPredicate> predicates,
        PageDocumentIdAuthorizationSpec? authorization,
        AuthorizationClaimEducationOrganizationIdParameterization? authorizationClaimParameterization,
        bool requiresDocumentUuidJoin
    )
    {
        var writer = new SqlWriter(_sqlDialect);

        writer
            .AppendLine("SELECT COUNT(1)")
            .Append("FROM ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(rootTable))
            .AppendLine($" {_rootAlias}");

        AppendDocumentJoin(writer, requiresDocumentUuidJoin);
        AppendWhereClause(
            writer,
            rootTable,
            predicates,
            authorization,
            authorizationClaimParameterization,
            cursor: null
        );
        writer.AppendLine(";");

        return writer.ToString();
    }

    /// <summary>
    /// Emits the optional <c>dms.Document</c> join required for <c>?id=</c> filtering.
    /// </summary>
    private static void AppendDocumentJoin(SqlWriter writer, bool requiresDocumentUuidJoin)
    {
        if (!requiresDocumentUuidJoin)
        {
            return;
        }

        writer
            .Append("INNER JOIN ")
            .AppendRelation(new SqlRelationRef.PhysicalTable(_documentTable))
            .Append($" {_documentAlias} ON {_documentAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .Append($" = {_rootAlias}.")
            .AppendQuoted(DocumentIdColumnName)
            .AppendLine();
    }

    /// <summary>
    /// Emits a deterministic multi-line <c>WHERE</c> clause.
    /// </summary>
    private void AppendWhereClause(
        SqlWriter writer,
        DbTableName rootTable,
        IReadOnlyList<RewrittenPredicate> predicates,
        PageDocumentIdAuthorizationSpec? authorization,
        AuthorizationClaimEducationOrganizationIdParameterization? authorizationClaimParameterization,
        PageCandidateMode.Cursor? cursor
    )
    {
        var orderedAndFilters = BuildOrderedAuthorizationAndFilters(authorization);
        var hasRelationshipGroup = (authorization?.Strategies.Count ?? 0) > 0;

        // Cursor bounds are emitted last, alongside rather than instead of every authorization
        // predicate. Appending them keeps the filter and authorization fragments byte-identical to the
        // other candidate modes, which is what makes the shared-candidate guarantee checkable.
        var cursorBoundCount = cursor is null ? 0 : CursorBoundCount;
        var predicateCount =
            predicates.Count + orderedAndFilters.Count + (hasRelationshipGroup ? 1 : 0) + cursorBoundCount;

        writer.AppendWhereClause(
            predicateCount,
            (predicateWriter, index) =>
            {
                if (index < predicates.Count)
                {
                    AppendPredicateSql(predicateWriter, predicates[index]);
                    return;
                }

                var authorizationFilterIndex = index - predicates.Count;

                if (authorizationFilterIndex < orderedAndFilters.Count)
                {
                    AppendOrderedAuthorizationAndFilterSql(
                        predicateWriter,
                        rootTable,
                        authorization!,
                        orderedAndFilters[authorizationFilterIndex]
                    );
                    return;
                }

                var afterAuthorizationFilterIndex = authorizationFilterIndex - orderedAndFilters.Count;

                if (hasRelationshipGroup && afterAuthorizationFilterIndex == 0)
                {
                    AppendAuthorizationSql(
                        predicateWriter,
                        rootTable,
                        authorization!,
                        authorizationClaimParameterization
                            ?? throw new InvalidOperationException(
                                "Authorization SQL emission requires a claim EdOrg parameterization when authorization strategies are present."
                            )
                    );
                    return;
                }

                AppendCursorBoundSql(
                    predicateWriter,
                    cursor
                        ?? throw new InvalidOperationException(
                            "Cursor bound SQL emission requires a cursor candidate mode."
                        ),
                    afterAuthorizationFilterIndex - (hasRelationshipGroup ? 1 : 0)
                );
            }
        );
    }

    /// <summary>
    /// Emits one inclusive cursor bound predicate against the root <c>DocumentId</c>.
    /// </summary>
    private void AppendCursorBoundSql(SqlWriter writer, PageCandidateMode.Cursor cursor, int boundIndex)
    {
        var (operatorToken, parameterName) = boundIndex switch
        {
            0 => (">=", cursor.InclusiveMinimumParameterName),
            1 => ("<=", cursor.InclusiveMaximumParameterName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(boundIndex),
                boundIndex,
                "Unsupported cursor bound index."
            ),
        };

        _planSqlDialect.AppendComparisonSql(
            writer,
            _rootAlias,
            new DbColumnName(DocumentIdColumnName),
            operatorToken,
            parameterName,
            ScalarKind.Int64
        );
    }

    private static IReadOnlyList<OrderedPageAuthorizationAndFilter> BuildOrderedAuthorizationAndFilters(
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        if (authorization is null)
        {
            return [];
        }

        var filters = new List<OrderedPageAuthorizationAndFilter>();
        var stableTieBreaker = 0;

        foreach (var namespaceCheck in authorization.NamespaceChecks ?? [])
        {
            filters.Add(new OrderedPageAuthorizationAndFilter.Namespace(namespaceCheck, stableTieBreaker++));
        }

        foreach (var customViewCheck in authorization.CustomViewChecks ?? [])
        {
            filters.Add(
                new OrderedPageAuthorizationAndFilter.CustomView(customViewCheck, stableTieBreaker++)
            );
        }

        return filters
            .OrderBy(static filter => filter.RawConfiguredIndex)
            .ThenBy(static filter => filter.StableTieBreaker)
            .ToArray();
    }

    private static void AppendOrderedAuthorizationAndFilterSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationSpec authorization,
        OrderedPageAuthorizationAndFilter filter
    )
    {
        switch (filter)
        {
            case OrderedPageAuthorizationAndFilter.Namespace namespaceFilter:
                AppendNamespaceCheckSql(
                    writer,
                    rootTable,
                    namespaceFilter.Check,
                    authorization.NamespacePrefixParameterization
                        ?? throw new InvalidOperationException(
                            "Namespace authorization SQL emission requires a namespace prefix parameterization when namespace checks are present."
                        )
                );
                return;
            case OrderedPageAuthorizationAndFilter.CustomView customViewFilter:
                AppendCustomViewCheckSql(writer, rootTable, customViewFilter.Check);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(filter),
                    filter.GetType().Name,
                    "Unsupported page authorization AND filter."
                );
        }
    }

    private static void AppendNamespaceCheckSql(
        SqlWriter writer,
        DbTableName rootTable,
        NamespaceAuthorizationCheckSpec check,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        var tableAlias = ResolveNamespaceCheckAlias(check.RootTable, rootTable);

        NamespacePrefixSqlHelper.AppendRootTableNamespacePredicate(
            writer,
            tableAlias,
            check.NamespaceColumn,
            namespacePrefixParameterization
        );
    }

    private static void AppendCustomViewCheckSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationCustomViewCheck check
    )
    {
        if (!check.RootTable.Equals(rootTable))
        {
            throw new InvalidOperationException(
                $"Custom view authorization check root table '{check.RootTable}' does not match query root table '{rootTable}'."
            );
        }

        if (check.PathToBasisResource.Count == 0)
        {
            throw new InvalidOperationException(
                "Custom view authorization check must include a path to the basis resource."
            );
        }

        var pathSteps = check.PathToBasisResource;
        var terminalStep = pathSteps[^1];

        // A one-step path always sources from the root table: SecurableElementColumnPathResolver only emits a
        // single step when the basis is the subject itself or is referenced by a root-owned FK, and a
        // child-table edge is prefixed with a root-to-child step (making the path two steps). The FK already
        // holds the basis resource's DocumentId, so the column can be filtered against the auth view
        // directly. This covers descriptor bases too — their terminal step carries dms.Descriptor/DocumentId
        // as a target, but the extra root re-scan those targets would drive is redundant.
        if (pathSteps.Count == 1)
        {
            if (!terminalStep.SourceTable.Equals(rootTable))
            {
                throw new InvalidOperationException(
                    $"Custom view authorization direct path table '{terminalStep.SourceTable}' does not match query root table '{rootTable}'."
                );
            }

            AppendColumnInCustomViewSql(writer, _rootAlias, terminalStep.SourceColumnName, check);
            return;
        }

        // Multi-step paths emit an uncorrelated subquery that re-scans the root table
        // (r.DocumentId IN (SELECT t0.DocumentId FROM <root> t0 JOIN ...)) rather than correlating the
        // joins to the outer row. Deferred, not overlooked: a correlated EXISTS rewrite would change the
        // hot-path page SQL for every transitive custom view, so it needs a realistic-row-count
        // measurement first — at small row counts the planner often flattens this to the same plan.
        var aliasAllocator = PlanNamingConventions.CreateTableAliasAllocator();
        var rootSubqueryAlias = aliasAllocator.AllocateNext();
        var pathJoinAliases = Enumerable
            .Range(0, pathSteps.Count - 1)
            .Select(_ => aliasAllocator.AllocateNext())
            .ToArray();

        writer.Append($"{_rootAlias}.");
        writer.AppendQuoted(check.RootDocumentIdColumn.Value);
        writer.Append(" IN (SELECT ");
        writer.Append($"{rootSubqueryAlias}.");
        writer.AppendQuoted(check.RootDocumentIdColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(rootTable));
        writer.Append($" {rootSubqueryAlias}");

        var currentSourceAlias = rootSubqueryAlias;

        for (var stepIndex = 0; stepIndex < pathSteps.Count - 1; stepIndex++)
        {
            var step = pathSteps[stepIndex];
            var targetTable =
                step.TargetTable
                ?? throw new InvalidOperationException(
                    "Custom view authorization transitive path steps must include a target table for intermediate joins."
                );
            var targetColumn =
                step.TargetColumnName
                ?? throw new InvalidOperationException(
                    "Custom view authorization transitive path steps must include a target column for intermediate joins."
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

        writer.Append($" WHERE {currentSourceAlias}.");
        writer.AppendQuoted(terminalStep.SourceColumnName.Value);
        AppendCustomViewMembershipSubquerySql(writer, check, aliasAllocator.AllocateNext());
        writer.Append(")");
    }

    private static void AppendColumnInCustomViewSql(
        SqlWriter writer,
        string tableAlias,
        DbColumnName sourceColumn,
        PageDocumentIdAuthorizationCustomViewCheck check
    )
    {
        writer.Append($"{tableAlias}.");
        writer.AppendQuoted(sourceColumn.Value);
        AppendCustomViewMembershipSubquerySql(
            writer,
            check,
            PlanNamingConventions.CreateTableAliasAllocator().AllocateNext()
        );
    }

    private static void AppendCustomViewMembershipSubquerySql(
        SqlWriter writer,
        PageDocumentIdAuthorizationCustomViewCheck check,
        string authAlias
    )
    {
        writer.Append(" IN (SELECT ");
        writer.Append($"{authAlias}.");
        writer.AppendQuoted(check.AuthViewDocumentIdColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(check.AuthView));
        writer.Append($" {authAlias})");
    }

    /// <summary>
    /// Resolves the SQL alias qualifying a namespace check column. Every supported query — resource and
    /// descriptor alike — roots its namespace check on the query root table: descriptor page subqueries
    /// root on <c>dms.Descriptor</c>, which is where <c>Namespace</c> lives, so no second alias applies.
    /// A mismatch therefore means the planner emitted a check against a table this query does not root on,
    /// which is a planning defect. Throwing keeps that loud instead of silently qualifying the column with
    /// the root alias and emitting a filter against the wrong table.
    /// </summary>
    private static string ResolveNamespaceCheckAlias(DbTableName checkRootTable, DbTableName queryRootTable)
    {
        if (checkRootTable.Equals(queryRootTable))
        {
            return _rootAlias;
        }

        throw new InvalidOperationException(
            $"Namespace authorization check spec table '{checkRootTable}' does not match query root table '{queryRootTable}'. "
                + "Namespace authorization SQL emission supports only concrete root-table columns."
        );
    }

    private static void AppendAuthorizationSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationSpec authorization,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        var aliasAllocator = PlanNamingConventions.CreateTableAliasAllocator();

        for (var strategyIndex = 0; strategyIndex < authorization.Strategies.Count; strategyIndex++)
        {
            if (strategyIndex > 0)
            {
                writer.Append(" OR ");
            }

            writer.Append("(");
            AppendAuthorizationStrategySql(
                writer,
                rootTable,
                authorization.Strategies[strategyIndex],
                authorizationClaimParameterization,
                aliasAllocator
            );
            writer.Append(")");
        }
    }

    private static void AppendAuthorizationStrategySql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationStrategy strategy,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        PlanSqlTableAliasAllocator aliasAllocator
    )
    {
        for (var subjectIndex = 0; subjectIndex < strategy.Subjects.Count; subjectIndex++)
        {
            if (subjectIndex > 0)
            {
                writer.Append(" AND ");
            }

            AppendAuthorizationSubjectSql(
                writer,
                rootTable,
                strategy.Subjects[subjectIndex],
                authorizationClaimParameterization,
                aliasAllocator
            );
        }
    }

    private static void AppendAuthorizationSubjectSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationSubject subject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        PlanSqlTableAliasAllocator aliasAllocator
    )
    {
        switch (subject)
        {
            case PageDocumentIdAuthorizationEdOrgSubject edOrgSubject:
                AppendAuthorizationEdOrgSubjectSql(
                    writer,
                    rootTable,
                    edOrgSubject,
                    authorizationClaimParameterization,
                    aliasAllocator.AllocateNext()
                );
                return;
            case PageDocumentIdAuthorizationPersonSubject personSubject:
                AppendAuthorizationPersonSubjectSql(
                    writer,
                    rootTable,
                    personSubject,
                    authorizationClaimParameterization,
                    aliasAllocator
                );
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(subject),
                    subject.GetType().Name,
                    "Unsupported page document-id authorization subject."
                );
        }
    }

    private static void AppendAuthorizationPersonSubjectSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationPersonSubject subject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        PlanSqlTableAliasAllocator aliasAllocator
    )
    {
        var personMetadata = subject.PersonMetadata;

        // Load-bearing for anchor correctness, not merely a diagnostic: the emitters below qualify the
        // anchor column with the root alias, so a stored anchor describing a different table would emit a
        // predicate against a column that does not exist on the root row.
        RelationshipAuthorizationPeoplePathValidation.ValidateStoredAnchorRootTable(
            rootTable,
            personMetadata,
            "query root table"
        );

        switch (personMetadata.Path.Kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId:
                AppendRootDocumentIdInPersonAuthViewSql(
                    writer,
                    personMetadata.StoredAnchor.RootDocumentIdColumn,
                    subject.AuthObject,
                    authorizationClaimParameterization,
                    aliasAllocator.AllocateNext()
                );
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn:
                AppendRootDocumentIdInPersonAuthViewSql(
                    writer,
                    // Its step.SourceTable check is likewise load-bearing here: it is what guarantees the
                    // returned column lives on the root row and can be anchored on the root alias.
                    RelationshipAuthorizationPeoplePathValidation.GetDirectRootPersonDocumentIdColumn(
                        rootTable,
                        subject.Table,
                        subject.Column,
                        personMetadata,
                        "query root table"
                    ),
                    subject.AuthObject,
                    authorizationClaimParameterization,
                    aliasAllocator.AllocateNext()
                );
                return;
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath:
                AppendRootDocumentIdInTransitivePersonAuthViewSql(
                    writer,
                    rootTable,
                    subject,
                    authorizationClaimParameterization,
                    aliasAllocator
                );
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(subject),
                    personMetadata.Path.Kind,
                    "Unsupported People relationship authorization subject path kind."
                );
        }
    }

    private static void AppendRootDocumentIdInTransitivePersonAuthViewSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationPersonSubject subject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        PlanSqlTableAliasAllocator aliasAllocator
    )
    {
        var personMetadata = subject.PersonMetadata;
        var pathSteps = personMetadata.Path.Steps;

        // Load-bearing for anchor correctness, not merely a diagnostic: this is what guarantees
        // pathSteps[0].SourceTable is the query root, so the first step's source column is a column the root
        // row itself carries and can be qualified with the root alias.
        RelationshipAuthorizationPeoplePathValidation.ValidateTransitivePersonPath(
            rootTable,
            subject.Table,
            subject.Column,
            pathSteps
        );

        // The anchored shape needs a first hop to open the subquery on plus a separate terminal step carrying
        // the person column. RelationshipAuthorizationPersonSubjectPath enforces that at construction, so a
        // one-step transitive path cannot reach here.
        var firstStep = pathSteps[0];
        var firstHopTable =
            firstStep.TargetTable
            ?? throw new InvalidOperationException(
                "Transitive People authorization path steps must include a target table for first-hop joins."
            );
        var firstHopColumn =
            firstStep.TargetColumnName
            ?? throw new InvalidOperationException(
                "Transitive People authorization path steps must include a target column for first-hop joins."
            );

        var firstHopAlias = aliasAllocator.AllocateNext();
        var pathJoinAliases = Enumerable
            .Range(0, pathSteps.Count - 2)
            .Select(_ => aliasAllocator.AllocateNext())
            .ToArray();
        var authAlias = aliasAllocator.AllocateNext();

        // Anchor on the root row's own reference FK and open the subquery at the first hop's target table.
        // Anchoring on a primary-key self-join of the root table would be semantically identical but makes
        // PostgreSQL scan the root table twice and hash every authorized DocumentId on every page.
        writer.Append($"{_rootAlias}.");
        writer.AppendQuoted(firstStep.SourceColumnName.Value);
        writer.Append(" IN (SELECT ");
        writer.Append($"{firstHopAlias}.");
        writer.AppendQuoted(firstHopColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(firstHopTable));
        writer.Append($" {firstHopAlias}");

        var currentSourceAlias = firstHopAlias;

        for (var stepIndex = 1; stepIndex < pathSteps.Count - 1; stepIndex++)
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
            var joinAlias = pathJoinAliases[stepIndex - 1];

            writer.Append(" JOIN ");
            writer.AppendRelation(new SqlRelationRef.PhysicalTable(targetTable));
            writer.Append($" {joinAlias} ON {joinAlias}.");
            writer.AppendQuoted(targetColumn.Value);
            writer.Append($" = {currentSourceAlias}.");
            writer.AppendQuoted(step.SourceColumnName.Value);

            currentSourceAlias = joinAlias;
        }

        var terminalStep = pathSteps[^1];

        writer.Append($" WHERE {currentSourceAlias}.");
        writer.AppendQuoted(terminalStep.SourceColumnName.Value);
        AppendPersonAuthViewMembershipSubquerySql(
            writer,
            subject.AuthObject,
            authorizationClaimParameterization,
            authAlias
        );
        writer.Append(")");
    }

    /// <summary>
    /// Emits the Self and Direct person predicates anchored on the root row's own column — the person twin
    /// of <see cref="AppendRootSubjectHierarchyMatchSql"/>. Anchoring on a primary-key self-join of the root
    /// table instead would be semantically identical but makes PostgreSQL scan the root table twice and hash
    /// every authorized DocumentId on every page, so the anchor column is always one the root row carries.
    /// </summary>
    private static void AppendRootDocumentIdInPersonAuthViewSql(
        SqlWriter writer,
        DbColumnName anchorColumn,
        RelationshipAuthorizationAuthObject authObject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        writer.Append($"{_rootAlias}.");
        writer.AppendQuoted(anchorColumn.Value);
        AppendPersonAuthViewMembershipSubquerySql(
            writer,
            authObject,
            authorizationClaimParameterization,
            authAlias
        );
    }

    private static void AppendPersonAuthViewMembershipSubquerySql(
        SqlWriter writer,
        RelationshipAuthorizationAuthObject authObject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        writer.Append(" IN (SELECT ");
        writer.Append($"{authAlias}.");
        writer.AppendQuoted(authObject.SubjectValueColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(authObject.Name));
        writer.Append($" {authAlias} WHERE {authAlias}.");
        writer.AppendQuoted(authObject.ClaimEducationOrganizationIdColumn.Value);
        AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
            writer,
            authorizationClaimParameterization
        );
        writer.Append(")");
    }

    private static void AppendAuthorizationEdOrgSubjectSql(
        SqlWriter writer,
        DbTableName rootTable,
        PageDocumentIdAuthorizationEdOrgSubject subject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        if (!subject.Table.Equals(rootTable))
        {
            throw new InvalidOperationException(
                $"Authorization subject table '{subject.Table}' does not match query root table '{rootTable}'. "
                    + "DMS-1055 query authorization currently supports only concrete root-table subjects in the page query compiler."
            );
        }

        if (subject.AuthObject.AllowsDirectClaimMatch)
        {
            writer.Append("(");
            AppendRootSubjectDirectClaimMatchSql(writer, subject, authorizationClaimParameterization);
            writer.Append(" OR ");
            AppendRootSubjectHierarchyMatchSql(
                writer,
                subject,
                subject.AuthObject,
                authorizationClaimParameterization,
                authAlias
            );
            writer.Append(")");
            return;
        }

        AppendRootSubjectHierarchyMatchSql(
            writer,
            subject,
            subject.AuthObject,
            authorizationClaimParameterization,
            authAlias
        );
    }

    private static void AppendRootSubjectDirectClaimMatchSql(
        SqlWriter writer,
        PageDocumentIdAuthorizationEdOrgSubject subject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        writer.Append($"{_rootAlias}.");
        writer.AppendQuoted(subject.Column.Value);
        AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
            writer,
            authorizationClaimParameterization
        );
    }

    private static void AppendRootSubjectHierarchyMatchSql(
        SqlWriter writer,
        PageDocumentIdAuthorizationEdOrgSubject subject,
        RelationshipAuthorizationAuthObject authObject,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization,
        string authAlias
    )
    {
        writer.Append($"{_rootAlias}.");
        writer.AppendQuoted(subject.Column.Value);
        writer.Append(" IN (SELECT ");
        writer.Append($"{authAlias}.");
        writer.AppendQuoted(authObject.SubjectValueColumn.Value);
        writer.Append(" FROM ");
        writer.AppendRelation(new SqlRelationRef.PhysicalTable(authObject.Name));
        writer.Append($" {authAlias} WHERE {authAlias}.");
        writer.AppendQuoted(authObject.ClaimEducationOrganizationIdColumn.Value);
        AuthorizationClaimEducationOrganizationIdSqlHelper.AppendClaimFilterSql(
            writer,
            authorizationClaimParameterization
        );
        writer.Append(")");
    }

    /// <summary>
    /// Emits SQL for a single rewritten predicate.
    /// </summary>
    private void AppendPredicateSql(SqlWriter writer, RewrittenPredicate predicate)
    {
        if (predicate.PresenceColumn is not null)
        {
            AppendIsNotNullSql(writer, _rootAlias, predicate.PresenceColumn.Value);
            writer.Append(" AND ");
        }

        AppendComparisonSql(
            writer,
            GetTargetAlias(predicate.Target),
            predicate.CanonicalColumn,
            predicate.Operator,
            predicate.ParameterName,
            predicate.ScalarKind
        );
    }

    /// <summary>
    /// Emits a simple binary comparison predicate against a table column.
    /// </summary>
    private void AppendComparisonSql(
        SqlWriter writer,
        string tableAlias,
        DbColumnName column,
        QueryComparisonOperator @operator,
        string parameterName,
        ScalarKind? scalarKind
    )
    {
        _planSqlDialect.AppendComparisonSql(
            writer,
            tableAlias,
            column,
            ToSqlOperator(@operator),
            parameterName,
            scalarKind
        );
    }

    /// <summary>
    /// Emits an <c>IS NOT NULL</c> predicate against a table column.
    /// </summary>
    private static void AppendIsNotNullSql(SqlWriter writer, string tableAlias, DbColumnName column)
    {
        writer.Append($"{tableAlias}.").AppendQuoted(column.Value).Append(" IS NOT NULL");
    }

    /// <summary>
    /// Converts a query comparison operator to its SQL token.
    /// </summary>
    private static string ToSqlOperator(QueryComparisonOperator @operator)
    {
        // Compiler-level support only. DMS-993 runtime query planning routes equality
        // predicates only; non-equality operators are retained here for future query
        // syntax stories and must not be treated as currently supported API behavior.
        return @operator switch
        {
            QueryComparisonOperator.Equal => "=",
            QueryComparisonOperator.NotEqual => "<>",
            QueryComparisonOperator.LessThan => "<",
            QueryComparisonOperator.LessThanOrEqual => "<=",
            QueryComparisonOperator.GreaterThan => ">",
            QueryComparisonOperator.GreaterThanOrEqual => ">=",
            QueryComparisonOperator.Like => "LIKE",

            // Defer implementation until the real compilation stories
            QueryComparisonOperator.In => throw new NotSupportedException(
                $"Operator '{nameof(QueryComparisonOperator.In)}' is not yet supported by {nameof(ToSqlOperator)}."
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator for now."
            ),
        };
    }

    /// <summary>
    /// Returns a deterministic textual sort key for query operators without relying on <c>Enum.ToString()</c>.
    /// </summary>
    private static string GetOperatorSortKey(QueryComparisonOperator @operator)
    {
        return @operator switch
        {
            QueryComparisonOperator.Equal => nameof(QueryComparisonOperator.Equal),
            QueryComparisonOperator.NotEqual => nameof(QueryComparisonOperator.NotEqual),
            QueryComparisonOperator.LessThan => nameof(QueryComparisonOperator.LessThan),
            QueryComparisonOperator.LessThanOrEqual => nameof(QueryComparisonOperator.LessThanOrEqual),
            QueryComparisonOperator.GreaterThan => nameof(QueryComparisonOperator.GreaterThan),
            QueryComparisonOperator.GreaterThanOrEqual => nameof(QueryComparisonOperator.GreaterThanOrEqual),
            QueryComparisonOperator.Like => nameof(QueryComparisonOperator.Like),
            QueryComparisonOperator.In => nameof(QueryComparisonOperator.In),
            _ => throw new ArgumentOutOfRangeException(
                nameof(@operator),
                @operator,
                "Unsupported query operator sort key."
            ),
        };
    }

    /// <summary>
    /// Formats the duplicate-detection semantic key.
    /// </summary>
    private static string FormatSemanticKey(RewrittenPredicate predicate)
    {
        var presenceColumn = predicate.PresenceColumn?.Value ?? "<none>";
        var operatorToken = GetOperatorSortKey(predicate.Operator);

        return string.Join(
            ", ",
            $"presenceColumn='{presenceColumn}'",
            $"canonicalColumn='{predicate.CanonicalColumn.Value}'",
            $"operator='{operatorToken}'"
        );
    }

    /// <summary>
    /// Returns <see langword="true"/> when both rewritten predicates share the same semantic key after unified-alias
    /// rewrite, ignoring parameter-name differences.
    /// </summary>
    /// <param name="left">The first rewritten predicate.</param>
    /// <param name="right">The second rewritten predicate.</param>
    /// <returns><see langword="true"/> when the semantic key collides; otherwise <see langword="false"/>.</returns>
    private static bool HasDuplicateSemanticKey(RewrittenPredicate left, RewrittenPredicate right)
    {
        return string.Equals(
                GetTargetSortKey(left.Target),
                GetTargetSortKey(right.Target),
                StringComparison.Ordinal
            )
            && string.Equals(
                left.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                right.PresenceColumn?.Value ?? MissingPresenceColumnSortValue,
                StringComparison.Ordinal
            )
            && string.Equals(
                left.CanonicalColumn.Value,
                right.CanonicalColumn.Value,
                StringComparison.Ordinal
            )
            && left.Operator == right.Operator;
    }

    /// <summary>
    /// Creates a deterministic exception for a duplicate semantic predicate set, listing all colliding original
    /// columns and parameter names in stable ordinal order.
    /// </summary>
    /// <param name="rewrittenPredicates">The full rewritten and sorted predicate list.</param>
    /// <param name="startIndex">Start index of the collision group.</param>
    /// <param name="endExclusiveIndex">End (exclusive) index of the collision group.</param>
    /// <returns>An exception describing the duplicate semantic predicate collision.</returns>
    private static InvalidOperationException CreateDuplicateSemanticPredicateException(
        IReadOnlyList<RewrittenPredicate> rewrittenPredicates,
        int startIndex,
        int endExclusiveIndex
    )
    {
        var collidingOriginalColumns = new List<string>(endExclusiveIndex - startIndex);
        var collidingParameterNames = new List<string>(endExclusiveIndex - startIndex);

        for (var index = startIndex; index < endExclusiveIndex; index++)
        {
            collidingOriginalColumns.Add(rewrittenPredicates[index].OriginalColumn.Value);
            collidingParameterNames.Add(rewrittenPredicates[index].ParameterName);
        }

        collidingOriginalColumns.Sort(StringComparer.Ordinal);
        collidingParameterNames.Sort(StringComparer.Ordinal);

        return new InvalidOperationException(
            $"Duplicate predicate after unified alias rewrite for semantic key ({FormatSemanticKey(rewrittenPredicates[startIndex])}). "
                + $"Colliding original columns: [{FormatCollisionValues(collidingOriginalColumns)}]. "
                + $"Colliding parameter names: [{FormatCollisionValues(collidingParameterNames)}]."
        );
    }

    /// <summary>
    /// Formats values as a deterministic, comma-delimited list of single-quoted tokens.
    /// </summary>
    /// <param name="values">Values to format.</param>
    /// <returns>A comma-delimited list of quoted values.</returns>
    private static string FormatCollisionValues(IReadOnlyList<string> values)
    {
        return string.Join(", ", values.Select(static value => $"'{value}'"));
    }

    /// <summary>
    /// Creates a deterministic exception describing a null entry in the predicates list.
    /// </summary>
    private static ArgumentException CreateNullPredicateEntryException()
    {
        var predicatesName = nameof(PageDocumentIdQuerySpec.Predicates);
        return BuildArgumentException($"{predicatesName} must not contain null entries.", predicatesName);
    }

    /// <summary>
    /// Helper that constructs an <see cref="ArgumentException"/> from a runtime-supplied parameter name.
    /// Routing through a parameter prevents the analyzer from statically tying the literal property
    /// name to the enclosing method's argument list (which it never matches for these record-spec helpers).
    /// </summary>
    private static ArgumentException BuildArgumentException(string message, string paramName)
    {
        return new ArgumentException(message, paramName);
    }

    /// <summary>
    /// Creates a deterministic exception describing a filter/mode parameter-name collision.
    /// </summary>
    private static ArgumentException CreateFilterModeParameterCollisionException(
        string filterParameterName,
        PageCandidateModeParameter modeParameter,
        string paramName
    )
    {
        return new ArgumentException(
            $"Filter parameter name '{filterParameterName}' collides with candidate mode parameter name "
                + $"'{modeParameter.Name}' (case-insensitive). "
                + $"Rename the filter parameter or change {modeParameter.PropertyName}.",
            paramName
        );
    }

    /// <summary>
    /// Creates a deterministic exception describing a collision between two mode-owned parameter names.
    /// </summary>
    private static ArgumentException CreateModeParameterCollisionException(
        PageCandidateModeParameter first,
        PageCandidateModeParameter second
    )
    {
        return BuildArgumentException(
            "Candidate mode parameter names must be distinct (case-insensitive). "
                + $"{first.PropertyName}='{first.Name}', {second.PropertyName}='{second.Name}'. "
                + $"Rename either {first.PropertyName} or {second.PropertyName}.",
            nameof(PageDocumentIdQuerySpec.Mode)
        );
    }

    /// <summary>
    /// Creates a deterministic exception describing duplicate filter parameter names.
    /// </summary>
    private static ArgumentException CreateDuplicateFilterParameterNamesException(
        IReadOnlyList<string[]> duplicateGroups,
        string paramName
    )
    {
        var formattedGroups = duplicateGroups
            .Select(static group => $"[{FormatCollisionValues(group)}]")
            .ToArray();

        return new ArgumentException(
            "Duplicate filter parameter names are not allowed (case-insensitive). "
                + $"Colliding names: [{string.Join(", ", formattedGroups)}]. "
                + "Rename filter parameters so each name is unique.",
            paramName
        );
    }

    /// <summary>
    /// Returns a stable sort key for the SQL-side predicate target.
    /// </summary>
    private static string GetTargetSortKey(QueryPredicateTarget target)
    {
        return target switch
        {
            QueryPredicateTarget.RootColumn => nameof(QueryPredicateTarget.RootColumn),
            QueryPredicateTarget.DocumentUuid => nameof(QueryPredicateTarget.DocumentUuid),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported query predicate target sort key."
            ),
        };
    }

    /// <summary>
    /// Returns the fixed SQL alias for a predicate target.
    /// </summary>
    private static string GetTargetAlias(QueryPredicateTarget target)
    {
        return target switch
        {
            QueryPredicateTarget.RootColumn => _rootAlias,
            QueryPredicateTarget.DocumentUuid => _documentAlias,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported query predicate target alias."
            ),
        };
    }

    /// <summary>
    /// Represents a predicate rewritten into canonical storage-column form, with an optional presence gate
    /// for unified-alias mappings.
    /// </summary>
    /// <param name="Target">The SQL-side predicate target.</param>
    /// <param name="OriginalColumn">The original API-bound predicate column.</param>
    /// <param name="CanonicalColumn">The canonical storage column used for SQL emission.</param>
    /// <param name="PresenceColumn">An optional presence gate column that must be <c>IS NOT NULL</c>.</param>
    /// <param name="Operator">The comparison operator.</param>
    /// <param name="ParameterName">The bare SQL parameter name that supplies the value.</param>
    /// <param name="ScalarKind">Optional scalar-kind metadata for provider-specific comparison behavior.</param>
    private readonly record struct RewrittenPredicate(
        QueryPredicateTarget Target,
        DbColumnName OriginalColumn,
        DbColumnName CanonicalColumn,
        DbColumnName? PresenceColumn,
        QueryComparisonOperator Operator,
        string ParameterName,
        ScalarKind? ScalarKind
    );
}
