// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>Endpoint metadata marking a route that <c>AppSettings:EnableDataStoreManagement</c> gates.</summary>
public sealed class ManagedDataStoreRouteMetadata
{
    public static ManagedDataStoreRouteMetadata Instance { get; } = new();

    private ManagedDataStoreRouteMetadata() { }
}
