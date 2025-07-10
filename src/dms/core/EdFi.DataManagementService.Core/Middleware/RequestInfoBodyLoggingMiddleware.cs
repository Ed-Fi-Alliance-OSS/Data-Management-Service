// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static string MaskRequestBody(string requestBody, ILogger logger)
    {
        try
        {
            var parsedJson = JsonNode.Parse(requestBody);
            MaskValues(parsedJson);
            return JsonSerializer.Serialize(parsedJson, new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Error parsing JSON request body");
            return requestBody;
        }

        void MaskValues(JsonNode? node)
        {
            if (node is JsonObject jsonObject)
            {
                foreach (var property in jsonObject.ToList())
                {
                    if (property.Value is JsonValue)
                    {
                        jsonObject[property.Key] = "*";
                    }
                    else
                    {
                        MaskValues(property.Value);
                    }
                }
            }
            else if (node is JsonArray jsonArray)
            {
                foreach (var item in jsonArray)
                {
                    MaskValues(item);
                }
            }
        }
    }
}
