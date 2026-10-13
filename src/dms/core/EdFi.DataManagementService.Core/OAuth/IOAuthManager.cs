// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.OAuth;

public interface IOAuthManager
{
    /// <param name="httpClient">Sends the request to the upstream identity service.</param>
    /// <param name="grantType">The <c>grant_type</c> the caller supplied.</param>
    /// <param name="authHeaderString">
    /// The caller's <c>Authorization</c> header, or <c>null</c> when the request carried none. An
    /// empty string means the header was present with no value, which is not the same request.
    /// </param>
    /// <param name="bodyCredentials">
    /// The <c>client_id</c> and <c>client_secret</c> the caller supplied in the request body, or
    /// <see cref="TokenRequestCredentials.None"/>.
    /// </param>
    /// <param name="upstreamUri">The upstream identity service's token endpoint.</param>
    /// <param name="traceId">The request's correlation ID.</param>
    public Task<HttpResponseMessage> GetAccessTokenAsync(
        IHttpClientWrapper httpClient,
        string grantType,
        string? authHeaderString,
        TokenRequestCredentials bodyCredentials,
        string upstreamUri,
        TraceId traceId
    );
}
