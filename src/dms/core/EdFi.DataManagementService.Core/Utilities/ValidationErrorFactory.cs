// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Factory for creating consistent validation error messages and responses
/// </summary>
internal static class ValidationErrorFactory
{
    /// <summary>
    /// Builds a validation error tuple for array duplicate validation
    /// </summary>
    /// <param name="arrayPath">The JSONPath to the array containing duplicates</param>
    /// <param name="index">The index of the duplicate item</param>
    /// <returns>A tuple containing the error key and error message</returns>
    public static (string errorKey, string message) BuildValidationError(string arrayPath, int index) =>
        DuplicateItemValidationError.Build(arrayPath, index);

    /// <summary>
    /// Creates a standardized validation error response for the frontend
    /// </summary>
    /// <param name="validationErrors">Dictionary of validation errors</param>
    /// <param name="traceId">Request trace ID for logging correlation</param>
    /// <returns>A FrontendResponse with status 400 and validation error details</returns>
    public static FrontendResponse CreateValidationErrorResponse(
        Dictionary<string, string[]> validationErrors,
        TraceId traceId
    )
    {
        return new FrontendResponse(
            StatusCode: 400,
            Body: ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                traceId: traceId,
                validationErrors,
                []
            ),
            Headers: []
        );
    }

    public static Dictionary<string, string[]> BuildInvalidWriteReferenceValidationErrors(
        IEnumerable<DocumentReferenceFailure> invalidDocumentReferences,
        IEnumerable<DescriptorReferenceFailure> invalidDescriptorReferences
    ) =>
        BuildValidationErrors(
            invalidDocumentReferences
                .Select(failure => new KeyValuePair<string, string>(
                    failure.Path.Value,
                    BuildInvalidReferenceMessage(failure)
                ))
                .Concat(
                    invalidDescriptorReferences.Select(failure => new KeyValuePair<string, string>(
                        failure.Path.Value,
                        BuildInvalidDescriptorMessage(failure)
                    ))
                )
        );

    public static Dictionary<string, string[]> BuildWriteValidationErrors(
        IEnumerable<WriteValidationFailure> validationFailures
    ) =>
        BuildValidationErrors(
            validationFailures.Select(failure => new KeyValuePair<string, string>(
                failure.Path.Value,
                failure.Message
            ))
        );

    private static Dictionary<string, string[]> BuildValidationErrors(
        IEnumerable<KeyValuePair<string, string>> validationErrors
    ) =>
        validationErrors
            .GroupBy(entry => entry.Key)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.Select(entry => entry.Value).Distinct().ToArray()
            );

    private static string BuildInvalidReferenceMessage(DocumentReferenceFailure failure) =>
        failure.Reason switch
        {
            DocumentReferenceFailureReason.Missing =>
                $"The referenced {failure.TargetResource.ResourceName.Value} item does not exist.",
            DocumentReferenceFailureReason.IncompatibleTargetType =>
                $"The referenced {failure.TargetResource.ResourceName.Value} item is not compatible with this reference.",
            _ => throw new InvalidOperationException(
                $"Unsupported document reference failure reason '{failure.Reason}'."
            ),
        };

    private static string BuildInvalidDescriptorMessage(DescriptorReferenceFailure failure)
    {
        var descriptorIdentity =
            failure.DocumentIdentity.DocumentIdentityElements.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Descriptor failure at path '{failure.Path.Value}' is missing its descriptor identity value."
            );

        return failure.Reason switch
        {
            DescriptorReferenceFailureReason.Missing =>
                $"{failure.TargetResource.ResourceName.Value} value '{descriptorIdentity.IdentityValue}' does not exist.",
            _ => throw new InvalidOperationException(
                $"Unsupported descriptor reference failure reason '{failure.Reason}'."
            ),
        };
    }
}
