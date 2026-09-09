// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Thrown from inside a plugin's load context when the host carries an older version of an assembly
/// than the plugin's reference declares.
/// </summary>
/// <remarks>
/// Internal because a caller never sees it. The runtime wraps anything thrown from inside an
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> resolution callback in a
/// <see cref="FileLoadException"/> whose message names only the version the host has, so the loader
/// unwraps that chain and reports this exception's four values as a <see cref="PluginLoadException"/>
/// instead. Falling back to the plugin's private copy rather than throwing is what would split the type
/// identity that host-first resolution exists to keep.
/// </remarks>
#pragma warning disable S3871 // Exception types should be public - deliberate: this one never leaves
// the assembly. The runtime wraps it, PluginLoader unwraps it, and a caller only ever sees the
// PluginLoadException built from its four values. Making it public would publish a type no host can
// observe and invite someone to catch what never escapes.
internal sealed class PluginVersionSkewException(
    string pluginName,
    string assemblyName,
    Version requestedVersion,
    Version hostVersion
)
    : Exception(
        $"plugin '{PluginDiagnosticText.Quote(pluginName)}' requires "
            + $"{PluginDiagnosticText.Quote(assemblyName)} >= {requestedVersion}, "
            + $"host carries {hostVersion}"
    )
{
    internal string PluginName { get; } = pluginName;

    internal string AssemblyName { get; } = assemblyName;

    internal Version RequestedVersion { get; } = requestedVersion;

    internal Version HostVersion { get; } = hostVersion;
}
#pragma warning restore S3871
