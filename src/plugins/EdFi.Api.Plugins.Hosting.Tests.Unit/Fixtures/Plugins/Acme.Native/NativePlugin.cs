// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;
using EdFi.Api.Plugins;

namespace Acme.Native;

public sealed class NativePlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Native";

    /// <summary>
    /// Calls into the native library the plugin shipped. Resolving it goes through the plugin's own
    /// load context, which is the only thing that knows where the plugin put it.
    /// </summary>
    public static int NativeLibraryVersion() => NativeMethods.NativeLibraryVersionNumber();
}

internal static class NativeMethods
{
    // DllImport rather than LibraryImport: the generated marshalling LibraryImport emits needs unsafe
    // code, and a fixture that ships one native call has no reason to turn that on. The entry point
    // name is the library's own and is not this repository's to rename.
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_libversion_number")]
    internal static extern int NativeLibraryVersionNumber();
}
