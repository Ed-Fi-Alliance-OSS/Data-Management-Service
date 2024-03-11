// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DataManagementService.Api.Content
{
    public interface IAssemblyProvider
    {
        /// <summary>
        /// Gets assembly by type
        /// </summary>
        /// <param name="assemblyType"></param>
        /// <returns></returns>
        Assembly GetAssemby(Type assemblyType);
    }

    public class AssemblyProvider : IAssemblyProvider
    {
        public Assembly GetAssemby(Type assemblyType)
        {
            var assembly =
                Assembly.GetAssembly(assemblyType)
                ?? throw new InvalidOperationException($"Could not load {nameof(assemblyType)} assembly");
            return assembly;
        }
    }
}
