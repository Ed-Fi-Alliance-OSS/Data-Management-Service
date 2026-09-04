// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL-side predicate target for a page-<c>DocumentId</c> query.
/// </summary>
public abstract record QueryPredicateTarget
{
    private QueryPredicateTarget() { }

    /// <summary>
    /// Predicate targets a root-table column.
    /// </summary>
    /// <param name="Column">The root-table column.</param>
    public sealed record RootColumn(DbColumnName Column) : QueryPredicateTarget;

    /// <summary>
    /// Predicate targets <c>dms.Document.DocumentUuid</c> and therefore requires the special-case document join.
    /// </summary>
    public sealed record DocumentUuid : QueryPredicateTarget;
}

/// <summary>
/// Represents a single value predicate over a root-table column.
/// </summary>
/// <param name="Target">The SQL-side predicate target.</param>
/// <param name="Operator">The value-comparison operator.</param>
/// <param name="ParameterName">The bare SQL parameter name that supplies the value.</param>
/// <param name="ScalarKind">
/// Optional scalar-kind metadata for the predicate value. Used by SQL emission for provider-specific string-comparison
/// semantics.
/// </param>
public sealed record QueryValuePredicate(
    QueryPredicateTarget Target,
    QueryComparisonOperator Operator,
    string ParameterName,
    ScalarKind? ScalarKind = null
)
{
    /// <summary>
    /// Initializes a root-column predicate.
    /// </summary>
    public QueryValuePredicate(
        DbColumnName Column,
        QueryComparisonOperator Operator,
        string ParameterName,
        ScalarKind? ScalarKind = null
    )
        : this(new QueryPredicateTarget.RootColumn(Column), Operator, ParameterName, ScalarKind) { }
}

/// <summary>
/// One authorization subject used by page-<c>DocumentId</c> relationship authorization.
/// </summary>
/// <param name="Table">The table owning the authorization subject column.</param>
/// <param name="Column">The subject column.</param>
/// <param name="AuthObject">The auth object used to evaluate this subject.</param>
/// <param name="Contributors">Schema securable elements that contributed this executable subject.</param>
public abstract record PageDocumentIdAuthorizationSubject(
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors
);

/// <summary>
/// One concrete root-table EducationOrganization authorization subject.
/// </summary>
public sealed record PageDocumentIdAuthorizationEdOrgSubject(
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors
) : PageDocumentIdAuthorizationSubject(Table, Column, AuthObject, Contributors);

/// <summary>
/// One Student, Contact, or Staff authorization subject with DMS-1056 person path metadata.
/// </summary>
/// <param name="PersonMetadata">DocumentId path metadata used to bind the root document to a person auth view.</param>
public sealed record PageDocumentIdAuthorizationPersonSubject(
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors,
    RelationshipAuthorizationPersonSubjectMetadata PersonMetadata
) : PageDocumentIdAuthorizationSubject(Table, Column, AuthObject, Contributors);

/// <summary>
/// One relationship-based authorization strategy with its participating subjects.
/// </summary>
/// <param name="StrategyName">The configured strategy name used for diagnostics.</param>
/// <param name="Subjects">
/// The participating authorization subjects. Multiple subjects are combined with AND in this order.
/// </param>
public sealed record PageDocumentIdAuthorizationStrategy(
    string StrategyName,
    IReadOnlyList<PageDocumentIdAuthorizationSubject> Subjects
);

/// <summary>
/// One custom view-based (<c>auth.{StrategyName}</c>) authorization check. The compiler emits it as an AND
/// filter restricting the subject's <c>DocumentId</c> to those reachable from the auth view along
/// <paramref name="PathToBasisResource" />.
/// </summary>
public sealed record PageDocumentIdAuthorizationCustomViewCheck(
    string StrategyName,
    int RawConfiguredIndex,
    DbTableName AuthView,
    DbColumnName AuthViewDocumentIdColumn,
    IReadOnlyList<ColumnPathStep> PathToBasisResource,
    DbTableName RootTable,
    DbColumnName RootDocumentIdColumn
);

