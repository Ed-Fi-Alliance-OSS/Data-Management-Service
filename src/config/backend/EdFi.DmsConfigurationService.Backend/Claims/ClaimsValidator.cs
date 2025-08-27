// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
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

    private readonly Lazy<JsonSchema> _jsonSchemaForClaims = new(() =>
    {
        _logger.LogDebug("Loading JSON Schema for Claims validation from embedded resource");

        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = "EdFi.DmsConfigurationService.Backend.Claims.JsonSchemaForClaims.json";

        using Stream? stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not load embedded resource '{resourceName}' from assembly '{assembly.GetName().Name}'"
            );

        using StreamReader reader = new(stream);
        string schemaContent = reader.ReadToEnd();
        _logger.LogDebug("Successfully loaded JSON Schema from embedded resource");
        return JsonSchema.FromText(schemaContent);
    });

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
    public List<ClaimsValidationFailure> Validate(JsonNode claimsDocument)
    {
        try
        {
            EvaluationResults results = _jsonSchemaForClaims.Value.Evaluate(
                claimsDocument,
                _validatorOptions
            );
            return ValidationErrorsFrom(results);
        }
        catch (ArgumentException ex)
        {
            const string Failure = "Invalid JSON format for claims validation";
            _logger.LogError(ex, Failure);
            return [new(new("$."), [Failure])];
        }
        catch (InvalidOperationException ex)
        {
            const string Failure =
                "ClaimsValidator failed to validate, check server configuration for JsonSchemaForClaims.json";
            _logger.LogError(ex, Failure);
            return [new(new("$."), [Failure])];
        }
    }
}
