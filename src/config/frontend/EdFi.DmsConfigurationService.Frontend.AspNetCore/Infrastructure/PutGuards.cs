// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class PutGuards
{
    public static void GuardRouteIdMatchesBodyId(int routeId, int bodyId) =>
        GuardRouteIdMatchesBodyId(routeId, bodyId, "Id");

    public static void GuardRouteIdMatchesBodyId(int routeId, int bodyId, string propertyName)
    {
        if (bodyId != routeId)
        {
            string messagePropertyName = propertyName == "Id" ? "id" : propertyName;

            throw new ValidationException([
                new ValidationFailure(
                    propertyName,
                    $"Request body {messagePropertyName} must match the id in the url."
                ),
            ]);
        }
    }
}
