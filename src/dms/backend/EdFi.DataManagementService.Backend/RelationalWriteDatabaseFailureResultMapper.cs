// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using JsonValue = System.Text.Json.Nodes.JsonValue;

namespace EdFi.DataManagementService.Backend;

internal sealed class RelationalWriteDatabaseFailureResultMapper(
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalWriteConstraintResolver writeConstraintResolver
)
{
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));

    private readonly IRelationalWriteConstraintResolver _writeConstraintResolver =
        writeConstraintResolver ?? throw new ArgumentNullException(nameof(writeConstraintResolver));

    public bool TryBuild(
        RelationalWriteExecutorRequest request,
        DbException exception,
        out RelationalWriteExecutorResult? result
    )
    {
        result = null;

        if (!_writeExceptionClassifier.TryClassify(exception, out var classification))
        {
            return false;
        }

        result = classification switch
        {
            RelationalWriteExceptionClassification.ConstraintViolation violation =>
                BuildConstraintViolationFailureResult(request, violation),
            RelationalWriteExceptionClassification.UnrecognizedWriteFailure =>
                RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    request.OperationKind,
                    BuildUnrecognizedDatabaseWriteFailureMessage(request.WritePlan.Model.Resource)
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write exception classification '{classification.GetType().Name}'."
            ),
        };

        return true;
    }

    private RelationalWriteExecutorResult BuildConstraintViolationFailureResult(
        RelationalWriteExecutorRequest request,
        RelationalWriteExceptionClassification.ConstraintViolation violation
    )
    {
        var resolution = _writeConstraintResolver.Resolve(
            new RelationalWriteConstraintResolutionRequest(
                request.WritePlan,
                request.ReferenceResolutionRequest,
                violation
            )
        );

        return resolution switch
        {
            // Re-run the whole POST after a guarded create race so the winning representation is
            // resolved and subjected to stored-value authorization before If-None-Match is evaluated.
            RelationalWriteConstraintResolution.RootNaturalKeyUnique when IsIfNoneMatchCreate(request) =>
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict()),
            RelationalWriteConstraintResolution.RootNaturalKeyUnique
            or RelationalWriteConstraintResolution.AbstractIdentityNaturalKeyUnique =>
                BuildIdentityConflictFailureResult(request),
            RelationalWriteConstraintResolution.RequestReference requestReference
                when TryBuildRequestReferenceFailureResult(
                    request.OperationKind,
                    request.ReferenceResolutionRequest,
                    requestReference,
                    out var referenceFailureResult
                ) => referenceFailureResult!,
            RelationalWriteConstraintResolution.RequestReference
            or RelationalWriteConstraintResolution.Unresolved =>
                RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    request.OperationKind,
                    BuildUnexpectedConstraintFailureMessage(request.WritePlan.Model.Resource)
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write constraint resolution '{resolution.GetType().Name}'."
            ),
        };
    }

    private static bool IsIfNoneMatchCreate(RelationalWriteExecutorRequest request) =>
        request.OperationKind == RelationalWriteOperationKind.Post
        && request.TargetContext is RelationalWriteTargetContext.CreateNew
        && request.WritePrecondition is WritePrecondition.IfNoneMatch;

    private static RelationalWriteExecutorResult BuildIdentityConflictFailureResult(
        RelationalWriteExecutorRequest request
    )
    {
        var duplicateIdentityValues = BuildDuplicateIdentityValues(request);
        var resourceName = new ResourceName(request.WritePlan.Model.Resource.ResourceName);

        return request.OperationKind switch
        {
            RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                new UpsertResult.UpsertFailureIdentityConflict(resourceName, duplicateIdentityValues)
            ),
            RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                new UpdateResult.UpdateFailureIdentityConflict(resourceName, duplicateIdentityValues)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
        };
    }

    private static bool TryBuildRequestReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        result = resolution.ReferenceKind switch
        {
            RelationalWriteReferenceKind.Document => TryBuildDocumentReferenceFailureResult(
                operationKind,
                request,
                resolution,
                out var documentReferenceResult
            )
                ? documentReferenceResult
                : null,
            RelationalWriteReferenceKind.Descriptor => TryBuildDescriptorReferenceFailureResult(
                operationKind,
                request,
                resolution,
                out var descriptorReferenceResult
            )
                ? descriptorReferenceResult
                : null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution.ReferenceKind,
                "Unsupported relational write reference kind."
            ),
        };

        return result is not null;
    }

    private static bool TryBuildDocumentReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        var invalidDocumentReferences = request
            .DocumentReferences.Where(reference =>
                MatchesRequestReference(reference.Path, resolution.ReferencePath)
                && RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo)
                    == resolution.TargetResource
            )
            .Select(static reference =>
                DocumentReferenceFailure.From(reference, DocumentReferenceFailureReason.Missing)
            )
            .ToArray();

        result =
            invalidDocumentReferences.Length == 0
                ? null
                : operationKind switch
                {
                    RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference(invalidDocumentReferences, [])
                    ),
                    RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference(invalidDocumentReferences, [])
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
                };

        return result is not null;
    }

    private static bool TryBuildDescriptorReferenceFailureResult(
        RelationalWriteOperationKind operationKind,
        ReferenceResolverRequest request,
        RelationalWriteConstraintResolution.RequestReference resolution,
        out RelationalWriteExecutorResult? result
    )
    {
        var invalidDescriptorReferences = request
            .DescriptorReferences.Where(reference =>
                MatchesRequestReference(reference.Path, resolution.ReferencePath)
                && RelationalWriteSupport.ToQualifiedResourceName(reference.ResourceInfo)
                    == resolution.TargetResource
            )
            .Select(DescriptorReferenceFailureClassifier.Missing)
            .ToArray();

        result =
            invalidDescriptorReferences.Length == 0
                ? null
                : operationKind switch
                {
                    RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UpsertFailureReference([], invalidDescriptorReferences)
                    ),
                    RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureReference([], invalidDescriptorReferences)
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
                };

        return result is not null;
    }

    private static KeyValuePair<string, string>[] BuildDuplicateIdentityValues(
        RelationalWriteExecutorRequest request
    )
    {
        var referentialIdentityParameters = GetReferentialIdentityParametersOrThrow(request);
        return referentialIdentityParameters
            .IdentityElements.Select(identityElement =>
                TryResolveIdentityValue(
                    request.SelectedBody,
                    identityElement.IdentityJsonPath,
                    out var identityValue
                )
                    ? new KeyValuePair<string, string>?(
                        new KeyValuePair<string, string>(
                            GetIdentityElementName(identityElement.IdentityJsonPath),
                            identityValue
                        )
                    )
                    : null
            )
            .OfType<KeyValuePair<string, string>>()
            .ToArray();
    }

    private static TriggerKindParameters.ReferentialIdentityMaintenance GetReferentialIdentityParametersOrThrow(
        RelationalWriteExecutorRequest request
    ) =>
        RelationalWriteSupport.GetReferentialIdentityParametersOrThrow(
            request.MappingSet,
            request.WritePlan.Model.Resource,
            request.WritePlan.Model.Root.Table
        );

    private static bool TryResolveIdentityValue(
        JsonNode selectedBody,
        string identityJsonPath,
        out string identityValue
    )
    {
        var segments = RelationalJsonPathSupport.GetRestrictedSegments(
            new JsonPathExpression(identityJsonPath, [])
        );
        JsonNode? currentNode = selectedBody;

        foreach (var segment in segments)
        {
            if (segment is not JsonPathSegment.Property property)
            {
                identityValue = string.Empty;
                return false;
            }

            if (currentNode is not JsonObject jsonObject)
            {
                identityValue = string.Empty;
                return false;
            }

            if (!jsonObject.TryGetPropertyValue(property.Name, out currentNode) || currentNode is null)
            {
                identityValue = string.Empty;
                return false;
            }
        }

        if (currentNode is not JsonValue jsonValue)
        {
            identityValue = string.Empty;
            return false;
        }

        identityValue = jsonValue.TryGetValue<string>(out var stringValue)
            ? stringValue
            : currentNode.ToJsonString();

        return true;
    }

    private static string GetIdentityElementName(string identityJsonPath)
    {
        var segments = RelationalJsonPathSupport.GetRestrictedSegments(
            new JsonPathExpression(identityJsonPath, [])
        );

        return segments.Count > 0 && segments[^1] is JsonPathSegment.Property property
            ? property.Name
            : identityJsonPath;
    }

    private static bool MatchesRequestReference(JsonPath concretePath, JsonPathExpression referencePath)
    {
        return string.Equals(
            RelationalJsonPathSupport.ParseConcretePath(concretePath).WildcardPath,
            referencePath.Canonical,
            StringComparison.Ordinal
        );
    }

    private static string BuildUnexpectedConstraintFailureMessage(QualifiedResourceName resource) =>
        $"Relational write failed for resource '{RelationalWriteSupport.FormatResource(resource)}' because the database reported a non-user-facing constraint violation.";

    private static string BuildUnrecognizedDatabaseWriteFailureMessage(QualifiedResourceName resource) =>
        $"Relational write failed for resource '{RelationalWriteSupport.FormatResource(resource)}' because the database reported an unrecognized final write failure.";
}
