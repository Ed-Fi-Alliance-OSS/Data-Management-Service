// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IMatchingDocumentUuidsValidator
{
    /// <summary>
    /// Validates a document for update has id matching the id in the request path
    /// </summary>
    /// <param name="context"></param>
    /// <returns>A boolean indicating whether the validator passed or not</returns>
    bool Validate(PipelineContext context);
}

internal class MatchingDocumentUuidsValidator() : IMatchingDocumentUuidsValidator
{
    public bool Validate(PipelineContext context)
    {
        string? documentId = context.ParsedBody["id"]?.GetValue<string>();

        return documentId != null
            && Guid.TryParse(documentId, out var id)
            && context.PathComponents.DocumentUuid.Value == id;
    }
}
