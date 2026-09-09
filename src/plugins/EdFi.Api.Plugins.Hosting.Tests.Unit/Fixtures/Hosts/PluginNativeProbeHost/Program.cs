// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
using EdFi.Api.Plugins;
using EdFi.Api.Plugins.Hosting;
using Microsoft.Extensions.Configuration;

namespace PluginNativeProbeHost;

/// <summary>
/// Loads one plugin and calls its native entry point, in a process that has loaded no native module
/// before it starts.
/// </summary>
/// <remarks>
/// Two modes, and the difference between them is the whole point. <c>loader</c> uses the real
/// <see cref="PluginLoader"/>, whose context answers for unmanaged dependencies. <c>plain</c> uses a
/// context that resolves managed assemblies host-first exactly as the real one does and overrides
/// nothing for unmanaged ones, which is what the runtime's own probing is left to handle. Running one
/// plugin through both is what shows whether the unmanaged override is doing anything, and a plugin
/// whose native library sits beside its entry assembly is satisfied by probing either way.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "usage: PluginNativeProbeHost <plugin-root> <plugin-name> <loader|plain>"
            );
            return 2;
        }

        string root = args[0];
        string pluginName = args[1];
        string mode = args[2];

        try
        {
            Assembly entryAssembly = mode switch
            {
                "loader" => LoadThroughTheRealLoader(root, pluginName),
                "plain" => LoadWithoutAnUnmanagedOverride(root, pluginName),
                _ => throw new ArgumentException($"unknown mode '{mode}'"),
            };

            Console.WriteLine("LOAD SUCCEEDED");

            MethodInfo nativeCall = entryAssembly
                .GetTypes()
                .Select(type =>
                    type.GetMethod("NativeLibraryVersion", BindingFlags.Public | BindingFlags.Static)
                )
                .First(method => method is not null)!;

            try
            {
                Console.WriteLine($"NATIVE RESULT {nativeCall.Invoke(null, null)}");
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException is { } cause)
            {
                Console.WriteLine($"NATIVE FAILURE {cause.GetType().FullName}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"LOAD FAILURE {exception.GetType().FullName}: {exception.Message}");
            return 1;
        }
    }

    private static Assembly LoadThroughTheRealLoader(string root, string pluginName)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Plugins:Directory"] = root,
                    ["Plugins:Allowed"] = pluginName,
                }
            )
            .Build();

        return PluginLoader.Load(configuration, ["EdFi.Api.Plugins"]).Plugins[0].Instance.GetType().Assembly;
    }

    private static Assembly LoadWithoutAnUnmanagedOverride(string root, string pluginName)
    {
        string entryAssemblyPath = Path.Combine(root, pluginName, $"{pluginName}.dll");

        return new ManagedOnlyContext(pluginName, entryAssemblyPath).LoadFromAssemblyPath(entryAssemblyPath);
    }

    /// <summary>
    /// The control: host-first for managed assemblies, and nothing at all for unmanaged ones.
    /// </summary>
    private sealed class ManagedOnlyContext(string pluginName, string entryAssemblyPath)
        : AssemblyLoadContext(pluginName, isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } simpleName)
            {
                return null;
            }

            try
            {
                return Default.LoadFromAssemblyName(new AssemblyName(simpleName));
            }
            catch (FileNotFoundException)
            {
                string? path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path is null ? null : LoadFromAssemblyPath(path);
            }
        }
    }
}
