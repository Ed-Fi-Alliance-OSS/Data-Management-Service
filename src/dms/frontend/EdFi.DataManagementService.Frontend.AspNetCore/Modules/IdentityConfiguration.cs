// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public static class IdentityConfiguration
{
    public static bool EnforceAuthorization(IEndpointRouteBuilder routeBuilder)
    {
        return routeBuilder.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("IdentitySettings:EnforceAuthorization");
    }
}
