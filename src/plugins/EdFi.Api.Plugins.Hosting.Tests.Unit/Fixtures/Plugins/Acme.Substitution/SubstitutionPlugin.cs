// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;

namespace Acme.Substitution;

public sealed class SubstitutionPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Substitution";

    /// <summary>
    /// Resolves Acme.HostShared for the first time. Until this is called nothing has asked for it,
    /// and the substitution record is empty because no substitution has happened.
    /// </summary>
    public static string DescribeHostShared() => HostSharedMarker.Describe();
}
