// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// One core education organization type's arm of the projection statement.
/// </summary>
/// <param name="Resource">The core resource.</param>
/// <param name="Table">The resource's root table.</param>
/// <param name="Discriminator">The discriminator literal the arm projects, <c>Ed-Fi:ResourceName</c>.</param>
/// <param name="EducationOrganizationIdColumn">The identity column.</param>
/// <param name="NameOfInstitutionColumn">The column carrying <c>nameOfInstitution</c>.</param>
/// <param name="ShortNameOfInstitutionColumn">The column carrying <c>shortNameOfInstitution</c>.</param>
/// <param name="LocalEducationAgencyReferenceColumn">
/// The column storing the referenced local education agency's identifier, for a School.
/// </param>
/// <param name="ParentLocalEducationAgencyReferenceColumn">
/// The column storing the parent local education agency's identifier, for a local education agency.
/// </param>
/// <param name="EducationServiceCenterReferenceColumn">
/// The column storing the referenced service center's identifier, for a local education agency.
/// </param>
/// <param name="StateEducationAgencyReferenceColumn">
/// The column storing the referenced state agency's identifier, for a local education agency or a
/// service center.
/// </param>
public sealed record EducationOrganizationProjectionArm(
    QualifiedResourceName Resource,
    DbTableName Table,
    string Discriminator,
    DbColumnName EducationOrganizationIdColumn,
    DbColumnName NameOfInstitutionColumn,
    DbColumnName ShortNameOfInstitutionColumn,
    DbColumnName? LocalEducationAgencyReferenceColumn,
    DbColumnName? ParentLocalEducationAgencyReferenceColumn,
    DbColumnName? EducationServiceCenterReferenceColumn,
    DbColumnName? StateEducationAgencyReferenceColumn
);

/// <summary>
/// Result-column aliases emitted by the projection statement, in select order.
/// </summary>
public sealed record EducationOrganizationProjectionResultColumns(
    DbColumnName EducationOrganizationId,
    DbColumnName Discriminator,
    DbColumnName NameOfInstitution,
    DbColumnName ShortNameOfInstitution,
    DbColumnName LocalEducationAgencyReference,
    DbColumnName ParentLocalEducationAgencyReference,
    DbColumnName EducationServiceCenterReference,
    DbColumnName StateEducationAgencyReference
)
{
    /// <summary>
    /// The fixed aliases the provider row readers consume.
    /// </summary>
    public static EducationOrganizationProjectionResultColumns Default { get; } =
        new(
            new DbColumnName("EducationOrganizationId"),
            new DbColumnName("Discriminator"),
            new DbColumnName("NameOfInstitution"),
            new DbColumnName("ShortNameOfInstitution"),
            new DbColumnName("LocalEducationAgencyReference"),
            new DbColumnName("ParentLocalEducationAgencyReference"),
            new DbColumnName("EducationServiceCenterReference"),
            new DbColumnName("StateEducationAgencyReference")
        );
}

/// <summary>
/// The compiled projection statement.
/// </summary>
/// <param name="Sql">
/// One statement returning every core row ordered by identifier, limited to the value bound to
/// <paramref name="RowLimitParameter"/>.
/// </param>
/// <param name="RowLimitParameter">
/// The row-limit parameter. Bind it to
/// <see cref="EducationOrganizationProjectionSetResult.RowLimitFor"/> of the cap.
/// </param>
/// <param name="ArmsInOrder">The core arms in statement order.</param>
/// <param name="ResultColumns">The result-column aliases, in select order.</param>
public sealed record EducationOrganizationProjectionSqlPlan(
    string Sql,
    QuerySqlParameter RowLimitParameter,
    IReadOnlyList<EducationOrganizationProjectionArm> ArmsInOrder,
    EducationOrganizationProjectionResultColumns ResultColumns
);

/// <summary>
/// The outcome of compiling the projection statement from a mapping set.
/// </summary>
public abstract record EducationOrganizationProjectionSqlCompilation
{
    private EducationOrganizationProjectionSqlCompilation() { }

    /// <summary>
    /// The mapping set supports the projection.
    /// </summary>
    public sealed record Compiled(EducationOrganizationProjectionSqlPlan Plan)
        : EducationOrganizationProjectionSqlCompilation;

    /// <summary>
    /// The mapping set cannot be projected; the first incompatibility found.
    /// </summary>
    public sealed record Incompatible(EducationOrganizationProjectionMappingIncompatibility Incompatibility)
        : EducationOrganizationProjectionSqlCompilation;
}
