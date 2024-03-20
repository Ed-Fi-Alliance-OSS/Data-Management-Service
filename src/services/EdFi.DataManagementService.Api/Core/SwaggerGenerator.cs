// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using Microsoft.OpenApi.Models;
using static System.Text.Json.JsonNamingPolicy;

namespace EdFi.DataManagementService.Api.Core
{
    public interface IApiSchemaTranslator
    {
        JsonNode? TranslateToSwagger(string hostUrl, string basePath);
    }

    public class ApiSchemaTranslator : IApiSchemaTranslator
    {
        private readonly ILogger<ApiSchemaTranslator> _logger;
        private readonly IApiSchemaProvider _apiSchemaProvider;

        public ApiSchemaTranslator(ILogger<ApiSchemaTranslator> logger, IApiSchemaProvider apiSchemaProvider)
        {
            _logger = logger;
            _apiSchemaProvider = apiSchemaProvider;
        }

        private IList<OpenApiServer> GenerateServers(string host, string basePath)
        {
            return (host == null && basePath == null)
                ? new List<OpenApiServer>()
                : new List<OpenApiServer> { new OpenApiServer { Url = $"{host}{basePath}" } };
        }

        public JsonNode? TranslateToSwagger(string hostUrl, string basePath)
        {
            _logger.LogDebug("Entering Json ApiSchemaTranslator");

            var schema = _apiSchemaProvider.ApiSchemaRootNode;
            var projectSchemas = schema["projectSchemas"]?.AsObject().ToArray();
            if (projectSchemas == null || projectSchemas.Length == 0)
            {
                var error = "No data model details found";
                _logger.LogCritical(error);
                throw new Exception(error);
            }

            var dataModels = projectSchemas.Where(x => x.Value != null).Select(x => x.Value).ToList();
            var swaggerDoc = new OpenApiDocument();
            foreach (var model in dataModels)
            {
                var name = model?["projectName"]?.GetValue<string>().ToLower() ?? string.Empty;

                if (model == null)
                {
                    var error = "No data model details found";
                    _logger.LogCritical(error);
                    throw new Exception(error);
                }
                var resourceSchemas = model["resourceSchemas"]?.AsObject().ToArray();

                if (resourceSchemas == null || resourceSchemas.Length == 0)
                {
                    var error = "No resource schemas found";
                    _logger.LogCritical(error);
                    throw new Exception(error);
                }
                var paths = new OpenApiPaths();
                var operationCommonParameters = new List<OpenApiParameter>
                {
                    new OpenApiParameter
                    {
                        Name = "offset",
                        Reference = new OpenApiReference() { Type = ReferenceType.Parameter, Id = "offset" }
                    },
                    new OpenApiParameter
                    {
                        Name = "limit",
                        Reference = new OpenApiReference() { Type = ReferenceType.Parameter, Id = "limit" }
                    },
                    new OpenApiParameter
                    {
                        Name = "totalCount",
                        Reference = new OpenApiReference()
                        {
                            Type = ReferenceType.Parameter,
                            Id = "totalCount"
                        }
                    }
                };
                var components = new OpenApiComponents
                {
                    Parameters = new Dictionary<string, OpenApiParameter>
                    {
                        {
                            "offset",
                            new OpenApiParameter
                            {
                                Name = "offset",
                                In = ParameterLocation.Query,
                                Description =
                                    "Indicates how many items should be skipped before returning results.",
                                Schema = new OpenApiSchema() { Type = "integer", Format = "int32" }
                            }
                        },
                        {
                            "limit",
                            new OpenApiParameter
                            {
                                Name = "limit",
                                In = ParameterLocation.Query,
                                Description =
                                    "Indicates the maximum number of items that should be returned in the results.",
                                Schema = new OpenApiSchema() { Type = "integer", Format = "int32" }
                            }
                        },
                        {
                            "totalCount",
                            new OpenApiParameter
                            {
                                Name = "totalCount",
                                In = ParameterLocation.Query,
                                Description =
                                    "Indicates if the total number of items available should be returned in the 'Total-Count' header of the response.  If set to false, 'Total-Count' header will not be provided.",
                                Schema = new OpenApiSchema() { Type = "boolean" }
                            }
                        }
                    }
                };

                foreach (var resourceSchema in resourceSchemas.Take(20))
                {
                    var schemaProperties = new Dictionary<string, OpenApiSchema>();
                    var operationParameters = operationCommonParameters;

                    var resouceSchemaValue = resourceSchema.Value?.AsObject();
                    if (resouceSchemaValue != null)
                    {
                        var resourceName = resouceSchemaValue["resourceName"]?.GetValue<string>();
#pragma warning disable S1481 // Unused local variables should be removed
                        var isDescriptor = resouceSchemaValue["isDescriptor"]?.GetValue<bool>();
                        if (isDescriptor != null && isDescriptor == false)
                        {
#pragma warning restore S1481 // Unused local variables should be removed
#pragma warning disable S125
                            //var jsonSchemaForQuery = resouceSchemaValue.SelectNodeFromPath(
                            //    "$.jsonSchemaForInsert",
                            //    _logger

                            // Sections of code should not be commented out
                            //);

                            var jsonSchemaForQuery = resouceSchemaValue["jsonSchemaForInsert"];
#pragma warning restore S125 // Sections of code should not be commented out
                            if (jsonSchemaForQuery != null)
                            {
                                var propertiesObject = jsonSchemaForQuery["properties"]?.AsObject().ToArray();
                                var properties = propertiesObject
                                    ?.Where(x => x.Value != null)
                                    .Select(x => x.Value)
                                    .ToList();
                                if (properties != null && properties.Any())
                                {
                                    foreach (var property in properties)
                                    {
                                        if (property != null)
                                        {
                                            operationParameters.Add(
                                                new OpenApiParameter
                                                {
                                                    Name = property.GetPropertyName(),
                                                    In = ParameterLocation.Query,
                                                    Description = property["description"]?.GetValue<string>(),
                                                    Schema = new OpenApiSchema()
                                                    {
                                                        Type = property["type"]?.GetValue<string>(),
                                                        MaxLength =
                                                            property["maxLength"] == null
                                                                ? null
                                                                : property["maxLength"]?.GetValue<int>(),
                                                        MinLength =
                                                            property["minLength"] == null
                                                                ? null
                                                                : property["minLength"]?.GetValue<int>(),
                                                        Format =
                                                            property["format"] == null
                                                                ? string.Empty
                                                                : property["format"]?.GetValue<string>()
                                                    }
                                                }
                                            );
                                        }
                                    }
                                }
                            }
                            if (resourceName != null)
                            {
                                var updatedResourceName = CamelCase.ConvertName(resourceName);
                                var operations = new Dictionary<OperationType, OpenApiOperation>
                                {
                                    {
                                        OperationType.Get,
                                        new OpenApiOperation()
                                        {
                                            Tags = new List<OpenApiTag>
                                            {
                                                new() { Name = updatedResourceName }
                                            },
                                            OperationId = $"get{updatedResourceName}",
                                            Summary =
                                                "Retrieves specific resources using the resource's property values (using the \\\"Get\\\" pattern).",
                                            Description =
                                                "This GET operation provides access to resources using the \"Get\" search pattern.  The values of any properties of the resource that are specified will be used to return all matching results (if it exists).",
                                            Parameters = operationParameters
                                        }
                                    }
                                };
                                paths.Add(
                                    $"/{name}/{updatedResourceName}",
                                    new OpenApiPathItem() { Operations = operations }
                                );
#pragma warning disable S125 // Sections of code should not be commented out
                                //var jsonSchemaForInsert = resouceSchemaValue.SelectNodeFromPath(
                                //    "jsonSchemaForInsert",
                                //    _logger


                                //);
                                var openApiSchema = new OpenApiSchema();
#pragma warning restore S125 // Sections of code should not be commented out
                                var jsonSchemaForInsert = resouceSchemaValue["jsonSchemaForInsert"];
                                if (jsonSchemaForInsert != null)
                                {
                                    var elementType = jsonSchemaForInsert["type"];
                                    openApiSchema.Type =
                                        elementType != null ? elementType.GetValue<string>() : string.Empty;
                                    var required = jsonSchemaForInsert["required"]?.AsArray();
                                    var requiredList = new HashSet<string>();
                                    if (required != null)
                                    {
                                        foreach (var requiredItem in required)
                                        {
                                            var value =
                                                requiredItem != null ? requiredItem.GetValue<string>() : null;
                                            if (value != null)
                                            {
                                                requiredList.Add(value);
                                            }
                                        }
                                        openApiSchema.Required = requiredList;
                                    }
                                    var propertiesObject = jsonSchemaForInsert["properties"]
                                        ?.AsObject()
                                        .ToArray();
                                    var properties = propertiesObject
                                        ?.Where(x => x.Value != null)
                                        .Select(x => x.Value)
                                        .ToList();
                                    if (properties != null && properties.Any())
                                    {
                                        foreach (var property in properties)
                                        {
                                            if (property != null)
                                            {
                                                var propertyName = property.GetPropertyName();
                                                schemaProperties.Add(
                                                    propertyName,
                                                    new OpenApiSchema
                                                    {
                                                        Type = property["type"]?.GetValue<string>(),
                                                        MaxLength =
                                                            property["maxLength"] == null
                                                                ? null
                                                                : property["maxLength"]?.GetValue<int>(),
                                                        MinLength =
                                                            property["minLength"] == null
                                                                ? null
                                                                : property["minLength"]?.GetValue<int>(),
                                                        Format =
                                                            property["format"] == null
                                                                ? string.Empty
                                                                : property["format"]?.GetValue<string>(),
                                                        Description =
                                                            property["description"] == null
                                                                ? string.Empty
                                                                : property["description"]?.GetValue<string>()
                                                    }
                                                );
                                            }
                                        }
                                        openApiSchema.Properties = schemaProperties;
                                    }
                                }
                                components.Schemas.Add($"edFi_{updatedResourceName}", openApiSchema);
                            }
                        }
                    }
                }

                swaggerDoc = new OpenApiDocument
                {
                    Info = new OpenApiInfo
                    {
                        Title = "Ed-Fi Operational Data Store API",
                        Description =
                            "The Ed-Fi ODS / API enables applications to read and write education data stored in an Ed-Fi ODS through a secure REST interface. \n***\n > *Note: Consumers of ODS / API information should sanitize all data for display and storage. The ODS / API provides reasonable safeguards against cross-site scripting attacks and other malicious content, but the platform does not and cannot guarantee that the data it contains is free of all potentially harmful content.* \n***\n",
                        Version = "3",
                    },
                    Servers = GenerateServers(hostUrl, basePath),
                    Paths = paths,
                    Components = components
                };
            }
            return JsonNode.Parse(JsonSerializer.Serialize(swaggerDoc));
        }
    }
}
