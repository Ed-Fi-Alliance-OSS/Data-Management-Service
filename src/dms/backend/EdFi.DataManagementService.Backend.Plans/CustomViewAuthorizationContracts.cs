// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

public sealed record CustomViewAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    DbTableName RootTable,
    DbColumnName RootDocumentIdColumn,
    DbTableName AuthView,
    DbColumnName AuthViewDocumentIdColumn,
    IReadOnlyList<ColumnPathStep> PathToBasisResource
);

public abstract record CustomViewAuthorizationPlanOutcome
{
    private CustomViewAuthorizationPlanOutcome() { }

    public sealed record Plan(IReadOnlyList<CustomViewAuthorizationCheckSpec> Checks)
        : CustomViewAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured custom view could not be planned. <paramref name="PlannedChecks"/> carries the
    /// custom views that <em>did</em> plan successfully, so a caller can still validate the ones configured
    /// ahead of the earliest failure before reporting it. Custom views are AND filters executing in
    /// CMS-configured order, so an earlier missing or non-conforming <c>auth.{StrategyName}</c> must surface
    /// its own error rather than being hidden by a later strategy's planning failure.
    /// </summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<CustomViewAuthorizationCheckSpec> PlannedChecks
    ) : CustomViewAuthorizationPlanOutcome;
}

/// <summary>
/// What a single-record custom view-based check evaluates the basis <c>DocumentId</c> against.
/// </summary>
/// <remarks>
/// GET-many needs no equivalent: it filters a page rather than deciding one record, so it has neither a
/// stored target to address nor a proposed value to bind.
/// </remarks>
public abstract record CustomViewAuthorizationCheckTarget
{
    private CustomViewAuthorizationCheckTarget() { }

    /// <summary>
    /// The stored row identified by the target <c>DocumentId</c> parameter. The resolved path is walked
    /// forward from that row to reach the basis resource's <c>DocumentId</c>.
    /// </summary>
    public sealed record Stored(DbTableName RootTable, DbColumnName RootDocumentIdColumn)
        : CustomViewAuthorizationCheckTarget;

    /// <summary>
    /// The proposed request body. The resolved path's first hop is bound as a parameter taken from the
    /// finalized root row — reference resolution has already turned the submitted reference into a
    /// <c>..._DocumentId</c> value by then — and any remaining hops are joined in SQL.
    /// </summary>
    public sealed record Proposed(DbTableName RootTable, CustomViewAuthorizationProposedValueBinding Binding)
        : CustomViewAuthorizationCheckTarget;

    /// <summary>
    /// The proposed basis <c>DocumentId</c> is the not-yet-assigned <c>DocumentId</c> of the row being
    /// created, so view membership cannot be proven. Only reachable when the basis resource <em>is</em> the
    /// subject resource and the operation is a POST that resolves to a create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The planner cannot tell a create from an update: a POST resolves to one or the other only when the
    /// target capture runs. So this target is planned for every proposed check on a self-basis strategy, and
    /// carries both execution branches:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>No captured target (create).</b> Deny with §2.4. ODS reaches the same dead end — the basis row does
    /// not exist yet, so no view row can reference it. The membership SQL is not issued; the custom view is
    /// still validated for existence and contract, so a misconfigured view keeps its 500 rather than being
    /// reported as this 403.
    /// </description></item>
    /// <item><description>
    /// <b>Captured target (update).</b> Satisfied. A document's own <c>DocumentId</c> is immutable, so the
    /// proposed basis value is the very value the paired stored check already authorized against this view;
    /// re-checking it could only produce the same answer.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed record ProposedSelfBasisUnavailable(DbTableName RootTable)
        : CustomViewAuthorizationCheckTarget;
}

/// <summary>
/// The root-row column a proposed custom-view check reads its basis value from.
/// </summary>
/// <param name="Table">The concrete root table the value is bound from.</param>
/// <param name="Column">The root-table column holding the first hop of the resolved basis path.</param>
/// <param name="LogicalKey">Stable identity for the binding, used in diagnostics.</param>
/// <param name="ParameterSeed">Seed the SQL compiler derives a collision-free parameter name from.</param>
public sealed record CustomViewAuthorizationProposedValueBinding(
    DbTableName Table,
    DbColumnName Column,
    string LogicalKey,
    string ParameterSeed
);

/// <summary>
/// One planned single-record custom view-based authorization check, in emission order.
/// </summary>
/// <param name="ConfiguredStrategy">
/// The CMS-configured strategy. Its <c>RawConfiguredIndex</c> is what orders this check against
/// <c>NamespaceBased</c>, since both are AND filters executing in configured order.
/// </param>
/// <param name="Index">
/// Zero-based position in the custom-view check list this request emits. Matches the index carried by the
/// <c>cv1</c> AUTH1 payload on failure. Independent of the namespace and relationship index spaces, because
/// each payload family owns its own discriminator.
/// </param>
/// <param name="ValueSource">Whether this check evaluates the stored row or the proposed request body.</param>
/// <param name="AuthView">The <c>auth.{StrategyName}</c> view.</param>
/// <param name="AuthViewDocumentIdColumn">The view's <c>DocumentId</c> output column.</param>
/// <param name="PathToBasisResource">
/// The resolved column path from the subject to the basis resource's <c>DocumentId</c>.
/// </param>
/// <param name="CheckTarget">What the basis <c>DocumentId</c> is evaluated against.</param>
/// <param name="BasisResource">The basis resource extracted from the strategy name.</param>
/// <param name="ReadableSecurableElements">
/// User-facing names of the securable element this check decides on, in path order, for the §2.4/2.7/2.8
/// wording. A composite-identity basis contributes more than one, which is what the multiple-element
/// phrasing in §2.4 exists for.
/// </param>
/// <param name="FailureHint">
/// The §"Authorization Failure Hints" sentence for this strategy, without a <c>Hint:</c> prefix.
/// </param>
public sealed record SingleRecordCustomViewAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int Index,
    CustomViewAuthorizationCheckValueSource ValueSource,
    DbTableName AuthView,
    DbColumnName AuthViewDocumentIdColumn,
    IReadOnlyList<ColumnPathStep> PathToBasisResource,
    CustomViewAuthorizationCheckTarget CheckTarget,
    QualifiedResourceName BasisResource,
    IReadOnlyList<string> ReadableSecurableElements,
    string FailureHint
);

internal static class PageDocumentIdCustomViewAdapter
{
    public static IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> AdaptFromChecks(
        IReadOnlyList<CustomViewAuthorizationCheckSpec> checks
    )
    {
        if (checks is null || checks.Count == 0)
        {
            return [];
        }

        return checks
            .Select(check => new PageDocumentIdAuthorizationCustomViewCheck(
                check.ConfiguredStrategy.StrategyName,
                check.ConfiguredStrategy.RawConfiguredIndex,
                check.AuthView,
                check.AuthViewDocumentIdColumn,
                check.PathToBasisResource,
                check.RootTable,
                check.RootDocumentIdColumn
            ))
            .ToArray();
    }
}
