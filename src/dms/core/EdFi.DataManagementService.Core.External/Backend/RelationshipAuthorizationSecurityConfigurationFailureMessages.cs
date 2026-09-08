// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Wire-stable security-configuration diagnostics for relationship authorization failures.
/// </summary>
public static class RelationshipAuthorizationSecurityConfigurationFailureMessages
{
    public const string InvalidFailurePayloadSecurityConfigurationError =
        "The relationship authorization failure payload returned by the authorization provider is invalid and cannot be mapped to the configured relationship authorization plan.";
}
