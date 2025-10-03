// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides data model information from ApiSchema without requiring database access
/// </summary>
internal class DataModelInfoProvider(
    IApiSchemaProvider apiSchemaProvider,
    ILogger<DataModelInfoProvider> logger
) : IDataModelInfoProvider
{
    /// <inheritdoc />
    public IList<IDataModelInfo> GetDataModelInfo()
    {
        ApiSchemaDocuments apiSchemaDocuments = new(apiSchemaProvider.GetApiSchemaNodes(), logger);

        IList<IDataModelInfo> result = [];
        foreach (ProjectSchema projectSchema in apiSchemaDocuments.GetAllProjectSchemas())
        {
            result.Add(
                new DataModelInfo(
                    projectSchema.ProjectName.Value,
                    projectSchema.ResourceVersion.Value,
                    projectSchema.Description
                )
            );
        }
        return result;
    }
}
