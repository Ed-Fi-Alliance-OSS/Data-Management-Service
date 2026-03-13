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
}

/// <summary>
/// Describes a parent EducationOrganization FK column on a concrete EdOrg table.
/// The FK column stores a <c>DocumentId</c> pointing to the parent entity. To resolve
/// the parent's <c>EducationOrganizationId</c>, the trigger joins via
/// <see cref="ParentTable"/>.<see cref="ParentIdentityColumn"/>.
/// </summary>
/// <param name="FkColumn">
/// The <c>DocumentId</c>-based FK column on the concrete EdOrg table
/// (e.g., <c>LocalEducationAgency_DocumentId</c>).
/// </param>
/// <param name="ParentTable">
/// The table to join for resolving the parent's EducationOrganizationId.
/// For concrete parent references, this is the parent's resource table
/// (e.g., <c>edfi.LocalEducationAgency</c>).
/// For abstract parent references, this is the abstract identity table
/// (e.g., <c>edfi.EducationOrganizationIdentity</c>).
/// </param>
/// <param name="ParentIdentityColumn">
/// The <c>EducationOrganizationId</c> column on <see cref="ParentTable"/>
/// used to resolve the parent's natural key for the auth hierarchy.
/// </param>
public sealed record AuthParentEdOrgFk(
    DbColumnName FkColumn,
    DbTableName ParentTable,
    DbColumnName ParentIdentityColumn
);

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
/// Produced by the hierarchy compiler from the <see cref="DerivedRelationalModelSet"/>
/// and consumed by the <c>AuthDdlEmitter</c>.
/// </summary>
/// <param name="EntitiesInNameOrder">
/// All concrete EducationOrganization entities, sorted alphabetically by
/// <see cref="AuthEdOrgEntity.EntityName"/> for deterministic DDL emission.
/// </param>
public sealed record AuthEdOrgHierarchy(IReadOnlyList<AuthEdOrgEntity> EntitiesInNameOrder);
