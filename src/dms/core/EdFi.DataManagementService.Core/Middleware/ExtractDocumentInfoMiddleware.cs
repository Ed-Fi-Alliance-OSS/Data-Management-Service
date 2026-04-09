// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Core.Extraction.ReferentialIdCalculator;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Extracts identity and reference information from a valid JSON document
/// </summary>
internal class ExtractDocumentInfoMiddleware(IOptions<Configuration.AppSettings> appSettings, ILogger _logger)
    : IPipelineStep
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

        Trace.Assert(requestInfo.ParsedBody != null, "Body was null, pipeline config invalid", "");

        var (documentIdentity, superclassIdentity) = requestInfo.ResourceSchema.ExtractIdentities(
            requestInfo.ParsedBody,
            _logger
        );

        DocumentReference[] documentReferences;
        DocumentReferenceArray[] documentReferenceArrays;
        var extractionMode = appSettings.Value.UseRelationalBackend
            ? ReferenceExtractionMode.RelationalWriteValidation
            : ReferenceExtractionMode.LegacyCompatibility;

        if (extractionMode == ReferenceExtractionMode.RelationalWriteValidation)
        {
            try
            {
                (documentReferences, documentReferenceArrays) = requestInfo.ResourceSchema.ExtractReferences(
                    requestInfo.ParsedBody,
                    _logger,
                    extractionMode
                );
            }
            catch (ReferenceExtractionValidationException ex)
            {
                requestInfo.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                    ValidationErrorFactory.BuildWriteValidationErrors(ex.ValidationFailures),
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            }
        }
        else
        {
            (documentReferences, documentReferenceArrays) = requestInfo.ResourceSchema.ExtractReferences(
                requestInfo.ParsedBody,
                _logger,
                extractionMode
            );
        }

        var descriptorReferences = appSettings.Value.UseRelationalBackend
            ? requestInfo.ResourceSchema.ExtractRelationalDescriptors(
                requestInfo.ResourceInfo,
                requestInfo.MappingSet
                    ?? throw new InvalidOperationException(
                        "MappingSet must be resolved before ExtractDocumentInfoMiddleware when UseRelationalBackend is enabled."
                    ),
                requestInfo.ParsedBody,
                _logger
            )
            : requestInfo.ResourceSchema.ExtractDescriptors(requestInfo.ParsedBody, _logger);

        requestInfo.DocumentInfo = new(
            DocumentReferences: documentReferences,
            DocumentReferenceArrays: documentReferenceArrays,
            DescriptorReferences: descriptorReferences,
            DocumentIdentity: documentIdentity,
            ReferentialId: ReferentialIdFrom(requestInfo.ResourceInfo, documentIdentity),
            SuperclassIdentity: superclassIdentity
        );

        await next();
    }
}
