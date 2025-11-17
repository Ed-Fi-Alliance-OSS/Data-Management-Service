// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Batch;

internal static class BatchResponseBuilder
{
    private const string NotImplementedDetail = "Batch operations not supported for configured backend.";

    public static FrontendResponse CreateTooLargeResponse(RequestInfo requestInfo, int count, int maximum)
    {
        return new FrontendResponse(
            StatusCode: 413,
            Body: new JsonObject
            {
                ["error"] = "Batch size limit exceeded.",
                ["message"] =
                    $"The number of operations ({count}) exceeds the maximum allowed ({maximum}). Please split the request into smaller batches.",
            },
            Headers: []
        );
    }

    public static FrontendResponse CreateBackendNotImplementedResponse(RequestInfo requestInfo)
    {
        return new FrontendResponse(
            StatusCode: 501,
            Body: new JsonObject
            {
                ["error"] = NotImplementedDetail,
                ["message"] = "The current document store does not support transactional batch operations.",
            },
            Headers: []
        );
    }

    public static FrontendResponse CreateSuccessResponse(
        RequestInfo requestInfo,
        IReadOnlyList<BatchOperationSuccess> successes
    )
    {
        JsonArray body = new();
        foreach (BatchOperationSuccess success in successes)
        {
            body.Add(
                new JsonObject
                {
                    ["index"] = success.Index,
                    ["status"] = "success",
                    ["op"] = success.OperationType.ToOperationString(),
                    ["resource"] = success.Endpoint.Value,
                    ["documentId"] = success.DocumentUuid.Value.ToString(),
                }
            );
        }

        return new FrontendResponse(StatusCode: 200, Body: body, Headers: []);
    }

    public static FrontendResponse CreateFailureResponse(
        RequestInfo requestInfo,
        BatchOperationFailure failure
    )
    {
        int statusCode = failure.ErrorResponse.StatusCode;
        JsonObject response = new()
        {
            ["detail"] = "Batch operation failed and was rolled back.",
            ["type"] = "urn:ed-fi:api:batch-operation-failed",
            ["title"] = "Batch Operation Failed",
            ["status"] = statusCode,
            ["correlationId"] = requestInfo.FrontendRequest.TraceId.Value,
            ["validationErrors"] = new JsonObject(),
            ["errors"] = new JsonArray(),
            ["failedOperation"] = BuildFailedOperationNode(failure),
        };

        return new FrontendResponse(StatusCode: statusCode, Body: response, Headers: []);
    }

    private static JsonObject BuildFailedOperationNode(BatchOperationFailure failure)
    {
        return new JsonObject
        {
            ["index"] = failure.Operation.Index,
            ["op"] = failure.Operation.OperationType.ToOperationString(),
            ["resource"] = failure.Operation.Endpoint.Value,
            ["problem"] = CloneOrCreateProblem(failure.ErrorResponse),
        };
    }

    private static JsonNode CloneOrCreateProblem(FrontendResponse response)
    {
        if (response.Body is JsonObject obj)
        {
            return obj.DeepClone();
        }

        return new JsonObject
        {
            ["detail"] = "The operation failed without additional error information.",
            ["status"] = response.StatusCode,
        };
    }
}
