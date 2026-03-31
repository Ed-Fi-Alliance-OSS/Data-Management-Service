// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Identifies the kind of securable element a resolved column path represents.
/// </summary>
public enum SecurableElementKind
{
    EducationOrganization,
    Namespace,
    Student,
    Contact,
    Staff,
}

/// <summary>
/// A single step in the column path chain used to resolve securable element authorization
/// columns. For EdOrg/Namespace elements, only one step is returned (with null target).
/// For person elements, one or more steps describe the join chain.
/// </summary>
/// <param name="SourceTable">The table containing the source column.</param>
/// <param name="SourceColumnName">The column on the source table (FK or identity column).</param>
/// <param name="TargetTable">The target table being joined (null for EdOrg/Namespace).</param>
/// <param name="TargetColumnName">The column on the target table (null for EdOrg/Namespace).</param>
public sealed record ColumnPathStep(
    DbTableName SourceTable,
    DbColumnName SourceColumnName,
    DbTableName? TargetTable,
    DbColumnName? TargetColumnName
);

/// <summary>
/// A resolved securable element column path with its element kind.
/// </summary>
/// <param name="Kind">The kind of securable element this path represents.</param>
/// <param name="Steps">The chain of column path steps to reach the authorization column.</param>
public sealed record ResolvedSecurableElementPath(
    SecurableElementKind Kind,
    IReadOnlyList<ColumnPathStep> Steps
);

/// <summary>
/// Securable element metadata for a single EducationOrganization path, carrying
/// both the JSON path and the MetaEd identity property name.
/// </summary>
/// <param name="JsonPath">The JSON path to the EdOrg identity value (e.g., <c>$.schoolReference.schoolId</c>).</param>
/// <param name="MetaEdName">The MetaEd identity property name (e.g., <c>SchoolId</c>).</param>
public sealed record EdOrgSecurableElement(string JsonPath, string MetaEdName);

/// <summary>
/// Per-resource securable element metadata extracted from ApiSchema.json.
/// Each list may be empty if the resource does not participate in that authorization kind.
/// </summary>
/// <param name="EducationOrganization">EdOrg securable element paths with MetaEd names.</param>
/// <param name="Namespace">Namespace JSON paths.</param>
/// <param name="Student">Student person JSON paths.</param>
/// <param name="Contact">Contact person JSON paths.</param>
/// <param name="Staff">Staff person JSON paths.</param>
public sealed record ResourceSecurableElements(
    IReadOnlyList<EdOrgSecurableElement> EducationOrganization,
    IReadOnlyList<string> Namespace,
    IReadOnlyList<string> Student,
    IReadOnlyList<string> Contact,
    IReadOnlyList<string> Staff
)
{
    /// <summary>
    /// An empty instance for resources with no securable elements.
    /// </summary>
    public static readonly ResourceSecurableElements Empty = new([], [], [], [], []);

    /// <summary>
    /// Whether this resource has any securable elements at all.
    /// </summary>
    public bool HasAny =>
        EducationOrganization.Count > 0
        || Namespace.Count > 0
        || Student.Count > 0
        || Contact.Count > 0
        || Staff.Count > 0;
}
