// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;

namespace EdFi.DataManagementService.Core.Batch;

internal static class BatchRequestParser
{
    public static async Task<IReadOnlyList<BatchOperation>> ParseAsync(RequestInfo requestInfo)
    {
        JsonNode root = await ReadRequestBodyAsync(requestInfo);

        if (root is not JsonArray operationsArray)
        {
            throw new BatchRequestException(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        "The batch request body must be a JSON array of operations.",
                        requestInfo.FrontendRequest.TraceId,
                        [],
                        []
                    ),
                    Headers: []
                )
            );
        }

        var operations = new List<BatchOperation>(operationsArray.Count);
        for (int index = 0; index < operationsArray.Count; index++)
        {
            JsonNode? element = operationsArray[index];
            if (element is not JsonObject operationNode)
            {
                throw new BatchRequestException(
                    CreateValidationError(requestInfo, $"Operation at index {index} must be a JSON object.")
                );
            }

            BatchOperation operation = ParseOperation(operationNode, index, requestInfo);
            operations.Add(operation);
        }

        return operations;
    }

    private static BatchOperation ParseOperation(JsonObject operationNode, int index, RequestInfo requestInfo)
    {
        string opValue = ReadRequiredString(
            operationNode,
            propertyName: "op",
            index,
            requestInfo,
            "must specify 'op' as a string value."
        );

        if (!BatchOperationTypeExtensions.TryParse(opValue, out var operationType))
        {
            throw new BatchRequestException(
                CreateValidationError(
                    requestInfo,
                    $"Operation at index {index} has invalid 'op' value '{opValue}'."
                )
            );
        }

        string resourceEndpoint = ReadRequiredString(
            operationNode,
            propertyName: "resource",
            index,
            requestInfo,
            "must specify a non-empty 'resource'."
        );
        EndpointName endpointName = new(resourceEndpoint);

        JsonObject? document = operationNode["document"] as JsonObject;
        JsonObject? naturalKey = operationNode["naturalKey"] as JsonObject;
        DocumentUuid? documentUuid = TryParseDocumentUuid(operationNode["documentId"], requestInfo, index);
        string? ifMatch = null;
        if (operationNode.TryGetPropertyValue("ifMatch", out JsonNode? ifMatchNode))
        {
            if (ifMatchNode is JsonValue ifMatchValue && ifMatchValue.TryGetValue(out string? headerValue))
            {
                ifMatch = headerValue;
            }
            else if (ifMatchNode != null)
            {
                throw new BatchRequestException(
                    CreateValidationError(
                        requestInfo,
                        $"Operation at index {index} has an invalid 'ifMatch' value. Expected a string."
                    )
                );
            }
        }

        switch (operationType)
        {
            case BatchOperationType.Create:
                if (document is null)
                {
                    throw new BatchRequestException(
                        CreateValidationError(
                            requestInfo,
                            $"Create operation at index {index} requires a 'document' object."
                        )
                    );
                }

                if (documentUuid.HasValue || naturalKey is not null)
                {
                    throw new BatchRequestException(
                        CreateValidationError(
                            requestInfo,
                            $"Create operation at index {index} must not include 'documentId' or 'naturalKey'."
                        )
                    );
                }
                break;

            case BatchOperationType.Update:
            case BatchOperationType.Delete:
                if (operationType == BatchOperationType.Update && document is null)
                {
                    throw new BatchRequestException(
                        CreateValidationError(
                            requestInfo,
                            $"Update operation at index {index} requires a 'document' object."
                        )
                    );
                }

                bool hasDocumentId = documentUuid.HasValue;
                bool hasNaturalKey = naturalKey is not null;
                if (hasDocumentId == hasNaturalKey)
                {
                    throw new BatchRequestException(
                        CreateValidationError(
                            requestInfo,
                            $"Operation at index {index} must specify exactly one of 'documentId' or 'naturalKey'."
                        )
                    );
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(ifMatch) && operationType == BatchOperationType.Create)
        {
            throw new BatchRequestException(
                CreateValidationError(
                    requestInfo,
                    $"Operation at index {index} must not provide 'ifMatch' for create actions."
                )
            );
        }

        return new BatchOperation(
            Index: index,
            OperationType: operationType,
            Endpoint: endpointName,
            Document: document,
            NaturalKey: naturalKey,
            DocumentId: documentUuid,
            IfMatch: string.IsNullOrWhiteSpace(ifMatch) ? null : ifMatch
        );
    }

    private static DocumentUuid? TryParseDocumentUuid(JsonNode? node, RequestInfo requestInfo, int index)
    {
        if (node == null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            if (Guid.TryParse(text, out Guid parsed))
            {
                return new DocumentUuid(parsed);
            }

            throw new BatchRequestException(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        $"Operation at index {index} has invalid 'documentId' value '{text}'.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        validationErrors: [],
                        errors: []
                    ),
                    Headers: []
                )
            );
        }

        throw new BatchRequestException(
            new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForBadRequest(
                    $"Operation at index {index} has an invalid 'documentId' value.",
                    traceId: requestInfo.FrontendRequest.TraceId,
                    validationErrors: [],
                    errors: []
                ),
                Headers: []
            )
        );
    }

    private static string ReadRequiredString(
        JsonObject node,
        string propertyName,
        int index,
        RequestInfo requestInfo,
        string failureDetail
    )
    {
        if (
            node.TryGetPropertyValue(propertyName, out JsonNode? jsonNode)
            && jsonNode is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? text)
            && !string.IsNullOrWhiteSpace(text)
        )
        {
            return text!;
        }

        throw new BatchRequestException(
            CreateValidationError(requestInfo, $"Operation at index {index} {failureDetail}")
        );
    }

    private static async Task<JsonNode> ReadRequestBodyAsync(RequestInfo requestInfo)
    {
        if (requestInfo.FrontendRequest.BodyStream != null)
        {
            using var reader = new StreamReader(
                requestInfo.FrontendRequest.BodyStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: false
            );
            string content = await reader.ReadToEndAsync();
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Body = null, BodyStream = null };

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new BatchRequestException(
                    CreateValidationError(requestInfo, "The batch request body cannot be empty.")
                );
            }

            return ParseJson(content, requestInfo);
        }

        if (!string.IsNullOrWhiteSpace(requestInfo.FrontendRequest.Body))
        {
            string body = requestInfo.FrontendRequest.Body!;
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with { Body = null, BodyStream = null };
            return ParseJson(body, requestInfo);
        }

        throw new BatchRequestException(
            CreateValidationError(requestInfo, "The batch request body cannot be empty.")
        );
    }

    private static JsonNode ParseJson(string payload, RequestInfo requestInfo)
    {
        try
        {
            return JsonNode.Parse(payload)
                ?? throw new BatchRequestException(
                    CreateValidationError(requestInfo, "Unable to parse batch request body.")
                );
        }
        catch (JsonException)
        {
            throw new BatchRequestException(
                CreateValidationError(requestInfo, "Unable to parse batch request body.")
            );
        }
    }

    private static FrontendResponse CreateValidationError(RequestInfo requestInfo, string detail)
    {
        return new FrontendResponse(
            StatusCode: 400,
            Body: FailureResponse.ForBadRequest(detail, requestInfo.FrontendRequest.TraceId, [], []),
            Headers: []
        );
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "S3871:Exception types should be public",
    Justification = "Exception exposes internal response types and is only used within Core batch parsing."
)]
internal sealed class BatchRequestException(FrontendResponse response) : Exception(response.Body?.ToString())
{
    public FrontendResponse Response { get; } = response;
}
