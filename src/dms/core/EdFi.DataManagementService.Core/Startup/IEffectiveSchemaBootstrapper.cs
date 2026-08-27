// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Loads, validates, normalizes, and initializes the effective API schema providers.
/// </summary>
public interface IEffectiveSchemaBootstrapper
{
    /// <summary>
    /// Initializes the effective schema providers from the configured API schema source.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}