/// <summary>
/// Optional authorization inputs for page-<c>DocumentId</c> query compilation.
/// </summary>
/// <param name="Strategies">
/// Effective relationship authorization strategies. Strategies are combined with OR.
/// </param>
/// <param name="ClaimEducationOrganizationIdParameterization">
/// Dialect-specific claim EdOrg parameterization shared by SQL emission and runtime binding. Required when
/// <paramref name="Strategies" /> is non-empty; ignored when the strategy list is empty.
/// </param>
/// <param name="NamespaceChecks">
/// Namespace authorization checks. The compiler emits each as its own root-table <c>IS NOT NULL</c> +
/// prefix-LIKE AND predicate rather than as one combined group: namespace and custom-view checks share a
/// single ordering by <c>RawConfiguredIndex</c>, so they interleave in CMS-configured order. The
/// relationship OR group is emitted after all of them.
/// </param>
/// <param name="NamespacePrefixParameterization">
/// Dialect-specific namespace prefix parameterization shared by SQL emission and runtime binding. Required
/// when <paramref name="NamespaceChecks" /> is non-empty; ignored otherwise.
/// </param>
/// <param name="CustomViewChecks">
/// Custom view-based authorization checks. Each is emitted as an AND predicate, ordered against
/// <paramref name="NamespaceChecks" /> by <c>RawConfiguredIndex</c> as described above.
/// </param>
/// <param name="OwnershipTokenParameterization">
/// Dialect-specific ownership-token parameterization for the <c>OwnershipBased</c> page filter, shared by SQL
/// emission and runtime binding. When present, the compiler joins <c>dms.Document</c> once and emits the
/// <c>CreatedByOwnershipTokenId</c> membership predicate as the last AND filter: after every namespace and
/// custom-view filter whatever position CMS configured <c>OwnershipBased</c> at, and before the relationship
/// OR group. Page and total-count SQL share it. Null when <c>OwnershipBased</c> is not configured.
/// </param>
public sealed record PageDocumentIdAuthorizationSpec(
    IReadOnlyList<PageDocumentIdAuthorizationStrategy> Strategies,
    AuthorizationClaimEducationOrganizationIdParameterization? ClaimEducationOrganizationIdParameterization =
        null,
    IReadOnlyList<NamespaceAuthorizationCheckSpec>? NamespaceChecks = null,
    NamespacePrefixParameterization? NamespacePrefixParameterization = null,
    IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? CustomViewChecks = null,
    OwnershipTokenParameterization? OwnershipTokenParameterization = null
);

/// <summary>
/// The canonical bare SQL parameter names used by each page-<c>DocumentId</c> candidate mode.
/// </summary>
public static class PageCandidateParameterNames
{
    /// <summary>The traditional paging offset parameter name.</summary>
    public const string Offset = "offset";

    /// <summary>The traditional paging limit parameter name.</summary>
    public const string Limit = "limit";

    /// <summary>
    /// The cursor inclusive lower anchor bound parameter name. The name is deliberately unchanged now
    /// that the bound can be a <c>ContentVersion</c> rather than a <c>DocumentId</c>: renaming it would
    /// churn every compiled-SQL golden for no behavior change, and the anchor the value belongs to is
    /// named by the mode's ordering rather than by the parameter.
    /// </summary>
    public const string CursorInclusiveMinimum = "cursorMin";

    /// <summary>The cursor inclusive upper anchor bound parameter name.</summary>
    public const string CursorInclusiveMaximum = "cursorMax";

    /// <summary>The cursor page size parameter name.</summary>
    public const string PageSize = "pageSize";

    /// <summary>
    /// The requested partition count parameter name, matching the public query parameter and the
    /// normative partition SQL. Reserved and collision-validated here; bound by partition-window SQL.
    /// </summary>
    public const string PartitionCount = "number";

    /// <summary>
    /// The minimum partition size parameter name. Reserved on the same terms as
    /// <see cref="PartitionCount" />.
    /// </summary>
    public const string MinimumPartitionSize = "minimumPartitionSize";
}

/// <summary>
/// How a page-<c>DocumentId</c> query selects from the shared candidate relation.
/// </summary>
/// <remarks>
/// An explicit choice rather than nullable combinations, so a cursor page with a total count and an
/// unpaged candidate relation with a page size are both unrepresentable rather than rejected at
/// runtime. Every mode compiles the same candidate root, predicates, and authorization; only the
/// range, ordering, and size clauses differ.
/// <para>
/// Every mode carries an ordering, because the anchor follows the ordering: a page's bounds, a
/// partition's boundaries, and the continuation token issued for either are all expressed in the key
/// the mode names. A mode whose ordering did not match the token issued for it would produce a walk
/// that skips rows.
/// </para>
/// </remarks>
public abstract record PageCandidateMode
{
    private PageCandidateMode() { }

