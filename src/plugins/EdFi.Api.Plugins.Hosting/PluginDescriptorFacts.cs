// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The facts a descriptor carries that a diagnostic may read.
/// </summary>
/// <remarks>
/// One place rather than a copy per reader, because the keyed and unkeyed accessors answer null for
/// each other's properties and a reader that consults only one silently reports nothing for half the
/// registrations it was given.
/// </remarks>
public static class PluginDescriptorFacts
{
    /// <summary>
    /// The implementation a descriptor names, or <see langword="null"/> when it names none, which is
    /// what a factory registration looks like.
    /// </summary>
    /// <remarks>
    /// Nothing is activated. An instance registration is asked for its runtime type and never for its
    /// value, so a diagnostic built from this cannot render implementer state.
    /// </remarks>
    public static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
    }
}
