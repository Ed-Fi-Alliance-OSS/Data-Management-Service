// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// The store an authentication decision depends on.
/// </summary>
public enum AuthenticationDependencyCategory
{
    /// <summary>The signing keys used to verify token signatures.</summary>
    SigningKeyStore,

    /// <summary>The per-request token-status (revocation) record.</summary>
    TokenStatusStore,
}

/// <summary>
/// Authentication could not reach a decision because a store it depends on is unavailable. This is a dependency
/// failure, not a verdict on the token: the request boundary answers it with 503 and <c>Retry-After</c>, never with
/// 401 and never with success.
/// </summary>
/// <remarks>
/// The message is a fixed description of the category. It never carries key material, connection strings, or
/// client-supplied values, so it can be logged as is; the underlying failure, when there is one, is the inner exception.
/// </remarks>
public class AuthenticationDependencyUnavailableException : Exception
{
    public AuthenticationDependencyUnavailableException(
        AuthenticationDependencyCategory category,
        string message,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        Category = category;
    }

    /// <summary>The store that was unavailable.</summary>
    public AuthenticationDependencyCategory Category { get; }
}
