// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The named <see cref="HttpClient"/> for every DMS call of the projection reader: Discovery, token and page
/// requests (DMS-1440 spec §5.1).
/// </summary>
public static class DmsEducationOrganizationProjectionHttpClient
{
    public const string Name = "DmsEducationOrganizationProjection";

    /// <summary>
    /// The primary handler. A redirect is classified rather than followed, so a bearer token or Basic credential is
    /// never replayed to another location; no cookie is stored or sent.
    /// </summary>
    internal static HttpMessageHandler CreatePrimaryHandler() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>
    /// No client-wide timeout: the reader bounds each request by its per-stage timeout linked to the total-read
    /// deadline and the caller's token, which lets it tell a timeout from caller cancellation.
    /// </summary>
    internal static void Configure(HttpClient client) => client.Timeout = Timeout.InfiniteTimeSpan;
}
