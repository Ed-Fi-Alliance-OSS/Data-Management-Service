// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// A delete request to a document repository
/// </summary>
/// <param name="DocumentUuid">The document UUID to delete</param>
/// <param name="ResourceInfo">The ResourceInfo for the resource being deleted</param>
/// <param name="validateNoReferencesToDocument">If true, validates there are no references to the document</param>
/// <param name="TraceId">The request TraceId</param>
internal record DeleteRequest(
    DocumentUuid DocumentUuid,
    ResourceInfo ResourceInfo,
    bool validateNoReferencesToDocument,
    TraceId TraceId
) : IDeleteRequest;
