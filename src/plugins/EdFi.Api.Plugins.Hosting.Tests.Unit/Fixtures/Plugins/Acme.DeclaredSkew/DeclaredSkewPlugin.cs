// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;

namespace Acme.DeclaredSkew;

public sealed class DeclaredSkewPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.DeclaredSkew";

    /// <summary>
    /// Never called. It exists so the compiler records the reference the manifest then declares,
    /// while no executed code path ever resolves the assembly.
    /// </summary>
    public static string NeverCalled() => HostSharedMarker.Describe();
}
