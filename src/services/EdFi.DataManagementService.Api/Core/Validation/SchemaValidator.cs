// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface ISchemaValidator
{
    /// <summary>
    /// Gets resource schema
    /// </summary>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    JsonSchema GetSchema(ValidatorContext validatorContext);
}

public class SchemaValidator : ISchemaValidator
{
    public JsonSchema GetSchema(ValidatorContext validatorContext)
    {
        var requestActionMethod = validatorContext.RequestActionMethod;
        var resourceJsonSchema = validatorContext.ResourceJsonSchema
            ?? throw new Exception("Resource schema not found.");
        var schemaNode = resourceJsonSchema.JsonSchemaForRequestMethod(requestActionMethod);
        var resourceSchema = JsonSerializer.Serialize(schemaNode);
        return JsonSchema.FromText(resourceSchema);
    }
}
