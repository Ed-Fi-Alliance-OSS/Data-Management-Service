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
    private Guid _currentReloadId;

    public JsonSchema GetOrAdd(
        ResourceName resourceName,
        RequestMethod method,
        Guid reloadId,
        Func<JsonSchema> schemaFactory
    )
    {
        ResetIfReloadChanged(reloadId);

        SchemaCacheKey key = new(resourceName.Value, method, reloadId);
        return _cache.GetOrAdd(key, _ => schemaFactory());
    }

    public void Prime(ApiSchemaDocuments documents, Guid reloadId)
    {
        ResetIfReloadChanged(reloadId);

        foreach (ProjectSchema projectSchema in documents.GetAllProjectSchemas())
        {
            foreach (var resourceNode in projectSchema.GetAllResourceSchemaNodes())
            {
                ResourceSchema resourceSchema = new(resourceNode);
                TryAddOrUpdate(resourceSchema, RequestMethod.POST, reloadId);
                TryAddOrUpdate(resourceSchema, RequestMethod.PUT, reloadId);
            }
        }
    }

    private void TryAddOrUpdate(ResourceSchema resourceSchema, RequestMethod method, Guid reloadId)
    {
        try
        {
            AddOrUpdate(resourceSchema, method, reloadId);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is NullReferenceException)
        {
            // Some unit tests provide minimal schemas that omit JsonSchema definitions.
            // Skip caching for those resources.
        }
    }

    private void AddOrUpdate(ResourceSchema resourceSchema, RequestMethod method, Guid reloadId)
    {
        SchemaCacheKey key = new(resourceSchema.ResourceName.Value, method, reloadId);
        _cache.GetOrAdd(key, _ => CompileSchema(resourceSchema, method));
    }

    private static JsonSchema CompileSchema(ResourceSchema resourceSchema, RequestMethod method)
    {
        var jsonSchemaForResource = resourceSchema.JsonSchemaForRequestMethod(method);
        string stringifiedJsonSchema = JsonSerializer.Serialize(jsonSchemaForResource);
        return JsonSchema.FromText(stringifiedJsonSchema);
    }

    private void ResetIfReloadChanged(Guid reloadId)
    {
        if (_currentReloadId == reloadId)
        {
            return;
        }

        _cache.Clear();
        _currentReloadId = reloadId;
    }

    private readonly record struct SchemaCacheKey(string ResourceName, RequestMethod Method, Guid ReloadId);
}
