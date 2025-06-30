// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Defines method for getting authorization validation/ filters handler by strategy name
/// </summary>
public interface IAuthorizationServiceFactory
{
    T? GetByName<T>(string authorizationStrategyName)
        where T : class;
}

public class NamedAuthorizationServiceFactory(IServiceProvider serviceProvider) : IAuthorizationServiceFactory
{
    private readonly Dictionary<(Type, string), Type> _authorizationValidatorTypes =
        RegisterAuthorizationValidators();

    private static Dictionary<(Type, string), Type> RegisterAuthorizationValidators()
    {
        var authorizationValidatorTypes = new Dictionary<(Type, string), Type>();
        var types = Assembly.GetExecutingAssembly().GetTypes();

        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<AuthorizationStrategyNameAttribute>();
            if (attribute != null)
            {
                var interfaces = type.GetInterfaces();
                foreach (var typeInterface in interfaces)
                {
                    authorizationValidatorTypes[(typeInterface, attribute.Name)] = type;
                }
            }
        }

        return authorizationValidatorTypes;
    }

    public T? GetByName<T>(string authorizationStrategyName)
        where T : class
    {
        var key = (typeof(T), authorizationStrategyName);
        if (!_authorizationValidatorTypes.TryGetValue(key, out var type))
        {
            return null;
        }
        return serviceProvider.GetRequiredService(type) as T;
    }
}
