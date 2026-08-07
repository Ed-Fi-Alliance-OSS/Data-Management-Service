// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input for compiling the co-batched single-record custom view-based authorization SQL.
/// </summary>
/// <param name="Checks">
/// The planned checks in emission order. Each check's <c>Index</c> must equal its position, because the
/// <c>cv1</c> AUTH1 payload reports that index and the failure mapper resolves it positionally.
/// </param>
/// <param name="DocumentIdParameterName">
/// Bare parameter name carrying the stored row's <c>DocumentId</c>. Bound only when a stored check is present.
/// </param>
/// <param name="RowGuardPredicateSql">
/// Optional raw predicate appended as a <c>WHERE</c> clause to every emitted check select. When it is false
/// the check's result set is empty and none of its branches — including the abort device — evaluates, which
/// is how checks co-batched behind a captured target stay vacuous for a write that resolved to no existing
/// document.
/// </param>
public sealed record SingleRecordCustomViewAuthorizationSqlSpec(
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Checks,
    string DocumentIdParameterName,
    string? RowGuardPredicateSql = null
);

/// <summary>
/// Generated SQL parameter carrying one proposed custom-view basis value.
/// </summary>
public sealed record CustomViewAuthorizationProposedValueSqlParameter(int CheckIndex, string ParameterName);

/// <summary>
/// Compiled single-record custom view-based authorization SQL plan.
/// </summary>
/// <param name="AuthorizationSql">The compiled SQL command body. Empty when no check emits SQL.</param>
/// <param name="ParametersInOrder">Deterministic parameter metadata in plan order.</param>
/// <param name="ProposedValueParametersInOrder">
/// The proposed-value parameters the caller must bind, each tagged with the check it belongs to.
/// </param>
/// <param name="EmittedCheckIndexesInOrder">
/// The checks that produced a statement, in statement order. A self-basis proposed check produces none — its
/// answer depends on whether a target was captured and on the paired stored check's outcome, neither of which
/// SQL can see — so callers use this to map result sets back to checks rather than assuming one statement per
/// check.
/// </param>
public sealed record SingleRecordCustomViewAuthorizationSqlPlan(
    string AuthorizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder,
    IReadOnlyList<CustomViewAuthorizationProposedValueSqlParameter> ProposedValueParametersInOrder,
    IReadOnlyList<int> EmittedCheckIndexesInOrder
);
