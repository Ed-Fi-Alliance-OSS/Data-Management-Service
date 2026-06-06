// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Input for compiling the relational token_info education organization lookup SQL.
/// </summary>
/// <param name="MappingSet">The selected runtime relational mapping set.</param>
/// <param name="ClaimEducationOrganizationIdParameterization">
/// Dialect-specific parameterization for the token's claimed EducationOrganizationIds.
/// </param>
public sealed record TokenInfoEducationOrganizationSqlSpec(
    MappingSet MappingSet,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization
);

/// <summary>
/// One concrete EducationOrganization projection arm in the token_info lookup.
/// </summary>
/// <param name="Resource">The concrete EducationOrganization resource.</param>
/// <param name="Table">The concrete resource root table.</param>
/// <param name="DocumentIdColumn">The root table column carrying <c>DocumentId</c>.</param>
/// <param name="EducationOrganizationIdColumn">
/// The concrete resource identity column projected as <c>EducationOrganizationId</c>.
/// </param>
/// <param name="NameOfInstitutionColumn">The root table column carrying <c>nameOfInstitution</c>.</param>
/// <param name="Discriminator">The relational discriminator rendered as <c>ProjectName:ResourceName</c>.</param>
public sealed record TokenInfoEducationOrganizationProjectionArm(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName DocumentIdColumn,
    DbColumnName EducationOrganizationIdColumn,
    DbColumnName NameOfInstitutionColumn,
    string Discriminator
);

/// <summary>
/// Result-column aliases emitted by the relational token_info education organization lookup SQL.
/// </summary>
public sealed record TokenInfoEducationOrganizationResultColumns(
    DbColumnName EducationOrganizationId,
    DbColumnName NameOfInstitution,
    DbColumnName Discriminator,
    DbColumnName AncestorDiscriminator,
    DbColumnName AncestorEducationOrganizationId
)
{
    /// <summary>
    /// The fixed result-column aliases consumed by token_info row readers.
    /// </summary>
    public static TokenInfoEducationOrganizationResultColumns Default { get; } =
        new(
            new DbColumnName("EducationOrganizationId"),
            new DbColumnName("NameOfInstitution"),
            new DbColumnName("Discriminator"),
            new DbColumnName("AncestorDiscriminator"),
            new DbColumnName("AncestorEducationOrganizationId")
        );
}

/// <summary>
/// Compiled SQL and metadata for the relational token_info education organization lookup.
/// </summary>
/// <param name="EducationOrganizationSql">SQL returning token_info education organization rows.</param>
/// <param name="ParametersInOrder">Runtime SQL parameter inventory in deterministic order.</param>
/// <param name="ProjectionArmsInOrder">Concrete EducationOrganization projection arms in SQL order.</param>
/// <param name="ResultColumns">The fixed output column aliases emitted by <paramref name="EducationOrganizationSql" />.</param>
public sealed record TokenInfoEducationOrganizationSqlPlan(
    string EducationOrganizationSql,
    IReadOnlyList<QuerySqlParameter> ParametersInOrder,
    IReadOnlyList<TokenInfoEducationOrganizationProjectionArm> ProjectionArmsInOrder,
    TokenInfoEducationOrganizationResultColumns ResultColumns
);
