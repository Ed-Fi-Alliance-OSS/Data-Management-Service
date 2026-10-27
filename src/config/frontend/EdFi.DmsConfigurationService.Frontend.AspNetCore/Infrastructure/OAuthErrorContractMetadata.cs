// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking a route whose exception responses follow the RFC 6749 §5.2 error format
/// instead of the Ed-Fi problem-details contract. <see cref="GlobalExceptionHandler"/> reads it from the
/// original endpoint and writes the response with <see cref="OAuthErrorResponseWriter"/>. Only
/// <c>/connect/token</c>, <c>/connect/revoke</c> and <c>/connect/introspect</c> carry it (DMS-1327 D-17); every other route keeps
/// the Ed-Fi contract. Each route has its own instance, which names the operation in the fixed
/// <c>server_error</c> description an unexpected fault is answered with.
/// </summary>
internal sealed class OAuthErrorContractMetadata
{
    public static readonly OAuthErrorContractMetadata Revocation = new(
        "The revocation request could not be processed."
    );

    public static readonly OAuthErrorContractMetadata Introspection = new(
        "The introspection request could not be processed."
    );

    public static readonly OAuthErrorContractMetadata Token = new(
        "The token request could not be processed."
    );

    private OAuthErrorContractMetadata(string serverErrorDescription) =>
        ServerErrorDescription = serverErrorDescription;

    /// <summary>The fixed <c>error_description</c> of a 500 <c>server_error</c> on this route.</summary>
    public string ServerErrorDescription { get; }
}
