// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ResourceLoadOrder;

internal class CoreProjectNameProvider(
    IApiSchemaProvider _apiSchemaProvider,
    ILogger<CoreProjectNameProvider> _logger
) : ICoreProjectNameProvider
{
    private readonly ProjectName _coreProjectName = new ApiSchemaDocuments(
        _apiSchemaProvider.GetApiSchemaNodes(),
        _logger
    )
        .GetCoreProjectSchema()
        .ProjectName;

    public ProjectName GetCoreProjectName() => _coreProjectName;
}
