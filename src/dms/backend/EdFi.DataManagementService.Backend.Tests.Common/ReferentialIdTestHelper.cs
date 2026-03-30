// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Test helper for computing expected ReferentialId values.
/// </summary>
public static class ReferentialIdTestHelper
{
    /// <summary>
    /// Convenience wrapper around <see cref="ReferentialIdCalculator.ReferentialIdFrom"/>
    /// that accepts raw strings for use in test assertions.
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

        return ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, documentIdentity).Value;
    }
}
