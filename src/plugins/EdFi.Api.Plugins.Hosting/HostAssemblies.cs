// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Asks the host what it carries, by simple name.
/// </summary>
/// <remarks>
/// The simple name is the whole point. <see cref="AssemblyLoadContext.LoadFromAssemblyName"/> throws
/// <see cref="FileNotFoundException"/> both when the host has nothing by that name and when the host
/// has an older version than the request declares, so a request carrying a version cannot tell the two
/// apart and a fall-through on that exception would hand a version-skewed plugin its private copy
/// silently. Asking without a version carries no constraint the binder can fail on, which leaves the
/// comparison to the loader, where it can be reported.
/// </remarks>
internal static class HostAssemblies
{
    /// <summary>
    /// Loads the host's copy of <paramref name="simpleName"/>, or returns <see langword="false"/> when
    /// the host carries nothing by that name.
    /// </summary>
    internal static bool TryLoad(string simpleName, out Assembly assembly)
    {
        try
        {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(simpleName));
            return true;
        }
        catch (FileNotFoundException)
        {
            assembly = null!;
            return false;
        }
    }

    /// <summary>
    /// Reads the <c>AssemblyVersion</c> the host carries for <paramref name="simpleName"/>, when it
    /// carries one at all and that copy declares a version.
    /// </summary>
    internal static bool TryGetVersion(string simpleName, out Version version)
    {
        if (TryLoad(simpleName, out Assembly assembly) && assembly.GetName().Version is { } hostVersion)
        {
            version = hostVersion;
            return true;
        }

        version = null!;
        return false;
    }
}
