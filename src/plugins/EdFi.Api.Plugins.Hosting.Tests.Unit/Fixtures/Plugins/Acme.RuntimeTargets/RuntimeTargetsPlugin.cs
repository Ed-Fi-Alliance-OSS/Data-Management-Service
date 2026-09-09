// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;
using Microsoft.Data.SqlClient;

namespace Acme.RuntimeTargets;

/// <summary>
/// A plugin whose only distinguishing feature is the publish shape of its dependency.
/// </summary>
public sealed class RuntimeTargetsPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.RuntimeTargets";

    /// <summary>
    /// Names a type from the package, so the reference in this assembly's metadata is real rather
    /// than a package reference the compiler dropped for want of a use.
    /// </summary>
    public static string DescribeConnectionStringBuilder() =>
        new SqlConnectionStringBuilder { DataSource = "nowhere" }.DataSource;
}
