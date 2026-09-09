// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
/// <para>
/// Two modes, and the difference between them is the whole point. <c>loader</c> uses the real
/// <see cref="PluginLoader"/>, whose context answers for unmanaged dependencies. <c>plain</c> uses a
/// context that resolves managed assemblies host-first exactly as the real one does and overrides
/// nothing for unmanaged ones, which is what the runtime's own probing is left to handle. Running one
/// plugin through both is what shows whether the unmanaged override is doing anything, and a plugin
/// whose native library sits beside its entry assembly is satisfied by probing either way.
/// </para>
/// <para>
/// Three further modes load no plugin at all. <c>stall</c> never exits, <c>spew</c> fills its error
/// stream before it writes anything to its output stream, and <c>linger</c> exits immediately after
/// starting a process of its own that inherits the streams and outlives it. They exist so that the
/// runner's deadlines and its stream draining are exercised by a child that really behaves that way,
/// rather than asserted by reading the runner.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "usage: PluginNativeProbeHost <plugin-root> <plugin-name> <loader|plain|stall|spew>"
            );
            return 2;
        }

        string root = args[0];
        string pluginName = args[1];
        string mode = args[2];

        // None of these load a plugin. They are here so that the runner's deadlines and its stream
        // draining are exercised by a child that really stalls, really fills a pipe, and really leaves
        // its streams held open by something else.
        if (mode == "stall")
        {
            return Stall();
        }

        if (mode == "spew")
        {
            return Spew();
        }

        if (mode == "linger")
        {
            return Linger();
        }

        if (mode == "linger-child")
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));

            return 0;
        }

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

    /// <summary>Announces itself and then never finishes, so the runner's deadline is what ends it.</summary>
    private static int Stall()
    {
        Console.WriteLine("STALLING");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);

        return 0;
    }

    /// <summary>
    /// Writes far more to the error stream than a pipe buffer holds, and only then writes to the output
    /// stream.
    /// </summary>
    /// <remarks>
    /// A runner that reads one stream to its end before touching the other never gets here: this
    /// process blocks writing its error stream, so its output stream never reaches end of file and both
    /// sides wait forever. The order is the whole point of the case.
    /// </remarks>
    private static int Spew()
    {
        string line = new('e', 1024);

        for (int written = 0; written < 2048; written++)
        {
            Console.Error.WriteLine(line);
        }

        Console.WriteLine("SPEW DONE");

        return 0;
    }

    /// <summary>
    /// Starts a process that inherits this one's streams, then exits at once and leaves it holding
    /// them.
    /// </summary>
    /// <remarks>
    /// The grandchild redirects nothing, so it keeps the write end of the pipes the runner is reading.
    /// Those streams therefore never reach their end, and a runner that waits for them without a bound
    /// waits forever even though the process it started is long gone. The grandchild sleeps for a
    /// bounded time rather than forever, so nothing survives the test that has to be hunted down.
    /// </remarks>
    private static int Linger()
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            UseShellExecute = false,
        };

        if (
            !Path.GetFileNameWithoutExtension(start.FileName)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
        {
            start.FileName = "dotnet";
        }

        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(".");
        start.ArgumentList.Add("none");
        start.ArgumentList.Add("linger-child");

        Process.Start(start);

        Console.WriteLine("LINGER STARTED");

        return 0;
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
