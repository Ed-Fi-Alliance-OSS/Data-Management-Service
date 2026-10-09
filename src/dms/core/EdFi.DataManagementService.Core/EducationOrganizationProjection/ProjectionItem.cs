// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// The four core education organization types the projection serves.
/// </summary>
internal enum ProjectionItemKind
{
    StateEducationAgency,
    EducationServiceCenter,
    LocalEducationAgency,
    School,
}

/// <summary>
/// One projected education organization after parent selection: exactly the five members a
/// response item carries, and the only input to <see cref="ProjectionDigest"/>.
/// </summary>
internal sealed record ProjectionItem(
    long EducationOrganizationId,
    ProjectionItemKind Kind,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    long? ParentId
);