    /// <summary>
    /// Traditional limit/offset page selection.
    /// </summary>
    /// <param name="OffsetParameterName">The bare paging offset parameter name.</param>
    /// <param name="LimitParameterName">The bare paging limit parameter name.</param>
    /// <param name="IncludeTotalCountSql">
    /// Indicates whether the compiler should include total-count SQL in the emitted plan.
    /// </param>
    /// <param name="OrderingMode">
    /// The page-selection ordering key, and therefore the anchor the page's continuation token is
    /// expressed in. Page membership follows this key while hydration output remains ordered by
    /// <c>DocumentId</c>.
    /// </param>
    public sealed record Traditional(
        string OffsetParameterName = PageCandidateParameterNames.Offset,
        string LimitParameterName = PageCandidateParameterNames.Limit,
        bool IncludeTotalCountSql = false,
        PageOrderingMode OrderingMode = PageOrderingMode.DocumentId
    ) : PageCandidateMode;

    /// <summary>
    /// Seek-based cursor page selection over an inclusive range of the anchor column, ordered by that
    /// same column.
    /// </summary>
    /// <param name="InclusiveMinimumParameterName">The inclusive lower bound parameter name.</param>
    /// <param name="InclusiveMaximumParameterName">The inclusive upper bound parameter name.</param>
    /// <param name="PageSizeParameterName">The page size parameter name.</param>
    /// <param name="OrderingMode">
    /// The anchor the range is expressed in and the key the page is ordered by. The two are the same
    /// key by construction: a page bounded on one column and ordered by another would return rows
    /// outside its own range.
    /// </param>
    public sealed record Cursor(
        string InclusiveMinimumParameterName = PageCandidateParameterNames.CursorInclusiveMinimum,
        string InclusiveMaximumParameterName = PageCandidateParameterNames.CursorInclusiveMaximum,
        string PageSizeParameterName = PageCandidateParameterNames.PageSize,
        PageOrderingMode OrderingMode = PageOrderingMode.DocumentId
    ) : PageCandidateMode;

    /// <summary>
    /// The unpaged, unordered candidate relation used for partition planning.
    /// </summary>
    /// <param name="PartitionCountParameterName">The reserved partition count parameter name.</param>
    /// <param name="MinimumPartitionSizeParameterName">
    /// The reserved minimum partition size parameter name.
    /// </param>
    /// <param name="OrderingMode">
    /// The anchor the consuming partition-window SQL ranks, sizes, and cuts boundaries on, and
    /// therefore the units of every range it returns. This relation still emits no ordering of its
    /// own — see the remarks — so the mode names the column rather than an <c>ORDER BY</c>, and it is
    /// the same column a page of the same request would be selected in.
    /// </param>
    /// <remarks>
    /// Emits no <c>ORDER BY</c>: the consumer wraps this relation in a common table expression and
    /// applies its own row numbering, and SQL Server rejects <c>ORDER BY</c> in a CTE that has no
    /// <c>TOP</c> or <c>OFFSET</c>. The two parameter names are reserved and collision-validated so a
    /// resource filter cannot shadow them, but no role is emitted until partition-window SQL binds them.
    /// </remarks>
    public sealed record UnpagedCandidates(
        string PartitionCountParameterName = PageCandidateParameterNames.PartitionCount,
        string MinimumPartitionSizeParameterName = PageCandidateParameterNames.MinimumPartitionSize,
        PageOrderingMode OrderingMode = PageOrderingMode.DocumentId
    ) : PageCandidateMode;
}

/// <summary>
/// One parameter name owned by a candidate mode.
/// </summary>
/// <param name="PropertyName">The mode property that supplied the name, used in diagnostics.</param>
/// <param name="Name">The bare SQL parameter name.</param>
/// <param name="Role">The plan role this name carries when compiled SQL binds it.</param>
/// <param name="IsBound">
/// Whether compiled SQL binds this name. Reserved-but-unbound names are validated and reserved
/// against filter collisions but excluded from a plan's parameter inventory, because an inventory
/// entry with no placeholder would fail runtime binding.
/// </param>
public readonly record struct PageCandidateModeParameter(
    string PropertyName,
    string Name,
    QuerySqlParameterRole Role,
    bool IsBound
);

