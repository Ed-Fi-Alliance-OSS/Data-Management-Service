// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Validator of Claims JSON structures
/// </summary>
public interface IClaimsValidator
{
    /// <summary>
    /// JSON Schema validation of a Claims document
    /// </summary>
    List<ClaimsValidationFailure> Validate(JsonNode claimsContent);
}

/// <summary>
/// Validator of Claims JSON structures using JSON Schema
/// </summary>
public class ClaimsValidator(ILogger<ClaimsValidator> _logger) : IClaimsValidator
{
    private static readonly EvaluationOptions _validatorOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private readonly Lazy<JsonSchema> _jsonSchemaForClaims = new(() => LoadSchema(_logger));

    internal ClaimsValidator(ILogger<ClaimsValidator> logger, Lazy<JsonSchema> jsonSchemaForClaims)
        : this(logger)
    {
        _jsonSchemaForClaims = jsonSchemaForClaims;
    }

    private static JsonSchema LoadSchema(ILogger<ClaimsValidator> logger)
    {
        logger.LogDebug("Loading JSON Schema for Claims validation from embedded resource");

        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = "EdFi.DmsConfigurationService.Backend.Claims.JsonSchemaForClaims.json";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not load embedded resource '{resourceName}' from assembly '{assembly.GetName().Name}'"
            );

        using StreamReader reader = new(stream);
        string schemaContent = reader.ReadToEnd();
        logger.LogDebug("Successfully loaded JSON Schema from embedded resource");
        return ParseSchema(schemaContent, resourceName);
    }

    // A malformed embedded schema is a build/configuration fault, not caller input, so it surfaces
    // as the operation failure the upload service already maps to a sanitized 500.
    internal static JsonSchema ParseSchema(string schemaContent, string resourceName)
    {
        try
        {
            return JsonSchema.FromText(schemaContent);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Embedded claims schema '{resourceName}' is not valid JSON.",
                ex
            );
        }
    }

    /// <summary>
    /// Converts JSON Schema evaluation results into a list of validation failures with property paths and error messages
    /// </summary>
    private static List<ClaimsValidationFailure> ValidationErrorsFrom(EvaluationResults results)
    {
        Dictionary<string, List<string>> validationErrorsByPath = [];

        foreach (EvaluationResults detail in results.Details)
        {
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

        List<ClaimsValidationFailure> validationErrors = [];
        validationErrors.AddRange(
            validationErrorsByPath.Select(kvp => new ClaimsValidationFailure(new(kvp.Key), kvp.Value))
        );

        return validationErrors;
    }

    /// <summary>
    /// JSON Schema validation of a Claims document
    /// </summary>
    public List<ClaimsValidationFailure> Validate(JsonNode claimsContent)
    {
        try
        {
            EvaluationResults results = _jsonSchemaForClaims.Value.Evaluate(claimsContent, _validatorOptions);
            return ValidationErrorsFrom(results);
        }
        catch (ArgumentException ex)
        {
            const string Failure = "Invalid JSON format for claims validation";
            _logger.LogError(ex, Failure);
            return [new(new("$."), [Failure])];
        }
    }
}
