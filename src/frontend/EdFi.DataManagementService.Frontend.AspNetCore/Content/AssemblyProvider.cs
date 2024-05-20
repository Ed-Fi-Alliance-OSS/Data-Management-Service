// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

public interface IAssemblyProvider
{
    /// <summary>
    /// Gets assembly by type
    /// </summary>
    /// <param name="assemblyType"></param>
    /// <returns></returns>
    Assembly GetAssemblyByType(Type assemblyType);
}

public class AssemblyProvider : IAssemblyProvider
{
    private readonly ILogger<AssemblyProvider> _logger;

    public AssemblyProvider(ILogger<AssemblyProvider> logger)
    {
        _logger = logger;
    }

    public Assembly GetAssemblyByType(Type assemblyType)
    {
        var assembly = Assembly.GetAssembly(assemblyType);
        if (assembly == null)
        {
            var error = $"Could not load {nameof(assemblyType)} assembly";
            _logger.LogCritical(error);
            throw new InvalidOperationException(error);
        }
        return assembly;
    }
}
