// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
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
        if (!_logger.IsEnabled(LogLevel.Debug) || requestInfo.ParsedBody == null)
        {
            await next();
            return;
        }

        _logger.LogDebug(
            "Entering RequestInfoBodyLoggingMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        string payload = _maskRequestBodyInLogs
            ? MaskRequestBody(requestInfo.ParsedBody, _logger)
            : requestInfo.ParsedBody.ToJsonString();

        _logger.LogDebug(
            MessageBody,
            requestInfo.Method,
            requestInfo.FrontendRequest.Path,
            payload,
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    private static string MaskRequestBody(JsonNode node, ILogger logger)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteMaskedNode(node, writer);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Error while masking request body.");
            return string.Empty;
        }
    }

    private static void WriteMaskedNode(JsonNode? node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj)
                {
                    writer.WritePropertyName(key);
                    WriteMaskedNode(value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteMaskedNode(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValue:
                writer.WriteStringValue("*");
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue("*");
                break;
        }
    }
}
