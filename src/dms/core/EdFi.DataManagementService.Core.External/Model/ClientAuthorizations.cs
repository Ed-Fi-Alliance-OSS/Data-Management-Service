// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

public record ClientAuthorizations(
    /// <summary>
    /// Unique token identifier
    /// </summary>
    string TokenId,
    /// <summary>
    /// Client identifier from the JWT token
    /// </summary>
    string ClientId,
    /// <summary>
    /// Claim set name associated with vendor application key and secret
    /// </summary>
    string ClaimSetName,
    /// <summary>
    /// Education organization id list associated with vendor application key and secret
    /// </summary>
    List<EducationOrganizationId> EducationOrganizationIds,
    /// <summary>
    /// Namespace prefixes associated with vendor application key and secret
    /// </summary>
    List<NamespacePrefix> NamespacePrefixes
);
