// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// Projects an <see cref="IdentityResultStatus.InvalidProperties" /> provider result into the same 400
/// problem-detail shape core schema validation produces (design.md:896-927,
/// <see cref="Middleware.ValidateDocumentMiddleware" />): a blank or null <see cref="IdentityError.Path" />
/// routes its <see cref="IdentityError.Message" /> to the document-level <c>errors</c> collection, and a
/// non-blank <see cref="IdentityError.Path" /> is used verbatim - never parsed, split, or renumbered -
/// as a <c>validationErrors</c> key, with messages for the same path grouped together in the order the
/// provider returned them.
/// </summary>
internal static class IdentityErrorProjection
{
    public static JsonNode Project(IReadOnlyList<IdentityError> errors, TraceId traceId)
    {
        List<string> documentErrors = [];
        Dictionary<string, List<string>> messagesByPath = [];
        List<string> pathsInProviderOrder = [];

        foreach (IdentityError error in errors)
        {
            if (string.IsNullOrWhiteSpace(error.Path))
            {
                documentErrors.Add(error.Message);
                continue;
            }

            if (!messagesByPath.TryGetValue(error.Path, out List<string>? messagesForPath))
            {
                messagesForPath = [];
                messagesByPath[error.Path] = messagesForPath;
                pathsInProviderOrder.Add(error.Path);
            }

            messagesForPath.Add(error.Message);
        }

        Dictionary<string, string[]> validationErrors = [];
        foreach (string path in pathsInProviderOrder)
        {
            validationErrors[path] = [.. messagesByPath[path]];
        }

        string[] documentErrorsArray = [.. documentErrors];

        return documentErrorsArray.Length > 0
            ? FailureResponse.ForBadRequest(
                FailureResponse.ErrorsArmDetail,
                traceId,
                validationErrors,
                documentErrorsArray
            )
            : FailureResponse.ForDataValidation(
                FailureResponse.ValidationErrorsArmDetail,
                traceId,
                validationErrors,
                documentErrorsArray
            );
    }
}
