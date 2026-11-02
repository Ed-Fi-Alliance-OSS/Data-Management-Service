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
    IReadOnlyList<EducationOrganizationId> EducationOrganizationIds,
    /// <summary>
    /// Namespace prefixes associated with vendor application key and secret
    /// </summary>
    IReadOnlyList<NamespacePrefix> NamespacePrefixes,
    /// <summary>
    /// data store identifiers associated with vendor application key and secret
    /// </summary>
    IReadOnlyList<DataStoreId> DataStoreIds
)
{
    /// <summary>
    /// Every distinct scope value from the validated token, in first-observed order. This is an
    /// unresolved fact: the order never selects permissions, and the claim set that applies is
    /// resolved per request against the tenant's claim-set metadata. Empty when the token had no
    /// scope values or the caller supplied only the legacy <see cref="ClaimSetName"/>. When empty,
    /// resolution uses that legacy name as one literal candidate to preserve compatibility with
    /// callers of the positional constructor. Nonempty scope values always take precedence.
    /// </summary>
    public IReadOnlyList<string> ScopeValues { get; init; } = [];
}
