// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
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
    /// The JSON Schema keyword that selects a branch rather than asserting anything about the document.
    /// </summary>
    private const string ConditionSelectorKeyword = "if";

    /// <summary>
    /// The keywords whose following evaluation-path segment is a name the schema author chose rather than
    /// another keyword. A property or definition named <c>if</c> is reached through one of these, and its
    /// failures assert something about the document.
    /// </summary>
    private static readonly FrozenSet<string> _authoredNameKeywords = FrozenSet.ToFrozenSet(
        ["properties", "patternProperties", "dependentSchemas", "$defs", "definitions"],
        StringComparer.Ordinal
    );

    /// <summary>
    /// Whether an evaluation result came from inside a condition selector. Such a result reports which
    /// branch was chosen, not whether the document is valid: a failing <c>if</c> means the <c>then</c>
    /// branch does not apply, and the assertions that do apply are reported by the selected branch. List
    /// output flattens every evaluated node, so these have to be excluded by evaluation path rather than
    /// by overall validity, and only a segment in keyword position counts — a <c>then</c> or <c>else</c>
    /// result, an instance path that happens to contain the same text, or a schema property named for the
    /// keyword, is a real failure.
    /// </summary>
    private static bool IsConditionSelectorResult(EvaluationResults detail)
    {
        string[] segments = [.. detail.EvaluationPath.Select(segment => segment.ToString())];

        return segments
            .Select((segment, index) => (segment, index))
            .Any(evaluated =>
                string.Equals(evaluated.segment, ConditionSelectorKeyword, StringComparison.Ordinal)
                && (evaluated.index == 0 || !_authoredNameKeywords.Contains(segments[evaluated.index - 1]))
            );
    }

    /// <summary>
    /// Converts JSON Schema evaluation results into a list of validation failures with property paths and error messages
    /// </summary>
    private static List<SchemaValidationFailure> ValidationErrorsFrom(EvaluationResults results)
    {
        Dictionary<string, List<string>> validationErrorsByPath = new();

        foreach (var detail in results.Details)
        {
            if (IsConditionSelectorResult(detail))
            {
                continue;
            }

            string propertyPathAndName = "$.";

            if (detail.InstanceLocation.Count != 0)
            {
                propertyPathAndName = $"${detail.InstanceLocation.ToString().Replace("/", ".")}";
            }

            if (detail.Errors == null || !detail.Errors.Any())
            {
                continue;
            }

            if (!validationErrorsByPath.ContainsKey(propertyPathAndName))
            {
                validationErrorsByPath[propertyPathAndName] = [];
            }

            foreach (var error in detail.Errors)
            {
                validationErrorsByPath[propertyPathAndName].Add(error.Value);
            }
        }

        List<SchemaValidationFailure> validationErrors = [];
        validationErrors.AddRange(
            validationErrorsByPath.Select(kvp => new SchemaValidationFailure(
                new JsonPath(kvp.Key),
                kvp.Value
            ))
        );

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
