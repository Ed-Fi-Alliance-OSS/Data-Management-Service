// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ValidateRepeatedPropertiesMiddleware(ILogger logger) : IPipelineStep
{
    private static readonly JsonSerializerOptions _serializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ValidateRepeatedPropertiesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId
        );
        if (context.FrontendRequest.Body != null)
        {
            try
            {
                var jsonDocument = JsonDocument.Parse(context.FrontendRequest.Body);
                var validationErrors = new Dictionary<string, List<string>>();

                CheckForDuplicates(jsonDocument.RootElement, "", validationErrors);

                if (validationErrors.Any())
                {
                    var validationErrorsDict = validationErrors.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToArray()
                    );

                    FailureResponseWithErrors failureResponse = ForDataValidation(
                        "Data validation failed. See 'validationErrors' for details.",
                        validationErrorsDict,
                        []
                    );

                    logger.LogDebug(
                        "'{Status}'.'{EndpointName}' - {TraceId}",
                        failureResponse.status.ToString(),
                        context.PathComponents.EndpointName,
                        context.FrontendRequest.TraceId
                    );

                    context.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        Body: JsonSerializer.Serialize(failureResponse, _serializerOptions),
                        Headers: []
                    );
                    return;
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Invalid JSON format  - {TraceId}", context.FrontendRequest.TraceId);

                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: JsonSerializer.Serialize(ex.Message, _serializerOptions),
                    Headers: []
                );

                return;
            }
        }
        await next();
    }

    private void CheckForDuplicates(
        JsonElement element,
        string currentPath,
        Dictionary<string, List<string>> validationErrors
    )
    {
        var propertyNames = new HashSet<string>();

        foreach (var property in element.EnumerateObject())
        {
            string propertyPath = string.IsNullOrEmpty(currentPath)
                ? property.Name
                : $"{currentPath}.{property.Name}";

            // Check for duplicate properties
            if (!propertyNames.Add(property.Name))
            {
                AddValidationError(validationErrors, $"$.{propertyPath}", "This property is duplicated.");
                continue; // Skip further validation for duplicated properties
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CheckForDuplicates(property.Value, propertyPath, validationErrors);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var valuesSet = new HashSet<string>();
                int index = 0;

                foreach (var item in property.Value.EnumerateArray())
                {
                    string itemValue = item.GetRawText();

                    if (!valuesSet.Add(itemValue))
                    {
                        AddValidationError(
                            validationErrors,
                            $"$.{propertyPath}",
                            $"The {OrdinalSuffix((index + 1))} item of the {property.Name} has the same identifying values as another item earlier in the list."
                        );
                    }
                    index++;

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        CheckForDuplicates(item, $"{propertyPath}[{index}]", validationErrors);
                    }
                }
            }
        }
    }

    private static string OrdinalSuffix(int number)
    {
        string ordinaryNumber = number.ToString();
        int nMod100 = number % 100;

        if (nMod100 >= 11 && nMod100 <= 13)
        {
            return string.Concat(ordinaryNumber, "th");
        }

        switch (number % 10)
        {
            case 1:
                return string.Concat(ordinaryNumber, "st");
            case 2:
                return string.Concat(ordinaryNumber, "nd");
            case 3:
                return string.Concat(ordinaryNumber, "rd");
            default:
                return string.Concat(ordinaryNumber, "th");
        }
    }

    private void AddValidationError(
        Dictionary<string, List<string>> validationErrors,
        string key,
        string message
    )
    {
        if (!validationErrors.ContainsKey(key))
        {
            validationErrors[key] = new List<string>();
        }

        if (!validationErrors[key].Contains(message))
        {
            validationErrors[key].Add(message);
        }
    }
}
