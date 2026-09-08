// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Maps profile write pipeline failures to client-visible <see cref="FrontendResponse"/>
/// problem responses.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     Submitted collection value-filter violations and duplicate visible collection-item
///     collisions are request data validation failures and map to
///     <c>urn:ed-fi:api:bad-request:data-validation-failed</c> (HTTP 400).
///   </item>
///   <item>
///     Internal categories (Core/backend contract mismatch, invalid profile definition,
///     binding-accounting failure) map to <c>urn:ed-fi:api:system</c> (HTTP 500).
///   </item>
///   <item>
///     Every other profile-policy failure (creatability violations, generic writable-profile
///     validation, invalid profile usage) maps to <c>urn:ed-fi:api:data-policy-enforced</c>
///     (HTTP 400).
///   </item>
/// </list>
/// </remarks>
internal static class ProfileWriteFailureResponseMapper
{
    public static FrontendResponse Map(
        IReadOnlyList<ProfileFailure> failures,
        string profileName,
        TraceId traceId
    )
    {
        // Internal categories surface as server errors and take precedence.
        if (failures.Any(IsServerError))
        {
            return new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(traceId),
                Headers: []
            );
        }

        // Submitted-data validation failures (collection value-filter violations and
        // duplicate visible collection-item collisions) map to data-validation-failed.
        List<ProfileFailure> dataValidationFailures = [.. failures.Where(IsDataValidation)];
        if (dataValidationFailures.Count > 0)
        {
            return new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForDataValidation(
                    "The request data failed validation against the assigned profile. See 'validationErrors' for details.",
                    traceId,
                    BuildValidationErrors(dataValidationFailures),
                    [.. dataValidationFailures.Select(failure => failure.Message)]
                ),
                Headers: []
            );
        }

        // Creatability and any other profile-policy failures map to data-policy-enforced.
        return new FrontendResponse(
            StatusCode: 400,
            Body: FailureResponse.ForDataPolicyEnforced(profileName, traceId),
            Headers: []
        );
    }

    private static bool IsServerError(ProfileFailure failure) =>
        failure.Category
            is ProfileFailureCategory.CoreBackendContractMismatch
                or ProfileFailureCategory.InvalidProfileDefinition
                or ProfileFailureCategory.BindingAccountingFailure;

    private static bool IsDataValidation(ProfileFailure failure) =>
        failure
            is CollectionValueFilterViolationWritableProfileValidationFailure
                or DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure;

    private static Dictionary<string, string[]> BuildValidationErrors(IReadOnlyList<ProfileFailure> failures)
    {
        // Accumulate per path so multiple failures targeting the same request path
        // (e.g. several filters failing on one collection item) each contribute a
        // message instead of the last one clobbering the rest. The response contract
        // is string[] per path; distinct messages are preserved in encounter order.
        Dictionary<string, List<string>> messagesByPath = [];

        foreach (ProfileFailure failure in failures)
        {
            ImmutableArray<string> requestPaths = failure switch
            {
                CollectionValueFilterViolationWritableProfileValidationFailure valueFilter =>
                    valueFilter.RequestJsonPaths,
                DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure duplicate =>
                    duplicate.RequestJsonPaths,
                _ => [],
            };

            foreach (string requestPath in requestPaths)
            {
                if (!messagesByPath.TryGetValue(requestPath, out List<string>? messages))
                {
                    messages = [];
                    messagesByPath[requestPath] = messages;
                }

                if (!messages.Contains(failure.Message))
                {
                    messages.Add(failure.Message);
                }
            }
        }

        return messagesByPath.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
    }
}
