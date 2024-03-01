// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface ISchemaValidatorResolver
{
    ISchemaValidator Resolve(ValidatorContext? validatorContext);
}

public class SchemaValidatorResolver() : ISchemaValidatorResolver
{
    public ISchemaValidator Resolve(ValidatorContext? validatorContext)
    {
        if (validatorContext != null)
        {
            if (!validatorContext.IsDescriptor)
            {
                var schemaValidator = new ResourceSchemaValidator
                {
                    RequestActionMethod = validatorContext.RequestActionMethod,
                    ResourceJsonSchema = validatorContext.ResourceJsonSchema
                };
                return schemaValidator;
            }
            else
            {
                throw new NotImplementedException();
            }
        }
        else
        {
            throw new NotImplementedException();
        }
    }
}

public interface ISchemaValidator
{
    ResourceSchema? ResourceJsonSchema { get; set; }
    RequestMethod RequestActionMethod { get; set; }
    JsonSchema GetSchema();
}

public class ResourceSchemaValidator : ISchemaValidator
{
    public ResourceSchema? ResourceJsonSchema { get; set; }
    public RequestMethod RequestActionMethod { get; set; }
    public JsonSchema GetSchema()
    {
        if (ResourceJsonSchema == null)
        {
            throw new Exception("Resource schema not found.");
        }
        var schemaNode = ResourceJsonSchema.JsonSchemaForRequestMethod(RequestActionMethod);
        var resourceJsonSchema = JsonSerializer.Serialize(schemaNode);
        return JsonSchema.FromText(resourceJsonSchema);
    }
}
