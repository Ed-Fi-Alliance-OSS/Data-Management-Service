// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Validator of ApiSchemas
/// </summary>
internal interface IApiSchemaValidator
{
    /// <summary>
    /// JSON Schema validation of an ApiSchema
    /// </summary>
    List<SchemaValidationFailure> Validate(JsonNode apiSchemaContent);
}

/// <summary>
/// Validator of ApiSchemas
/// </summary>
internal class ApiSchemaValidator(ILogger<ApiSchemaValidator> _logger) : IApiSchemaValidator
{
    private static readonly EvaluationOptions _validatorOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private readonly Lazy<JsonSchema> _jsonSchema = new(() =>
    {
        _logger.LogDebug("Entering _jsonSchemaForApiSchema");

        string schemaContent = File.ReadAllText(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "ApiSchema",
                "JsonSchemaForApiSchema.json"
            )
        );
        return JsonSchema.FromText(schemaContent);
    });

    /// <summary>
    /// Converts JSON Schema evaluation results into a list of validation failures with property paths and error messages
    /// </summary>
    private static List<SchemaValidationFailure> ValidationErrorsFrom(EvaluationResults results)
    {
        var validationErrorsByPath = new Dictionary<string, List<string>>();

        foreach (var detail in results.Details)
        {
            var propertyPathAndName = "$.";

            if (detail.InstanceLocation != null && detail.InstanceLocation.Count != 0)
            {
                propertyPathAndName = $"${detail.InstanceLocation.ToString().Replace("/", ".")}";
            }

            if (detail.Errors != null && detail.Errors.Any())
            {
                if (!validationErrorsByPath.ContainsKey(propertyPathAndName))
                {
                    validationErrorsByPath[propertyPathAndName] = [];
                }

                foreach (var error in detail.Errors)
                {
                    validationErrorsByPath[propertyPathAndName].Add(error.Value);
                }
            }
        }

        List<SchemaValidationFailure> validationErrors = [];
        foreach (var kvp in validationErrorsByPath)
        {
            validationErrors.Add(new(new(kvp.Key), kvp.Value));
        }

        return validationErrors;
    }

    /// <summary>
    /// JSON Schema validation of an ApiSchema
    /// </summary>
    public List<SchemaValidationFailure> Validate(JsonNode apiSchemaContent)
    {
        try
        {
            EvaluationResults results = _jsonSchema.Value.Evaluate(apiSchemaContent, _validatorOptions);
            return ValidationErrorsFrom(results);
        }
        catch (Exception ex)
        {
            const string CriticalFailure =
                "ApiSchemaValidator failed to validate, check server configuration for JsonSchemaForApiSchema.json";
            _logger.LogCritical(ex, CriticalFailure);
            return [new(new("$."), [CriticalFailure])];
        }
    }
}
