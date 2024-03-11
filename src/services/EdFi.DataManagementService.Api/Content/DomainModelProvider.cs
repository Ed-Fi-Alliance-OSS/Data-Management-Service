// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.ApiSchema;

namespace EdFi.DataManagementService.Api.Content;

public interface IDomainModelProvider
{
    /// <summary>
    /// Provides list of data models from api schema
    /// </summary>
    /// <returns></returns>
    IEnumerable<DataModel> GetDataModels();
}

public record DataModel(string name, string version, string informationalVersion);

public class DomainModelProvider : IDomainModelProvider
{
    private readonly IApiSchemaProvider _apiSchemaProvider;

    public DomainModelProvider(IApiSchemaProvider apiSchemaProvider)
    {
        _apiSchemaProvider = apiSchemaProvider;
    }

    public IEnumerable<DataModel> GetDataModels()
    {
        var schema = _apiSchemaProvider.ApiSchemaRootNode;

        var projectSchemas =
            (schema["projectSchemas"]?.AsObject().ToArray())
            ?? throw new Exception("No data model details found");

        List<DataModel> result = [];
        var dataModels = projectSchemas.Where(x => x.Value != null).Select(x => x.Value).ToList();

        foreach (var model in dataModels)
        {
            var name = model?["projectName"]?.GetValue<string>() ?? string.Empty;
            var version = model?["projectVersion"]?.GetValue<string>() ?? string.Empty;
            var informationalVersion = model?["description"]?.GetValue<string>() ?? string.Empty;

            result.Add(new DataModel(name, version, informationalVersion));
        }
        return result;
    }
}
