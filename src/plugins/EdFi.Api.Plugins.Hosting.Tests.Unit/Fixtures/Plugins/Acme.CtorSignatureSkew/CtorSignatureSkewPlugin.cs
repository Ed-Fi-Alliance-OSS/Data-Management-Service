// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;

namespace Acme.CtorSignatureSkew;

public sealed class CtorSignatureSkewPlugin : EdFiApiPlugin
{
    public CtorSignatureSkewPlugin() { }

    /// <summary>Never called. Its signature alone is what the runtime has to resolve.</summary>
    public CtorSignatureSkewPlugin(HostSharedMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
    }

    public override string Name => "Acme.CtorSignatureSkew";
}
