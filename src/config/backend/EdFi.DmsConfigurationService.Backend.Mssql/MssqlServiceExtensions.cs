// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// The Backend SQL Server service extensions to be registered to a Frontend DI container.
/// Connections are created per-operation from DatabaseOptions, so no shared data source
/// is registered here.
/// </summary>
public static class MssqlServiceExtensions
{
    public static IServiceCollection AddMssqlDatastore(this IServiceCollection services)
    {
        return services;
    }
}
