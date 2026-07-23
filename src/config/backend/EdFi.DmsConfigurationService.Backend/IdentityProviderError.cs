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
    public record InvalidClient(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// Bad credentials
    /// </summary>
    public record Unauthorized(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// Insufficient permission to perform the request
    /// </summary>
    public record Forbidden(string FailureMessage) : IdentityProviderError(FailureMessage);

    /// <summary>
    /// The identity provider rejected the request with an OAuth 2.0 client error (HTTP 400), such as
    /// invalid_scope or invalid_grant. This is a client error to be corrected, not a retryable server
    /// outage. <see cref="Error"/> is the OAuth error code parsed from the provider response.
    /// </summary>
    public record BadRequest(string Error, string FailureMessage) : IdentityProviderError(FailureMessage);
}
