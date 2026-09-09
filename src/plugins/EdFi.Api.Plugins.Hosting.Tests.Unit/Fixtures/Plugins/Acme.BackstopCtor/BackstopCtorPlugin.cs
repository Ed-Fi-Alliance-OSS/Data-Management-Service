// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;

namespace Acme.BackstopCtor;

public sealed class BackstopCtorPlugin : EdFiApiPlugin
{
    private readonly string _described;

    public BackstopCtorPlugin()
    {
        // Resolves Acme.HostShared while the loader is constructing this type, so the refusal has a
        // loader frame above it.
        _described = HostSharedMarker.Describe();
    }

    public override string Name => _described;
}
