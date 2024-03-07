// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Infrastructure.Extensions;

public static class HttpRequestExtensions
{
    public static string RootUrl(this HttpRequest httpRequest)
    {
        return $"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.PathBase}";
    }

    public static string UrlWithPathSegment(this HttpRequest httpRequest)
    {
        var rootUrl = $"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.PathBase}";
        return $"{rootUrl}{httpRequest.Path}";
    }
}
