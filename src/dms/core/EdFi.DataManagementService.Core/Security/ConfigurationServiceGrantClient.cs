// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// The private transport for the DMS client-credentials grant to the Configuration Service. It is
/// registered with its own primary handler that never follows a redirect, so a 3xx surfaces as a
/// failed grant and the credential form is never resent to another location. It carries no
/// response handler: <see cref="ConfigurationServiceTokenHandler" /> owns the grant's status and
/// diagnostics.
/// </summary>
public sealed class ConfigurationServiceGrantClient(HttpClient httpClient)
{
    /// <summary>
    /// Sends the grant and returns as soon as the response headers arrive, so the caller observes
    /// the status before buffering or parsing any body.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) => httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
}
