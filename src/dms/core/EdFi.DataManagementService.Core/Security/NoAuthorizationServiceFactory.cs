// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Backend factory class that does not provide any authorization services.
/// </summary>
public class NoAuthorizationServiceFactory : IAuthorizationServiceFactory
{
    public T? GetByName<T>(string authorizationStrategyName)
        where T : class
    {
        throw new NotImplementedException();
    }
}
