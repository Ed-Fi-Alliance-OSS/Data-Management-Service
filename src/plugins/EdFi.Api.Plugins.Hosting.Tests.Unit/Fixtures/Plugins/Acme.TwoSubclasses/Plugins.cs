// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;

namespace Acme.TwoSubclasses;

public sealed class FirstPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.TwoSubclasses";
}

public sealed class SecondPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.TwoSubclasses";
}
