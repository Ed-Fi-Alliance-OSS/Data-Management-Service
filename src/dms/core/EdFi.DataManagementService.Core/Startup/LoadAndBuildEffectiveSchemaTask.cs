// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that loads, validates, and builds the effective API schema.
/// This task runs early in the startup sequence to ensure schemas are available
/// before any request processing begins.
/// </summary>
internal class LoadAndBuildEffectiveSchemaTask(
    IApiSchemaProvider apiSchemaProvider,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    IApiSchemaInputNormalizer inputNormalizer,
    EffectiveSchemaSetBuilder effectiveSchemaSetBuilder,
    ILogger<LoadAndBuildEffectiveSchemaTask> logger
) : IDmsStartupTask
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = effectiveApiSchemaProvider;
    private readonly IEffectiveSchemaSetProvider _effectiveSchemaSetProvider = effectiveSchemaSetProvider;
    private readonly IApiSchemaInputNormalizer _inputNormalizer = inputNormalizer;
    private readonly EffectiveSchemaSetBuilder _effectiveSchemaSetBuilder = effectiveSchemaSetBuilder;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Name => "Load and Build Effective Schema";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return EffectiveSchemaBootstrapperCore.InitializeAsync(
            _apiSchemaProvider,
            _effectiveApiSchemaProvider,
            _effectiveSchemaSetProvider,
            _inputNormalizer,
            _effectiveSchemaSetBuilder,
            _logger,
            cancellationToken
        );
    }
}
