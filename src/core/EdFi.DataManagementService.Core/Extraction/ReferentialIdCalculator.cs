// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Extraction;

/// <summary>
/// Helper class for calculating ReferentialId from DocumentIdentity
/// </summary>
internal static class ReferentialIdCalculator
{
    /// <summary>
    /// A UUID namespace for generating UUIDv5-compliant deterministic UUIDs per RFC 4122.
    /// </summary>
    public static readonly Guid EdFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    /// <summary>
    /// Returns the string form of a ResourceInfo for identity hashing.
    /// </summary>
    private static string ResourceInfoString(BaseResourceInfo resourceInfo)
    {
        return $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}";
    }

    /// <summary>
    /// Returns the string form of a DocumentIdentity for identity hashing.
    /// </summary>
    private static string DocumentIdentityString(DocumentIdentity documentIdentity)
    {
        return string.Join(
            "#",
            documentIdentity.DocumentIdentityElements.Select(
                (DocumentIdentityElement element) =>
                    $"${element.IdentityJsonPath.Value}=${element.IdentityValue}"
            )
        );
    }

    /// <summary>
    /// Returns a ReferentialId as a UUIDv5-compliant deterministic UUID per RFC 4122.
    /// </summary>
    public static ReferentialId ReferentialIdFrom(
        BaseResourceInfo resourceInfo,
        DocumentIdentity documentIdentity
    )
    {
        return new(
            Deterministic.Create(
                EdFiUuidv5Namespace,
                $"{ResourceInfoString(resourceInfo)}{DocumentIdentityString(documentIdentity)}"
            )
        );
    }
}
