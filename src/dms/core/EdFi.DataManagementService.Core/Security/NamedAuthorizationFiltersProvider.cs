// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Defines method for getting authorization filters by strategy name
/// </summary>
public interface IAuthorizationFiltersProvider
{
    IAuthorizationFilters? GetByName(string authorizationStrategyName);
}

public class NamedAuthorizationFiltersProvider : IAuthorizationFiltersProvider
{
    private readonly Dictionary<string, Type> _authorizationValidatorTypes = [];
    private readonly IServiceProvider _serviceProvider;

    public NamedAuthorizationFiltersProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        RegisterAuthorizationValidators();
    }

    private void RegisterAuthorizationValidators()
    {
        var serviceType = typeof(IAuthorizationFilters);
        var types = Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(t => serviceType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<AuthorizationStrategyNameAttribute>();
            if (attribute != null)
            {
                _authorizationValidatorTypes[attribute.Name] = type;
            }
        }
    }

    public IAuthorizationFilters? GetByName(string authorizationStrategyName)
    {
        if (!_authorizationValidatorTypes.TryGetValue(authorizationStrategyName, out var type))
        {
            return null;
        }
        return (IAuthorizationFilters)_serviceProvider.GetRequiredService(type);
    }
}
