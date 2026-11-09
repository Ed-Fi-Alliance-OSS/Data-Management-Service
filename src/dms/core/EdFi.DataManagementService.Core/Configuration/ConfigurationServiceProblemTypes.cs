// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>Known CMS token-limit types shared by the private grant and public OAuth readers.</summary>
/// <remarks>
/// CMS has used both prefixes for problem-details token-limit rejections. Keep exact matches for
/// either version. Its newer OAuth error envelope is handled separately by the public proxy.
/// </remarks>
internal static class ConfigurationServiceProblemTypes
{
    internal const string LegacyTooManyTokens = "urn:ed-fi:api:security:authentication:too-many-tokens";

    internal const string ManagementApiTooManyTokens =
        "urn:ed-fi:management-api:security:authentication:too-many-tokens";
}
