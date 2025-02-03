// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Security.AuthorizationStrategies;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Defines method for getting authorization strategy handler by name
/// </summary>
public interface IAuthorizationStrategyHandlerProvider
{
    IAuthorizationStrategyHandler? GetByName(string authorizationStrategyName);
}

public class NamedAuthorizationStrategyHandlerProvider : IAuthorizationStrategyHandlerProvider
{
    private readonly Dictionary<string, Type> _authStrategyHandlerTypes = [];
    private readonly IServiceProvider _serviceProvider;

    public NamedAuthorizationStrategyHandlerProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        RegisterAuthorizationStrategyHandlers();
    }

    private void RegisterAuthorizationStrategyHandlers()
    {
        var serviceType = typeof(IAuthorizationStrategyHandler);
        var types = Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(t => serviceType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<AuthorizationStrategyNameAttribute>();
            if (attribute != null)
            {
                _authStrategyHandlerTypes[attribute.Name] = type;
            }
        }
    }

    public IAuthorizationStrategyHandler? GetByName(string authorizationStrategyName)
    {
        if (!_authStrategyHandlerTypes.TryGetValue(authorizationStrategyName, out var type))
        {
            return null;
        }
        return (IAuthorizationStrategyHandler)_serviceProvider.GetRequiredService(type);
    }
}
