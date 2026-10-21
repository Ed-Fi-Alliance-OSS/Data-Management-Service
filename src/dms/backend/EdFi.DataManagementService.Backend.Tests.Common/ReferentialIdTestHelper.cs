// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Test helper for computing the UUIDv5 referential ids that the database triggers write to
/// dms.ReferentialIdentity. Core no longer computes them; tests that seed or assert
/// dms.ReferentialIdentity rows use this helper until that table is removed.
/// </summary>
public static class ReferentialIdTestHelper
{
    /// <summary>
    /// The UUID namespace the referential identity triggers hash under.
    /// </summary>
    public static readonly Guid EdFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    /// <summary>
    /// Returns the UUIDv5 referential id for a resource and document identity, hashing
    /// "{ProjectName}{ResourceName}" followed by the "#"-joined "{jsonPath}={value}" identity elements.
    /// </summary>
    public static Guid ReferentialIdFrom(BaseResourceInfo resourceInfo, DocumentIdentity documentIdentity)
    {
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentIdentity);

        string identityString = string.Join(
            "#",
            documentIdentity.DocumentIdentityElements.Select(element =>
                $"{element.IdentityJsonPath.Value}={element.IdentityValue}"
            )
        );

        return Deterministic.Create(
            EdFiUuidv5Namespace,
            $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}{identityString}"
        );
    }

    /// <summary>
    /// Convenience wrapper around <see cref="ReferentialIdFrom"/> that accepts raw strings for use in
    /// test assertions.
    /// </summary>
    public static Guid ComputeReferentialId(
        string projectName,
        string resourceName,
        params (string jsonPath, string value)[] identityElements
    )
    {
        var resourceInfo = new BaseResourceInfo(
            new ProjectName(projectName),
            new ResourceName(resourceName),
            IsDescriptor: false
        );

        var documentIdentity = new DocumentIdentity(
            identityElements
                .Select(e => new DocumentIdentityElement(new JsonPath(e.jsonPath), e.value))
                .ToArray()
        );

        return ReferentialIdFrom(resourceInfo, documentIdentity);
    }
}
