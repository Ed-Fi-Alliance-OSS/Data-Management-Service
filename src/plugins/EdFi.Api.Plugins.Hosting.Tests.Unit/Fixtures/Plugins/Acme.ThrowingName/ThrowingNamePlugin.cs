// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;

namespace Acme.ThrowingName;

public sealed class ThrowingNamePlugin : EdFiApiPlugin
{
    public override string Name =>
        throw new InvalidOperationException("Acme.ThrowingName refuses to say its name.");
}
