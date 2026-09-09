// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins;

namespace Acme.NoSubclass;

/// <summary>
/// A public exported type that names the contract without deriving from it, which is the mistake this
/// fixture exists to reproduce.
/// </summary>
public sealed class NotAPlugin
{
    /// <summary>Names the contract type so the reference is real rather than incidental.</summary>
    public static string ContractTypeName => typeof(EdFiApiPlugin).FullName ?? nameof(EdFiApiPlugin);
}
