// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Extracts identity and reference information from a valid JSON document
/// </summary>
internal class ExtractDocumentInfoMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    /// Builds a DocumentInfo using the various extractors on a document body
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ExtractDocumentInfoMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        Trace.Assert(requestInfo.ParsedBody != null, "Body was null, pipeline config invalid");

        var (documentIdentity, superclassIdentity) = requestInfo.ResourceSchema.ExtractIdentities(
            requestInfo.ParsedBody,
            _logger
        );

        (DocumentReference[] documentReferences, DocumentReferenceArray[] documentReferenceArrays) =
            requestInfo.ResourceSchema.ExtractReferences(requestInfo.ParsedBody, _logger);

        requestInfo.DocumentInfo = new(
            DocumentReferences: documentReferences,
            DocumentReferenceArrays: documentReferenceArrays,
            DescriptorReferences: requestInfo.ResourceSchema.ExtractDescriptors(
                requestInfo.ParsedBody,
                _logger
            ),
            DocumentIdentity: documentIdentity,
            ReferentialId: ReferentialIdFrom(requestInfo.ResourceInfo, documentIdentity),
            SuperclassIdentity: superclassIdentity
        );

        await next();
    }
}
