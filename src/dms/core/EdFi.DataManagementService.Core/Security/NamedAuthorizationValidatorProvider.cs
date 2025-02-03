// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Defines method for getting authorization validator by strategy name
/// </summary>
public interface IAuthorizationValidatorProvider
{
    IAuthorizationValidator? GetByName(string authorizationStrategyName);
}

public class NamedAuthorizationValidatorProvider : IAuthorizationValidatorProvider
{
    private readonly Dictionary<string, Type> _authorizationValidatorTypes = [];
    private readonly IServiceProvider _serviceProvider;

    public NamedAuthorizationValidatorProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        RegisterAuthorizationValidators();
    }

    private void RegisterAuthorizationValidators()
    {
        var serviceType = typeof(IAuthorizationValidator);
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

    public IAuthorizationValidator? GetByName(string authorizationStrategyName)
    {
        if (!_authorizationValidatorTypes.TryGetValue(authorizationStrategyName, out var type))
        {
            return null;
        }
        return (IAuthorizationValidator)_serviceProvider.GetRequiredService(type);
    }
}
