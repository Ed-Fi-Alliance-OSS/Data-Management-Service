// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record IdentityProviderError(string FailureMessage)
{
    /// <summary>
    /// Keycloak is unreachable
    /// </summary>
    public record Unreachable(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// Received a 404 when calling the IDP
    /// </summary>
    public record NotFound(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// Bad credentials
    /// </summary>
    public record Unauthorized(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// Insufficient permission to perform the request
    /// </summary>
    public record Forbidden(string FailureMessage) : IdentityProviderError(FailureMessage);
}
