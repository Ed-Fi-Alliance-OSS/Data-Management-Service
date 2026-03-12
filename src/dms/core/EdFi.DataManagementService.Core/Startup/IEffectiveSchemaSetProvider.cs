// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Provides access to the authoritative <see cref="EffectiveSchemaSet" /> built during startup.
/// </summary>
public interface IEffectiveSchemaSetProvider
{
    /// <summary>
    /// Gets the effective schema set built from the normalized startup schema payload.
    /// </summary>
    EffectiveSchemaSet EffectiveSchemaSet { get; }

    /// <summary>
    /// Gets whether the provider has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Stores the authoritative startup <see cref="EffectiveSchemaSet" />.
    /// </summary>
    void Initialize(EffectiveSchemaSet effectiveSchemaSet);
}
