// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// One planned namespace authorization check, addressing a resolved root-table column.
/// </summary>
/// <param name="Index">
/// Zero-based position in the namespace planner's emitted check list. Matches the AUTH1
/// payload index emitted by the SQL batch on failure.
/// </param>
/// <param name="ValueSource">Whether this check evaluates the stored row or the proposed request body.</param>
/// <param name="RootTable">The concrete root table of the subject resource. Always a root table — never a child collection.</param>
/// <param name="NamespaceColumn">The resolved root-table column carrying the Namespace value.</param>
/// <param name="StrategyName">The configured strategy name — always <c>NamespaceBased</c>.</param>
/// <param name="RawConfiguredIndex">
/// Zero-based position of the originating <c>NamespaceBased</c> strategy in the CMS-configured strategy
/// list. Callers must stamp this from that strategy;
/// <see cref="RelationalAuthorizationPlanner"/> does so for every spec the namespace planner emits.
/// <para>
/// The default of <c>0</c> is not a safe "unset" value — it claims the check was configured first. Custom
/// views are only validated when their own configured index is strictly less than the terminal's, so a spec
/// left at the default suppresses custom-view validation ahead of that terminal, which is the masking this
/// ordering model exists to prevent. Treat the default as "configured first", never as "unknown".
/// </para>
/// </param>
public sealed record NamespaceAuthorizationCheckSpec(
    int Index,
    NamespaceAuthorizationCheckValueSource ValueSource,
    DbTableName RootTable,
    DbColumnName NamespaceColumn,
    string StrategyName = AuthorizationStrategyNameConstants.NamespaceBased,
    int RawConfiguredIndex = 0
);
