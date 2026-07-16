// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

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

    /// <summary>
    /// Predicate targets a shared <c>dms.Descriptor</c> table column and therefore requires the descriptor join.
    /// No production planner currently emits this target (descriptor queries root on <c>dms.Descriptor</c>
    /// and use <see cref="RootColumn"/>); it is retained for callers that filter descriptor columns from a
    /// non-descriptor root.
    /// </summary>
    /// <param name="Column">The shared descriptor-table column.</param>
    public sealed record DescriptorColumn(DbColumnName Column) : QueryPredicateTarget;
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
/// Namespace authorization checks. The compiler emits each as a root-table <c>IS NOT NULL</c> + prefix-LIKE
/// predicate and AND-combines them into a single outer group placed before the relationship OR group.
/// </param>
/// <param name="NamespacePrefixParameterization">
/// Dialect-specific namespace prefix parameterization shared by SQL emission and runtime binding. Required
/// when <paramref name="NamespaceChecks" /> is non-empty; ignored otherwise.
/// </param>
public sealed record PageDocumentIdAuthorizationSpec(
    IReadOnlyList<PageDocumentIdAuthorizationStrategy> Strategies,
    AuthorizationClaimEducationOrganizationIdParameterization? ClaimEducationOrganizationIdParameterization =
        null,
    IReadOnlyList<NamespaceAuthorizationCheckSpec>? NamespaceChecks = null,
    NamespacePrefixParameterization? NamespacePrefixParameterization = null
);

/// <summary>
/// Input specification for compiling page-<c>DocumentId</c> query SQL.
/// </summary>
/// <param name="RootTable">The resource root table queried for <c>DocumentId</c>.</param>
/// <param name="Predicates">Value predicates are treated as an unordered set; compiler emits them in deterministic sorted order after rewrite</param>
/// <param name="UnifiedAliasMappingsByColumn">
/// Unified alias metadata keyed by API-bound alias/binding column for canonical-column predicate rewrite.
/// </param>
/// <param name="OffsetParameterName">The bare paging offset parameter name.</param>
/// <param name="LimitParameterName">The bare paging limit parameter name.</param>
/// <param name="IncludeTotalCountSql">
/// Indicates whether the compiler should include total-count SQL in the emitted plan.
/// </param>
/// <param name="Authorization">
/// Optional DMS-1055 authorization inputs. When present, relationship predicates are applied to both page and
/// total-count SQL.
/// </param>
public sealed record PageDocumentIdQuerySpec(
    DbTableName RootTable,
    IReadOnlyList<QueryValuePredicate> Predicates,
    IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> UnifiedAliasMappingsByColumn,
    string OffsetParameterName = "offset",
    string LimitParameterName = "limit",
    bool IncludeTotalCountSql = false,
    PageDocumentIdAuthorizationSpec? Authorization = null
);
