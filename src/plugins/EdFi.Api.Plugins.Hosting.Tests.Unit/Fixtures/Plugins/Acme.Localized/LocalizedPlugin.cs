// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Resources;
using EdFi.Api.Plugins;

namespace Acme.Localized;

public sealed class LocalizedPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Localized";

    /// <summary>Reads the French resource, which lives in the satellite assembly.</summary>
    public static string FrenchGreeting() =>
        new ResourceManager("Acme.Localized.Strings", typeof(LocalizedPlugin).Assembly).GetString(
            "Greeting",
            new CultureInfo("fr")
        )!;
}
