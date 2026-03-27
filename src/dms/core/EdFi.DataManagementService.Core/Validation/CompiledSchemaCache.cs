// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Json.Schema;

namespace EdFi.DataManagementService.Core.Validation;

internal sealed class CompiledSchemaCache : ICompiledSchemaCache
{
    private readonly ConcurrentDictionary<SchemaCacheKey, JsonSchema> _cache = new();

    public JsonSchema GetOrAdd(
        ProjectName projectName,
        ResourceName resourceName,
        RequestMethod method,
        Func<JsonSchema> schemaFactory
    )
    {
        SchemaCacheKey key = new(projectName.Value, resourceName.Value, method);
        return _cache.GetOrAdd(key, _ => schemaFactory());
    }

    public void Prime(ApiSchemaDocuments documents)
    {
        foreach (ProjectSchema projectSchema in documents.GetAllProjectSchemas())
        {
            foreach (var resourceNode in projectSchema.GetAllResourceSchemaNodes())
            {
                ResourceSchema resourceSchema = new(resourceNode);
                TryAddOrUpdate(projectSchema.ProjectName, resourceSchema, RequestMethod.POST);
                TryAddOrUpdate(projectSchema.ProjectName, resourceSchema, RequestMethod.PUT);
            }
        }
    }

    private void TryAddOrUpdate(ProjectName projectName, ResourceSchema resourceSchema, RequestMethod method)
    {
        try
        {
            AddOrUpdate(projectName, resourceSchema, method);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is NullReferenceException)
        {
            // Some unit tests provide minimal schemas that omit JsonSchema definitions.
            // Skip caching for those resources.
        }
    }

    private void AddOrUpdate(ProjectName projectName, ResourceSchema resourceSchema, RequestMethod method)
    {
        SchemaCacheKey key = new(projectName.Value, resourceSchema.ResourceName.Value, method);
        _cache.GetOrAdd(key, _ => CompileSchema(resourceSchema, method));
    }

    private static JsonSchema CompileSchema(ResourceSchema resourceSchema, RequestMethod method)
    {
        var jsonSchemaForResource = resourceSchema.JsonSchemaForRequestMethod(method);
        string stringifiedJsonSchema = JsonSerializer.Serialize(jsonSchemaForResource);
        return JsonSchema.FromText(stringifiedJsonSchema);
    }

    private readonly record struct SchemaCacheKey(
        string ProjectName,
        string ResourceName,
        RequestMethod Method
    );
}
