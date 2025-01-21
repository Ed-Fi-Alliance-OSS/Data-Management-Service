// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

public record ApiClientDetails(
    /// <summary>
    /// Unique token identifier
    /// </summary>
    string TokenId,
    /// <summary>
    /// Claim set name associated with vendor application key and secret
    /// </summary>
    string ClaimSetName,
    /// <summary>
    /// Education organization id list associated with vendor application key and secret
    /// </summary>
    IList<long> educationOrganizationIds,
    /// <summary>
    /// Namespace prefixes associated with vendor application key and secret
    /// </summary>
    IList<string> namespacePrefixes
);
