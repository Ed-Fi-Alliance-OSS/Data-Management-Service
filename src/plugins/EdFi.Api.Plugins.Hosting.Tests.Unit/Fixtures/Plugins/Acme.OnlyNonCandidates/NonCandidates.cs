// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;

namespace Acme.OnlyNonCandidates;

/// <summary>Abstract, so it cannot be constructed.</summary>
public abstract class AbstractPlugin : EdFiApiPlugin
{
    public override string Name => "abstract";
}

/// <summary>Open generic, so there is no closed type to construct.</summary>
public sealed class GenericPlugin<T> : EdFiApiPlugin
{
    public override string Name => typeof(T).Name;
}

/// <summary>Not exported at all.</summary>
internal sealed class InternalPlugin : EdFiApiPlugin
{
    public override string Name => "internal";
}

/// <summary>Public, but its only constructor is not.</summary>
public sealed class PrivateConstructorPlugin : EdFiApiPlugin
{
    private PrivateConstructorPlugin() { }

    public override string Name => "private-ctor";
}

/// <summary>Public, but the host has nothing to pass it.</summary>
public sealed class ParameterizedPlugin : EdFiApiPlugin
{
    public ParameterizedPlugin(string name)
    {
        Name = name;
    }

    public override string Name { get; }
}
