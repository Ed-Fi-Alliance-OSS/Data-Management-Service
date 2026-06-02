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
public sealed record NamespaceAuthorizationCheckSpec(
    int Index,
    NamespaceAuthorizationCheckValueSource ValueSource,
    DbTableName RootTable,
    DbColumnName NamespaceColumn,
    string StrategyName = AuthorizationStrategyNameConstants.NamespaceBased
);
