// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using static System.Text.Json.JsonNamingPolicy;

namespace EdFi.DataManagementService.Api.Core
{
    public interface IApiSchemaTranslator
    {
        JsonNode? TranslateToSwagger(string hostUrl, string basePath, bool forDescriptor = false);
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

        private IList<OpenApiServer> GenerateServers(string host, string basePath) =>
            (host == null && basePath == null)
                ? []
                : new List<OpenApiServer> { new OpenApiServer { Url = $"{host}{basePath}" } };

        public JsonNode? TranslateToSwagger(string hostUrl, string basePath, bool forDescriptor = false)
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

                foreach (var resourceSchema in resourceSchemas)
                {
                    var schemaProperties = new Dictionary<string, OpenApiSchema>();
                    var operationParameters = new List<OpenApiParameter>();
                    operationParameters.AddRange(operationCommonParameters);

                    var resouceSchemaValue = resourceSchema.Value?.AsObject();
                    if (resouceSchemaValue != null)
                    {
                        var resourceName = resouceSchemaValue.GetPropertyName();
                        var isDescriptor = resouceSchemaValue["isDescriptor"]?.GetValue<bool>();

                        var openApiSchema = new OpenApiSchema();

                        if (isDescriptor != null && isDescriptor == forDescriptor)
                        {
                            var jsonSchemaForResource = resouceSchemaValue["jsonSchemaForInsert"];
                            if (jsonSchemaForResource != null)
                            {
                                // Schema definition
                                var elementType = jsonSchemaForResource["type"];
                                openApiSchema.Type =
                                    elementType != null ? elementType.GetValue<string>() : string.Empty;
                                var required = jsonSchemaForResource["required"]?.AsArray();
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

                                var propertiesObject = jsonSchemaForResource["properties"]
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
                                        var type = property?["type"]?.GetValue<string>().ToLower();

                                        // Set operation parameters
                                        var documentPathMappings = resouceSchemaValue["documentPathsMapping"]
                                            ?.AsObject()
                                            .ToArray();
                                        var isThereInDocMappings = Array.Exists(
                                            documentPathMappings!,
                                            (x) =>
                                                x.Key.Equals(
                                                    property?.GetPropertyName(),
                                                    StringComparison.InvariantCultureIgnoreCase
                                                )
                                        );

                                        if (property != null && type != "object" && isThereInDocMappings)
                                        {
                                            operationParameters.Add(
                                                new OpenApiParameter
                                                {
                                                    Name = property.GetPropertyName(),
                                                    In = ParameterLocation.Query,
                                                    Description = property["description"]?.GetValue<string>(),
                                                    Schema = new OpenApiSchema()
                                                    {
                                                        Type = type,
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

                                        // Set schema properties
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
                            if (resourceName != null)
                            {
                                var operations = new Dictionary<OperationType, OpenApiOperation>
                                {
                                    {
                                        OperationType.Get,
                                        new OpenApiOperation()
                                        {
                                            Tags = new List<OpenApiTag>
                                            {
                                                new() { Name = $"{resourceName}" }
                                            },
                                            OperationId = $"get{resourceName}",
                                            Summary =
                                                "Retrieves specific resources using the resource's property values (using the \\\"Get\\\" pattern).",
                                            Description =
                                                "This GET operation provides access to resources using the \"Get\" search pattern.  The values of any properties of the resource that are specified will be used to return all matching results (if it exists).",
                                            Parameters = operationParameters,
                                            Responses = new OpenApiResponses()
                                            {
                                                {
                                                    "200",
                                                    new OpenApiResponse
                                                    {
                                                        Description =
                                                            "The requested resource was successfully retrieved.",
                                                        Content = { }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                };
                                paths.Add(
                                    $"/{name}/{resourceName}",
                                    new OpenApiPathItem() { Operations = operations }
                                );
                            }

                            var updatedResourceName = resouceSchemaValue["resourceName"]?.GetValue<string>();
                            components.Schemas.Add(
                                $"edFi_{CamelCase.ConvertName(updatedResourceName!)}",
                                openApiSchema
                            );
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
            var json = swaggerDoc.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
            return JsonNode.Parse(json);
        }
    }
}
