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
    public static (string errorKey, string message) BuildValidationError(string arrayPath, int index)
    {
        if (!arrayPath.Contains("[*]"))
        {
            throw new InvalidOperationException(
                $"Array validation failure: Array path {arrayPath} in ApiSchema.json must contain '[*]' to identify the array."
            );
        }

        string errorKey = arrayPath.Substring(0, arrayPath.IndexOf("[*]", StringComparison.Ordinal));
        string[] parts = errorKey.Split('.');
        string shortArrayName = parts[^1];
        string message =
            $"The {GetOrdinal(index + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";
        return (errorKey, message);
    }

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

    public static Dictionary<string, string[]> BuildInvalidReferenceValidationErrors(
        IEnumerable<DocumentReferenceFailure> invalidDocumentReferences
    ) =>
        invalidDocumentReferences
            .GroupBy(failure => failure.Path.Value)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.Select(BuildInvalidReferenceMessage).Distinct().ToArray()
            );

    public static Dictionary<string, string[]> BuildInvalidDescriptorValidationErrors(
        IEnumerable<DescriptorReferenceFailure> invalidDescriptorReferences
    ) =>
        invalidDescriptorReferences
            .GroupBy(failure => failure.Path.Value)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.Select(BuildInvalidDescriptorMessage).Distinct().ToArray()
            );

    /// <summary>
    /// Converts an ordinal number to its string representation (1st, 2nd, 3rd, etc.)
    /// </summary>
    /// <param name="number">The number to convert</param>
    /// <returns>The ordinal string representation</returns>
    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
        {
            return $"{number}th";
        }

        return (number % 10) switch
        {
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        };
    }

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
            DescriptorReferenceFailureReason.DescriptorTypeMismatch =>
                $"{failure.TargetResource.ResourceName.Value} value '{descriptorIdentity.IdentityValue}' is not a valid {failure.TargetResource.ResourceName.Value}.",
            _ => throw new InvalidOperationException(
                $"Unsupported descriptor reference failure reason '{failure.Reason}'."
            ),
        };
    }
}
