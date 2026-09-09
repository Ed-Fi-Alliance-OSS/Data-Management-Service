// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.HostShared;
using EdFi.Api.Plugins;

namespace Acme.NewerContract;

public sealed class NewerContractPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.NewerContract";
}

/// <summary>
/// Derives from a type in an assembly this plugin does not ship, so enumerating the exported types
/// produces an error naming Acme.HostShared. A test asserts that error is not the one reported.
/// </summary>
public sealed class RecognizableTypeLoadFailure : HostSharedMarker { }