/// <summary>
/// The single derivation of which parameter names a candidate mode owns, what plan role each name
/// carries, and whether compiled SQL binds it. Filter-parameter name allocation in the page keyset
/// planners and parameter validation plus inventory emission in <see cref="PageDocumentIdSqlCompiler" />
/// both read this, so the set of names a mode reserves cannot drift from the set it emits.
/// </summary>
/// <remarks>
/// Names come from the mode instance rather than from <see cref="PageCandidateParameterNames" />, so a
/// mode constructed with non-default names reserves the names it will actually emit.
/// </remarks>
public static class PageCandidateModeParameters
{
    /// <summary>
    /// Returns the parameter inventory the supplied mode owns, in canonical order.
    /// </summary>
    /// <param name="mode">The candidate selection mode.</param>
    public static IReadOnlyList<PageCandidateModeParameter> For(PageCandidateMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        return mode switch
        {
            PageCandidateMode.Traditional traditional =>
            [
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.Traditional.OffsetParameterName),
                    traditional.OffsetParameterName,
                    QuerySqlParameterRole.Offset,
                    IsBound: true
                ),
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.Traditional.LimitParameterName),
                    traditional.LimitParameterName,
                    QuerySqlParameterRole.Limit,
                    IsBound: true
                ),
            ],
            PageCandidateMode.Cursor cursor =>
            [
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.Cursor.InclusiveMinimumParameterName),
                    cursor.InclusiveMinimumParameterName,
                    QuerySqlParameterRole.CursorInclusiveMinimum,
                    IsBound: true
                ),
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.Cursor.InclusiveMaximumParameterName),
                    cursor.InclusiveMaximumParameterName,
                    QuerySqlParameterRole.CursorInclusiveMaximum,
                    IsBound: true
                ),
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.Cursor.PageSizeParameterName),
                    cursor.PageSizeParameterName,
                    QuerySqlParameterRole.PageSize,
                    IsBound: true
                ),
            ],
            PageCandidateMode.UnpagedCandidates unpaged =>
            [
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.UnpagedCandidates.PartitionCountParameterName),
                    unpaged.PartitionCountParameterName,
                    QuerySqlParameterRole.PartitionCount,
                    IsBound: false
                ),
                new PageCandidateModeParameter(
                    nameof(PageCandidateMode.UnpagedCandidates.MinimumPartitionSizeParameterName),
                    unpaged.MinimumPartitionSizeParameterName,
                    QuerySqlParameterRole.MinimumPartitionSize,
                    IsBound: false
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode.GetType().Name,
                "Unsupported page candidate mode."
            ),
        };
    }

    /// <summary>
    /// Returns the bare parameter names the supplied mode owns. Filter-name allocation reserves these
    /// and only these: reserving another mode's names would suffix a filter parameter over a collision
    /// this query does not have, which would move the SQL of a mode with no stake in the name.
    /// </summary>
    /// <param name="mode">The candidate selection mode.</param>
    public static IReadOnlyList<string> OwnedNames(PageCandidateMode mode)
    {
        return [.. For(mode).Select(static parameter => parameter.Name)];
    }
}

/// <summary>
/// Input specification for compiling page-<c>DocumentId</c> query SQL.
/// </summary>
/// <param name="RootTable">The resource root table queried for <c>DocumentId</c>.</param>
/// <param name="Predicates">Value predicates are treated as an unordered set; compiler emits them in deterministic sorted order after rewrite</param>
/// <param name="UnifiedAliasMappingsByColumn">
/// Unified alias metadata keyed by API-bound alias/binding column for canonical-column predicate rewrite.
/// </param>
/// <param name="Mode">
/// The candidate selection mode. A null value is normalized to
/// <see cref="PageCandidateMode.Traditional" /> with its own defaults; a record parameter default
/// cannot be a constructed instance, which is the only reason this is nullable.
/// </param>
/// <param name="Authorization">
/// Optional DMS-1055 authorization inputs. When present, relationship predicates are applied to both page and
/// total-count SQL.
/// </param>
public sealed record PageDocumentIdQuerySpec(
    DbTableName RootTable,
    IReadOnlyList<QueryValuePredicate> Predicates,
    IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> UnifiedAliasMappingsByColumn,
    PageCandidateMode? Mode = null,
    PageDocumentIdAuthorizationSpec? Authorization = null
);
