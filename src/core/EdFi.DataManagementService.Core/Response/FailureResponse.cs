// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// Failure response with error lists
/// </summary>
internal static class FailureResponse
{
    private static readonly string _typePrefix = "urn:ed-fi:api";
    private static readonly string _badRequestTypePrefix = $"{_typePrefix}:bad-request";
    private static readonly string _dataConflictTypePrefix = $"{_typePrefix}:data-conflict";
    private static readonly string _keyChangeNotSupported = $"{_badRequestTypePrefix}:data-validation-failed:key-change-not-supported";

    public static FailureResponseWithErrors ForDataValidation(
        string detail,
        Dictionary<string, string[]>? validationErrors,
        string[]? errors
    ) =>
        new(
            detail,
            type: $"{_badRequestTypePrefix}:data-validation-failed",
            title: "Data Validation Failed",
            status: 400,
            correlationId: null,
            validationErrors,
            errors
        );

    public static FailureResponseWithErrors ForBadRequest(
        string detail,
        Dictionary<string, string[]>? ValidationErrors,
        string[]? Errors
    ) =>
        new(
            detail,
            type: _badRequestTypePrefix,
            title: "Bad Request",
            status: 400,
            correlationId: null,
            validationErrors: ValidationErrors,
            errors: Errors
        );

    public static FailureResponseWithErrors ForNotFound(string detail) =>
        new(
            detail,
            type: $"{_typePrefix}:not-found",
            title: "Not Found",
            status: 404,
            correlationId: null,
            validationErrors: [],
            errors: []
        );

    public static FailureResponseWithErrors ForIdentityConflict(string[]? errors) =>
        new(
            detail: "The identifying value(s) of the item are the same as another item that already exists.",
            type: $"{_typePrefix}:identity-conflict",
            title: "Identifying Values Are Not Unique",
            status: 409,
            correlationId: null,
            validationErrors: [],
            errors
        );

    public static FailureResponseWithErrors ForDataConflict(string[] dependentItemNames)
    {
        return new(
            detail: $"The requested action cannot be performed because this item is referenced by existing {string.Join(", ", dependentItemNames)} item(s).",
            type: $"{_dataConflictTypePrefix}:dependent-item-exists",
            title: "Dependent Item Exists",
            status: 409,
            correlationId: null,
            validationErrors: [],
            errors: []
        );
    }

    public static BaseFailureResponse ForInvalidReferences(ResourceName[] resourceNames)
    {
        string resources = string.Join(", ", resourceNames.Select(x => x.Value));
        return new(
            detail: $"The referenced {resources} item(s) do not exist.",
            type: $"{_dataConflictTypePrefix}:unresolved-reference",
            title: "Unresolved Reference",
            status: 409,
            correlationId: null
        );
    }

    public static FailureResponseWithErrors ForImmutableIdentity(string error) =>
        new(
            detail: error,
            type: _keyChangeNotSupported,
            title: "Key Change Not Supported",
            status: 400,
            correlationId: null,
            validationErrors: [],
            errors: []
        );
}
