// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Validates descriptor resource schemas against the canonical <c>dms.Descriptor</c> contract.
/// </summary>
internal static class DescriptorSchemaValidator
{
    /// <summary>
    /// Determines whether a resource schema represents a descriptor resource.
    /// </summary>
    /// <param name="resourceSchema">The resource schema node from ApiSchema.json.</param>
    /// <returns><c>true</c> if the resource is a descriptor; otherwise, <c>false</c>.</returns>
    public static bool IsDescriptorResource(JsonNode? resourceSchema)
    {
        if (resourceSchema is null)
        {
            return false;
        }

        return resourceSchema["isDescriptor"]?.GetValue<bool>() ?? false;
    }

    /// <summary>
    /// Validates a descriptor resource schema against the canonical descriptor contract.
    /// </summary>
    /// <param name="resourceSchema">The resource schema node from ApiSchema.json.</param>
    /// <returns>Validation result with errors if incompatible.</returns>
    public static DescriptorValidationResult ValidateDescriptorSchema(JsonNode? resourceSchema)
    {
        if (resourceSchema is null)
        {
            return new DescriptorValidationResult(false, ["Resource schema is null"]);
        }

        var jsonSchemaNode = resourceSchema["jsonSchemaForInsert"];
        if (jsonSchemaNode is null)
        {
            return new DescriptorValidationResult(
                false,
                ["Missing 'jsonSchemaForInsert' in resource schema"]
            );
        }

        var properties = jsonSchemaNode["properties"]?.AsObject();
        if (properties is null)
        {
            return new DescriptorValidationResult(
                false,
                ["Missing 'properties' section in jsonSchemaForInsert"]
            );
        }

        var errors = new List<string>();

        ValidateRequiredStringField(properties, "namespace", errors);
        ValidateRequiredStringField(properties, "codeValue", errors);

        ValidateOptionalStringField(properties, "shortDescription", errors);
        ValidateOptionalStringField(properties, "description", errors);

        ValidateOptionalDateField(properties, "effectiveBeginDate", errors);
        ValidateOptionalDateField(properties, "effectiveEndDate", errors);

        var requiredFields = jsonSchemaNode["required"]?.AsArray();
        if (requiredFields is not null)
        {
            var requiredFieldNames = requiredFields
                .Select(f => f?.GetValue<string>())
                .Where(f => f is not null)
                .ToHashSet(StringComparer.Ordinal);

            if (!requiredFieldNames.Contains("namespace"))
            {
                errors.Add("Field 'namespace' must be required");
            }

            if (!requiredFieldNames.Contains("codeValue"))
            {
                errors.Add("Field 'codeValue' must be required");
            }

            foreach (var fieldName in requiredFieldNames)
            {
                if (!IsAllowedRequiredField(fieldName!) && properties.ContainsKey(fieldName!))
                {
                    errors.Add($"Descriptor schema has unexpected required fields: {fieldName}");
                }
            }
        }
        else
        {
            errors.Add("Required fields array is missing");
        }

        return new DescriptorValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateRequiredStringField(
        JsonObject properties,
        string fieldName,
        List<string> errors
    )
    {
        if (!properties.ContainsKey(fieldName))
        {
            errors.Add($"Required field '{fieldName}' is missing");
            return;
        }

        var fieldNode = properties[fieldName];
        var fieldType = fieldNode?["type"]?.GetValue<string>();
        if (fieldType != "string")
        {
            errors.Add($"Field '{fieldName}' must be of type 'string', but found '{fieldType}'");
        }
    }

    private static void ValidateOptionalStringField(
        JsonObject properties,
        string fieldName,
        List<string> errors
    )
    {
        if (!properties.ContainsKey(fieldName))
        {
            return;
        }

        var fieldNode = properties[fieldName];
        var fieldType = fieldNode?["type"]?.GetValue<string>();
        if (fieldType != "string")
        {
            errors.Add($"Field '{fieldName}' must be of type 'string', but found '{fieldType}'");
        }
    }

    private static void ValidateOptionalDateField(
        JsonObject properties,
        string fieldName,
        List<string> errors
    )
    {
        if (!properties.ContainsKey(fieldName))
        {
            return;
        }

        var fieldNode = properties[fieldName];
        var fieldType = fieldNode?["type"]?.GetValue<string>();
        if (fieldType != "string")
        {
            errors.Add(
                $"Field '{fieldName}' must be of type 'string' with format 'date', but found type '{fieldType}'"
            );
            return;
        }

        var format = fieldNode?["format"]?.GetValue<string>();
        if (format != "date")
        {
            errors.Add($"Field '{fieldName}' must have format 'date', but found format '{format ?? "none"}'");
        }
    }

    private static bool IsAllowedRequiredField(string fieldName)
    {
        return fieldName switch
        {
            "namespace" => true,
            "codeValue" => true,
            "shortDescription" => true,
            _ => false,
        };
    }
}

/// <summary>
/// Result of descriptor schema validation.
/// </summary>
/// <param name="IsValid">Whether the schema is valid.</param>
/// <param name="Errors">Validation error messages.</param>
internal sealed record DescriptorValidationResult(bool IsValid, IReadOnlyList<string> Errors);
