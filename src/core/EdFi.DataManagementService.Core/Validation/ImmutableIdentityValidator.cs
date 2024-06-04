// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IImmutableIdentityValidator
{
    /// <summary>
    /// Validates a document for update has id matching the id in the request path
    /// </summary>
    /// <param name="context"></param>
    /// <param name="validatorContext"></param>
    /// <returns></returns>
    string[] Validate(PipelineContext context);
}

internal class ImmutableIdentityValidator() : IImmutableIdentityValidator
{
    public string[] Validate(PipelineContext context)
    {
        if (context.ParsedBody == No.JsonNode)
        {
            return (["A non-empty request body is required."]);
        }

        var documentId = context.ParsedBody["id"]?.GetValue<string>();

        if (documentId != null &&
            context.PathComponents.DocumentUuid == new DocumentUuid(new Guid(documentId)))
        {
            return ([]);
        }

        return (["Request body id must match the id in the url."]);
    }
}
