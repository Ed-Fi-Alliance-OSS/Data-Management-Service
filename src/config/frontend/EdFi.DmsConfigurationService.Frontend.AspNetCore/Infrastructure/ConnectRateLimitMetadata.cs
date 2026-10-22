// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking the four <c>/connect/*</c> routes that
/// <see cref="Middleware.ConnectSourceRateLimitMiddleware"/> limits per source address (DMS-1482). Routing,
/// not string matching, decides which requests carry it, so path base, route-context suffixes and path
/// case need no handling here.
/// </summary>
internal sealed class ConnectRateLimitMetadata
{
    public static readonly ConnectRateLimitMetadata Instance = new();

    private ConnectRateLimitMetadata() { }
}
