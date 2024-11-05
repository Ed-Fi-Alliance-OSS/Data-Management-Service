// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record KeycloakError(string FailureMessage)
{
    /// <summary>
    /// Keycloak is unreachable
    /// </summary>
    public record Unreachable(string FailureMessage) : KeycloakError(FailureMessage);

    /// <summary>
    /// Received a 404 when calling Keycloak, possibly due to realm being invalid
    /// </summary>
    public record NotFound(string FailureMessage) : KeycloakError(FailureMessage);

    /// <summary>
    /// Bad credentials
    /// </summary>
    public record Unauthorized(string FailureMessage) : KeycloakError(FailureMessage);

    /// <summary>
    /// Insufficient permission to perform the request
    /// </summary>
    public record Forbidden(string FailureMessage) : KeycloakError(FailureMessage);
}
