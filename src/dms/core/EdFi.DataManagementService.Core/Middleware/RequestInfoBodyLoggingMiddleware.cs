// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class RequestInfoBodyLoggingMiddleware(ILogger _logger, bool _maskRequestBodyInLogs) : IPipelineStep
{
    private const string MessageBody =
        "Incoming {Method} request to {Path} with body structure: {Body} - {TraceId}";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (_logger.IsEnabled(LogLevel.Debug) && !string.IsNullOrEmpty(requestInfo.FrontendRequest.Body))
        {
            _logger.LogDebug(
                "Entering RequestInfoBodyLoggingMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            string body = UtilityService.MinifyRegex().Replace(requestInfo.FrontendRequest.Body, "$1");

            if (!_maskRequestBodyInLogs)
            {
                _logger.LogDebug(
                    MessageBody,
                    requestInfo.Method,
                    requestInfo.FrontendRequest.Path,
                    body,
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }
            else
            {
                _logger.LogDebug(
                    MessageBody,
                    requestInfo.Method,
                    requestInfo.FrontendRequest.Path,
                    MaskRequestBody(body, _logger),
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }
        }
        await next();
    }

    private static string MaskRequestBody(string body, ILogger logger)
    {
        try
        {
            // Deserialize the JSON body in a dynamic object
            JsonDocument jsonDoc = JsonDocument.Parse(body);
            JsonElement maskedJson = MaskJsonElement(jsonDoc.RootElement);
            return JsonSerializer.Serialize(maskedJson);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Error while masking request body.");
            return string.Empty;
        }
    }

    private static JsonElement MaskJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, JsonElement> dictionary = new();
                foreach (var property in element.EnumerateObject())
                {
                    dictionary[property.Name] = MaskJsonElement(property.Value);
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dictionary));

            case JsonValueKind.Array:
                List<JsonElement> maskedArray = [];
                maskedArray.AddRange(element.EnumerateArray().Select(MaskJsonElement));
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(maskedArray));

            default:
                return JsonSerializer.Deserialize<JsonElement>("\"*\"");
        }
    }
}
