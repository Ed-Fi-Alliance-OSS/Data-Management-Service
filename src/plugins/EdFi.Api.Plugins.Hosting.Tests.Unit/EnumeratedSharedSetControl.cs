// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// The rejected alternative to host-first, built so the decision can be measured rather than restated.
/// </summary>
/// <remarks>
/// <para>
/// It unifies a named list - the Ed-Fi contract plus the two abstractions that appear in the hook
/// signature - and serves everything else from the plugin's own directory. That is the design any
/// reasonable implementer reaches for first, which is why refuting it needs a working copy of it.
/// </para>
/// <para>
/// It lives here and only here. Nothing in the loader can be configured into this behaviour, so there
/// is no production switch, no strategy parameter, and no way for a host to select it by accident.
/// </para>
/// </remarks>
internal sealed class EnumeratedSharedSetContext : AssemblyLoadContext
{
    /// <summary>
    /// The assemblies this alternative shares: the contract, and the two the hook signature names.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.Primitives is deliberately not here, and that is the whole point rather
    /// than an oversight. It holds <c>IChangeToken</c>, which is the return type of
    /// <c>IConfiguration.GetReloadToken()</c>, so it is reachable through a shared interface without
    /// appearing in any hook signature. Any list written by inspecting the signatures omits it.
    /// </remarks>
    private static readonly string[] SharedNames =
    [
        "EdFi.Api.Plugins",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Configuration.Abstractions",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    private EnumeratedSharedSetContext(string pluginName, string entryAssemblyPath)
        : base($"{pluginName}.enumerated-shared-set", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } simpleName)
        {
            return null;
        }

        if (SharedNames.Contains(simpleName, StringComparer.Ordinal))
        {
            return Default.LoadFromAssemblyName(new AssemblyName(simpleName));
        }

        // Everything else comes from the plugin, which is what host-first refuses to do and what
        // splits an identity the host reaches through one of the shared interfaces.
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// Loads a staged plugin under the enumerated set, invokes its hook, and reports what happened.
    /// </summary>
    /// <remarks>
    /// The whole point is that the plugin loads cleanly under either strategy and the difference shows
    /// up when the hook runs, so this deliberately separates the two and returns the hook's failure
    /// rather than asserting on it. A load failure would be a different finding and is thrown.
    /// </remarks>
    internal static Exception? InvokeContributeServices(
        string pluginDirectory,
        string pluginName,
        IConfiguration configuration
    )
    {
        string entryPath = Path.Combine(pluginDirectory, $"{pluginName}.dll");
        EnumeratedSharedSetContext context = new(pluginName, entryPath);

        Assembly entryAssembly = context.LoadFromAssemblyPath(entryPath);

        Type pluginType = entryAssembly
            .GetExportedTypes()
            .Single(candidate =>
                typeof(EdFiApiPlugin).IsAssignableFrom(candidate)
                && candidate is { IsAbstract: false, IsInterface: false }
            );

        EdFiApiPlugin instance = (EdFiApiPlugin)Activator.CreateInstance(pluginType)!;

        try
        {
            instance.ContributeServices(new ServiceCollection(), configuration);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
