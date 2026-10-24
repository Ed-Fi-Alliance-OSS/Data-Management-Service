// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;

/// <summary>
/// Optional capability of a token or data repository: read only what client authentication needs,
/// before the presented secret is verified.
/// <para>
/// <see cref="IOpenIddictTokenRepository.GetApplicationByClientIdAsync"/> loads an application's
/// scopes, permissions, data stores and protocol mappers, so it does more work when the client
/// exists than when it does not. A repository that offers this capability lets the token manager
/// authenticate from the narrow <see cref="ClientCredential"/> and load the full application only
/// once the secret has been verified and the client is approved. That narrows the difference in
/// work between an existing and an unknown client id; it does not remove it.
/// </para>
/// <para>
/// Deliberately optional and not part of the plugin replace contract: the token manager answers
/// through <see cref="IOpenIddictTokenRepository.GetApplicationByClientIdAsync"/> alone, exactly
/// as it does without this capability, when its repository does not implement this interface or
/// reports <see cref="CanReadClientCredentials"/> as <c>false</c>. Such a repository keeps that
/// difference in work.
/// </para>
/// </summary>
public interface IClientCredentialReader
{
    /// <summary>
    /// Whether this instance can answer <see cref="GetClientCredentialAsync"/>. A property of the
    /// instance, fixed for its lifetime: a repository that wraps another reports what the wrapped
    /// one offers. It is not a health or configuration switch, and reading it does no I/O.
    /// </summary>
    bool CanReadClientCredentials { get; }

    /// <summary>
    /// Reads the authentication fields of the application registered under
    /// <paramref name="clientId"/>.
    /// </summary>
    /// <returns>
    /// The credential, or <c>null</c> when no application is registered under the client id.
    /// <c>null</c> never stands for an unsupported read.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// <see cref="CanReadClientCredentials"/> is <c>false</c>. Calling this then is a programming
    /// error, never an authentication outcome.
    /// </exception>
    Task<ClientCredential?> GetClientCredentialAsync(string clientId);
}
