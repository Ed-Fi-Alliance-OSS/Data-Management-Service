// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input for compiling a stored-value relationship authorization check for one resolved root document.
/// </summary>
public sealed record SingleRecordRelationshipAuthorizationSqlSpec(
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index,
    string DocumentIdParameterName = "DocumentId"
);

/// <summary>
/// Compiled SQL and runtime parameter binding metadata for one stored-value relationship authorization check.
/// </summary>
public sealed record SingleRecordRelationshipAuthorizationSqlPlan(
    string AuthorizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder
);
