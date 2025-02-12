// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// An immutable list of the EducationOrganizations and NamespacePrefixes
/// this client is authorized for
/// </summary>
public record ClientAuthorizations(
    /// <summary>
    /// Education organization ids this client is authorized for
    /// </summary>
    IReadOnlyList<EducationOrganizationId> EducationOrganizationIds,
    /// <summary>
    /// Namespace prefixes this client is authorized for
    /// </summary>
    IReadOnlyList<NamespacePrefix> NamespacePrefixes


);
