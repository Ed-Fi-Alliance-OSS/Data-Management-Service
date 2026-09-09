// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.Private;
using EdFi.Api.Plugins;

namespace Acme.CtorSignatureMissing;

public sealed class CtorSignatureMissingPlugin : EdFiApiPlugin
{
    public CtorSignatureMissingPlugin() { }

    /// <summary>Never called, and its parameter type is nowhere the runtime can find it.</summary>
    public CtorSignatureMissingPlugin(PrivateHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
    }

    public override string Name => "Acme.CtorSignatureMissing";
}
