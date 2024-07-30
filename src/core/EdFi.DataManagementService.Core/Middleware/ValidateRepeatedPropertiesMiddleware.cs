// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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

                CheckForDuplicateProperties(jsonDocument.RootElement, "", validationErrors);

                CheckForDuplicateValues(jsonDocument.RootElement, "", validationErrors);

                if (validationErrors.Any())
                {
                    var validationErrorsDict = validationErrors.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToArray()
                    );

                    FailureResponseWithErrors failureResponse;

                    failureResponse = ForDataValidation(
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

    private void CheckForDuplicateProperties(
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

            if (!propertyNames.Add(property.Name))
            {
                AddValidationError(validationErrors, $"$.{propertyPath}", "This property is duplicated.");
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CheckForDuplicateProperties(property.Value, propertyPath, validationErrors);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    string itemPath = $"{propertyPath}[{index}]";
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        CheckForDuplicateProperties(item, itemPath, validationErrors);
                    }
                    index++;
                }
            }
        }
    }

    private void CheckForDuplicateValues(
        JsonElement element,
        string currentPath,
        Dictionary<string, List<string>> validationErrors
    )
    {
        foreach (var property in element.EnumerateObject())
        {
            string propertyPath = string.IsNullOrEmpty(currentPath)
                ? property.Name
                : $"{currentPath}.{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Array)
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
                            $"The {AddOrdinalSuffix((index + 1).ToString())} item of the {property.Name} has the same identifying values as another item earlier in the list."
                        );
                    }
                    index++;
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CheckForDuplicateValues(property.Value, propertyPath, validationErrors);
            }
        }
    }

    public static bool IsStringNumeric(string str)
    {
        double result;
        return double.TryParse(str, NumberStyles.Float, NumberFormatInfo.CurrentInfo, out result);
    }

    public static string AddOrdinalSuffix(string number)
    {
        if (IsStringNumeric(number))
        {
            int n = int.Parse(number);
            int nMod100 = n % 100;

            if (nMod100 >= 11 && nMod100 <= 13)
            {
                return string.Concat(number, "th");
            }

            switch (n % 10)
            {
                case 1:
                    return string.Concat(number, "st");
                case 2:
                    return string.Concat(number, "nd");
                case 3:
                    return string.Concat(number, "rd");
                default:
                    return string.Concat(number, "th");
            }
        }

        return number;
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
