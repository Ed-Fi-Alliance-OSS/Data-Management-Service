// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Extraction;

internal static class IdentityValueCanonicalizer
{
    public static DocumentIdentityElement CreateDocumentIdentityElement(
        JsonPath identityJsonPath,
        string identityValue
    )
    {
        return new(identityJsonPath, Canonicalize(identityJsonPath, identityValue));
    }

    private static string Canonicalize(JsonPath identityJsonPath, string identityValue)
    {
        return IsDescriptorValuedIdentityMember(identityJsonPath)
            ? identityValue.ToLowerInvariant()
            : identityValue;
    }

    private static bool IsDescriptorValuedIdentityMember(JsonPath identityJsonPath)
    {
        return identityJsonPath.Value.EndsWith("Descriptor", StringComparison.Ordinal);
    }
}
