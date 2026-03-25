// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Classifies the DML event for an auth hierarchy maintenance trigger.
/// </summary>
public enum AuthHierarchyTriggerEvent
{
    /// <summary>INSERT trigger.</summary>
    Insert,

    /// <summary>UPDATE trigger (hierarchical entities only).</summary>
    Update,

    /// <summary>DELETE trigger.</summary>
    Delete,
}

/// <summary>
/// Shared table and column name constants for the <c>auth.*</c> schema.
/// </summary>
public static class AuthTableNames
{
    public static readonly DbSchemaName AuthSchema = new("auth");

    public static readonly DbTableName EdOrgIdToEdOrgId = new(
        AuthSchema,
        "EducationOrganizationIdToEducationOrganizationId"
    );

    public static readonly DbColumnName SourceEdOrgId = new("SourceEducationOrganizationId");
    public static readonly DbColumnName TargetEdOrgId = new("TargetEducationOrganizationId");

    // Column names referenced by the people auth views
    public static readonly DbColumnName SchoolIdUnified = new("SchoolId_Unified");
    public static readonly DbColumnName StudentDocumentId = new("Student_DocumentId");
    public static readonly DbColumnName ContactDocumentId = new("Contact_DocumentId");
    public static readonly DbColumnName StaffDocumentId = new("Staff_DocumentId");
    public static readonly DbColumnName EdOrgEdOrgId = new("EducationOrganization_EducationOrganizationId");
}

/// <summary>
/// Describes a denormalized parent EducationOrganization identity column on a concrete
/// EdOrg table. The column stores the parent's EducationOrganizationId directly, so
/// triggers can reference it without joining the parent table.
/// </summary>
/// <param name="DenormalizedParentIdColumn">
/// The denormalized parent identity column on the concrete EdOrg table
/// (e.g., <c>StateEducationAgency_EducationOrganizationId</c> on the
/// <c>LocalEducationAgency</c> table).
/// </param>
public sealed record AuthParentEdOrgFk(DbColumnName DenormalizedParentIdColumn);

/// <summary>
/// Describes a concrete EducationOrganization entity for auth hierarchy trigger generation.
/// </summary>
/// <param name="EntityName">
/// The logical entity name (e.g., <c>School</c>, <c>LocalEducationAgency</c>).
/// Used for deterministic ordering and trigger naming.
/// </param>
/// <param name="Table">
/// The concrete resource table (e.g., <c>edfi.School</c>).
/// </param>
/// <param name="IdentityColumn">
/// The <c>EducationOrganizationId</c> column on the concrete table that holds
/// this entity's natural key in the EdOrg hierarchy.
/// </param>
/// <param name="ParentEdOrgFks">
/// Parent FK columns referencing other EducationOrganization entities.
/// Empty for leaf entities (e.g., <c>StateEducationAgency</c>).
/// Contains one or more entries for hierarchical entities (e.g., <c>School</c> has one,
/// <c>LocalEducationAgency</c> has three).
/// </param>
public sealed record AuthEdOrgEntity(
    string EntityName,
    DbTableName Table,
    DbColumnName IdentityColumn,
    IReadOnlyList<AuthParentEdOrgFk> ParentEdOrgFks
);

/// <summary>
/// The complete EducationOrganization hierarchy for auth DDL trigger generation.
/// Produced by <see cref="DeriveAuthHierarchyPass"/> and consumed by
/// <c>RelationalModelDdlEmitter</c> (via <c>AuthTriggerBodyEmitter</c>).
/// </summary>
/// <param name="EntitiesInNameOrder">
/// All concrete EducationOrganization entities, sorted alphabetically by
/// <see cref="AuthEdOrgEntity.EntityName"/> for deterministic DDL emission.
/// </param>
public sealed record AuthEdOrgHierarchy(IReadOnlyList<AuthEdOrgEntity> EntitiesInNameOrder);
