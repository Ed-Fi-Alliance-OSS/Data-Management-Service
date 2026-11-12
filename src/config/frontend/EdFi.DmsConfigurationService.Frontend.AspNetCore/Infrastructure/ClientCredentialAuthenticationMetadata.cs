// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking a route that authenticates its caller with OAuth client credentials
/// (HTTP Basic or form fields) inside its handler, rather than through an authorization policy.
/// <see cref="ConfigurationServiceOpenApiTransformers"/> describes such an operation with the HTTP Basic
/// client-credentials scheme instead of the empty requirement anonymous operations get. It carries no
/// behavior of its own: the handler enforces the authentication.
/// </summary>
internal sealed class ClientCredentialAuthenticationMetadata
{
    public static readonly ClientCredentialAuthenticationMetadata Instance = new();

    private ClientCredentialAuthenticationMetadata() { }
}
