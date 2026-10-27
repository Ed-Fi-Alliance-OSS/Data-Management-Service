// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking an OAuth route (<c>/connect/register</c>, <c>/connect/token</c>,
/// <c>/connect/introspect</c>, <c>/connect/revoke</c>) whose responses carry tokens, credentials or other
/// sensitive information. <see cref="Middleware.ResponseCachePolicyMiddleware"/> reads it from the matched
/// endpoint and marks the response <c>Cache-Control: no-store</c> and <c>Pragma: no-cache</c>
/// (RFC 6749 section 5.1).
/// </summary>
internal sealed class OAuthEndpointMetadata
{
    public static readonly OAuthEndpointMetadata Instance = new();

    private OAuthEndpointMetadata() { }
}
