// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;
using EdFi.Api.Plugins;

namespace Acme.NativePortable;

public sealed class NativePortablePlugin : EdFiApiPlugin
{
    public override string Name => "Acme.NativePortable";

    /// <summary>
    /// Calls into the native library the plugin shipped, which sits under runtimes/ rather than
    /// beside the entry assembly, so only the plugin's own context knows where to find it.
    /// </summary>
    public static int NativeLibraryVersion() => NativeMethods.NativeLibraryVersionNumber();
}

internal static class NativeMethods
{
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_libversion_number")]
    internal static extern int NativeLibraryVersionNumber();
}
