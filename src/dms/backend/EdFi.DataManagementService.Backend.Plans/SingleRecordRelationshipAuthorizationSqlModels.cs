// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input for compiling a relationship authorization check for one stored or proposed root record.
/// </summary>
public sealed record SingleRecordRelationshipAuthorizationSqlSpec(
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index,
    string DocumentIdParameterName = "DocumentId",
    IReadOnlyList<string>? ReservedParameterNames = null
);

/// <summary>
/// Generated SQL parameter for one proposed authorization subject value.
/// </summary>
public sealed record RelationshipAuthorizationProposedValueSqlParameter(
    int StrategyOrdinal,
    int SubjectOrdinal,
    string ParameterName
);

/// <summary>
/// Compiled SQL and runtime parameter binding metadata for one relationship authorization check.
/// </summary>
public sealed record SingleRecordRelationshipAuthorizationSqlPlan(
    string AuthorizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder,
    IReadOnlyList<RelationshipAuthorizationProposedValueSqlParameter> ProposedValueParametersInOrder
);
