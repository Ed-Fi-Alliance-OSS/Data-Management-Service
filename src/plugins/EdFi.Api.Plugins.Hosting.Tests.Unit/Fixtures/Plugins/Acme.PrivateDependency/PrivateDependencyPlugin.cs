// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.Private;
using EdFi.Api.Plugins;

namespace Acme.PrivateDependency;

public sealed class PrivateDependencyPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.PrivateDependency";

    /// <summary>Resolves the private copy, which is the assembly the plugin shipped.</summary>
    public static string DescribePrivateDependency() => PrivateMarker.Describe();
}
