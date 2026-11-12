// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking the DMS endpoints that serve education data to an authenticated
/// client - the resource routes, their deletes and keyChanges routes, and Change Queries
/// availableChangeVersions - so middleware can recognize them without matching on the path.
/// </summary>
internal sealed class DataEndpointMetadata
{
    public static readonly DataEndpointMetadata Instance = new();

    private DataEndpointMetadata() { }
}
