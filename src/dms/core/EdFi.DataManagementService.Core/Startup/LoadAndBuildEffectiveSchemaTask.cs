// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that loads, validates, and builds the effective API schema.
/// This task runs early in the startup sequence to ensure schemas are available
/// before any request processing begins.
/// </summary>
internal class LoadAndBuildEffectiveSchemaTask(IEffectiveSchemaBootstrapper effectiveSchemaBootstrapper)
    : IDmsStartupTask
{
    private readonly IEffectiveSchemaBootstrapper _effectiveSchemaBootstrapper = effectiveSchemaBootstrapper;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Name => "Load and Build Effective Schema";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken) =>
        _effectiveSchemaBootstrapper.InitializeAsync(cancellationToken);
}
