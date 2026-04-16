// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Pipeline result carrying the outcome of profile write processing.
/// </summary>
public sealed record ProfileWritePipelineResult
{
    /// <summary>
    /// Whether the request is governed by a writable profile.
    /// False when the resolved write content type is null (no profile applies).
    /// </summary>
    public bool HasProfile { get; init; }

    /// <summary>
    /// The request-side contract when processing succeeds.
    /// Null when <see cref="HasProfile"/> is false or <see cref="Failures"/> is non-empty.
    /// </summary>
    public ProfileAppliedWriteRequest? Request { get; init; }

    /// <summary>
    /// The full stored-side context for update/upsert flows.
    /// Null for create flows or when no stored document is present.
    /// </summary>
    public ProfileAppliedWriteContext? Context { get; init; }

    /// <summary>
    /// Typed profile failures across all categories encountered during processing.
    /// Empty on success.
    /// </summary>
    public ImmutableArray<ProfileFailure> Failures { get; init; }

    /// <summary>
    /// True when a profile applies and no failures were emitted.
    /// </summary>
    public bool IsSuccess => HasProfile && Failures.IsEmpty;

    /// <summary>
    /// Short-circuit result when no writable profile governs the request.
    /// </summary>
    public static ProfileWritePipelineResult NoProfile() => new() { HasProfile = false, Failures = [] };

    /// <summary>
    /// Successful pipeline result with the request contract and optional stored context.
    /// </summary>
    public static ProfileWritePipelineResult Success(
        ProfileAppliedWriteRequest request,
        ProfileAppliedWriteContext? context = null
    ) =>
        new()
        {
            HasProfile = true,
            Request = request,
            Context = context,
            Failures = [],
        };

    /// <summary>
    /// Failed pipeline result with one or more typed failures.
    /// </summary>
    public static ProfileWritePipelineResult Failure(params ProfileFailure[] failures) =>
        new() { HasProfile = true, Failures = [.. failures] };

    /// <summary>
    /// Failed pipeline result with a collection of typed failures.
    /// </summary>
    public static ProfileWritePipelineResult Failure(IEnumerable<ProfileFailure> failures) =>
        new() { HasProfile = true, Failures = [.. failures] };
}

/// <summary>
/// Pre-resolution result carrying request-side shaping that is safe to compute
/// before POST/PUT target resolution.
/// </summary>
internal sealed record ProfileWritePreResolutionResult
{
    public bool HasProfile { get; init; }

    public ProfilePreResolvedWriteRequest? Request { get; init; }

    public ImmutableArray<ProfileFailure> Failures { get; init; }

    public bool IsSuccess => HasProfile && Failures.IsEmpty;

    public static ProfileWritePreResolutionResult NoProfile() => new() { HasProfile = false, Failures = [] };

    public static ProfileWritePreResolutionResult Success(ProfilePreResolvedWriteRequest request) =>
        new()
        {
            HasProfile = true,
            Request = request,
            Failures = [],
        };

    public static ProfileWritePreResolutionResult Failure(params ProfileFailure[] failures) =>
        new() { HasProfile = true, Failures = [.. failures] };

    public static ProfileWritePreResolutionResult Failure(IEnumerable<ProfileFailure> failures) =>
        new() { HasProfile = true, Failures = [.. failures] };
}

/// <summary>
/// Orchestrates profile write processing across pre-resolution and resolved-target phases.
/// </summary>
internal static class ProfileWritePipeline
{
    /// <summary>
    /// Executes the request-side phase that is safe before POST/PUT target resolution.
    /// Covers profile mode validation, semantic identity checks, and writable request shaping.
    /// </summary>
    public static ProfileWritePreResolutionResult ExecutePreResolution(
        JsonNode canonicalizedRequestBody,
        ContentTypeDefinition? writeContentType,
        ProfileContentType? resolvedContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        if (resolvedContentType != null && resolvedContentType != ProfileContentType.Write)
        {
            return ProfileWritePreResolutionResult.Failure(
                ProfileFailures.ProfileModeMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    expectedUsage: "Write",
                    actualUsage: resolvedContentType.ToString() ?? "None"
                )
            );
        }

        if (writeContentType is null)
        {
            return ProfileWritePreResolutionResult.NoProfile();
        }

        ProfileDefinition syntheticDefinition = new(
            profileName,
            [new ResourceProfile(resourceName, LogicalSchema: null, ReadContentType: null, writeContentType)]
        );

        IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> identityFailures =
            SemanticIdentityCompatibilityValidator.Validate(syntheticDefinition, resourceName, scopeCatalog);

        if (identityFailures.Count > 0)
        {
            return ProfileWritePreResolutionResult.Failure(identityFailures);
        }

        var classifier = new ProfileVisibilityClassifier(writeContentType, scopeCatalog);
        var addressEngine = new AddressDerivationEngine(scopeCatalog);
        var shaper = new WritableRequestShaper(
            classifier,
            addressEngine,
            profileName,
            resourceName,
            method,
            operation
        );

        WritableRequestShapingResult shapingResult = shaper.Shape(canonicalizedRequestBody);

        if (!shapingResult.ValidationFailures.IsEmpty)
        {
            return ProfileWritePreResolutionResult.Failure(
                shapingResult.ValidationFailures.CastArray<ProfileFailure>()
            );
        }

        return ProfileWritePreResolutionResult.Success(
            new ProfilePreResolvedWriteRequest(
                shapingResult.WritableRequestBody,
                shapingResult.RequestScopeStates,
                shapingResult.VisibleRequestCollectionItems
            )
        );
    }

    /// <summary>
    /// Executes the target-aware phase after POST/PUT target resolution is known.
    /// </summary>
    public static ProfileWritePipelineResult ExecuteResolvedTarget(
        ProfilePreResolvedWriteRequest preResolvedRequest,
        ContentTypeDefinition writeContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        JsonNode? storedDocument,
        bool isCreate,
        string profileName,
        string resourceName,
        string method,
        string operation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    )
    {
        var classifier = new ProfileVisibilityClassifier(writeContentType, scopeCatalog);
        var addressEngine = new AddressDerivationEngine(scopeCatalog);

        // ------------------------------------------------------------------
        // Step 5: Build stored-side existence lookup
        // ------------------------------------------------------------------
        StoredSideExistenceLookupResult existenceLookupResult = StoredSideExistenceLookupBuilder.Build(
            storedDocument,
            scopeCatalog,
            classifier,
            addressEngine
        );

        // ------------------------------------------------------------------
        // Step 6: C4 — Creatability analysis and duplicate detection
        // ------------------------------------------------------------------
        var creatabilityAnalyzer = new CreatabilityAnalyzer(
            scopeCatalog,
            classifier,
            profileName,
            resourceName,
            method,
            operation
        );

        CreatabilityResult creatabilityResult = creatabilityAnalyzer.Analyze(
            preResolvedRequest.RequestScopeStates,
            preResolvedRequest.VisibleRequestCollectionItems,
            existenceLookupResult.Lookup,
            isCreate,
            effectiveSchemaRequiredMembersByScope
        );

        ImmutableArray<WritableProfileValidationFailure> duplicateFailures =
            DuplicateCollectionItemDetector.Detect(
                creatabilityResult.EnrichedCollectionItems,
                profileName,
                resourceName,
                method,
                operation
            );

        // Bail out if creatability or duplicate failures accumulated
        if (!creatabilityResult.Failures.IsEmpty || !duplicateFailures.IsEmpty)
        {
            ImmutableArray<ProfileFailure>.Builder failureBuilder =
                ImmutableArray.CreateBuilder<ProfileFailure>();
            failureBuilder.AddRange(creatabilityResult.Failures);
            failureBuilder.AddRange(duplicateFailures);
            return ProfileWritePipelineResult.Failure(failureBuilder.ToImmutable());
        }

        // ------------------------------------------------------------------
        // Step 7: Assemble ProfileAppliedWriteRequest
        // ------------------------------------------------------------------
        ProfileAppliedWriteRequest request = new(
            WritableRequestBody: preResolvedRequest.WritableRequestBody,
            RootResourceCreatable: creatabilityResult.RootResourceCreatable,
            RequestScopeStates: creatabilityResult.EnrichedScopeStates,
            VisibleRequestCollectionItems: creatabilityResult.EnrichedCollectionItems
        );

        // ------------------------------------------------------------------
        // Step 8: For update/upsert — invoke C6 stored-state projector
        // ------------------------------------------------------------------
        ProfileAppliedWriteContext? context = null;

        if (!isCreate && storedDocument != null)
        {
            var projector = new StoredStateProjector(storedDocument, classifier);
            context = projector.ProjectStoredState(request, existenceLookupResult);
        }

        return ProfileWritePipelineResult.Success(request, context);
    }

    /// <summary>
    /// Backward-compatible full pipeline entry point used by direct unit tests and legacy callers.
    /// </summary>
    public static ProfileWritePipelineResult Execute(
        JsonNode canonicalizedRequestBody,
        ContentTypeDefinition? writeContentType,
        ProfileContentType? resolvedContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        JsonNode? storedDocument,
        bool isCreate,
        string profileName,
        string resourceName,
        string method,
        string operation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    )
    {
        var preResolutionResult = ExecutePreResolution(
            canonicalizedRequestBody,
            writeContentType,
            resolvedContentType,
            scopeCatalog,
            profileName,
            resourceName,
            method,
            operation
        );

        if (!preResolutionResult.HasProfile)
        {
            return ProfileWritePipelineResult.NoProfile();
        }

        if (!preResolutionResult.Failures.IsEmpty)
        {
            return ProfileWritePipelineResult.Failure(preResolutionResult.Failures);
        }

        return ExecuteResolvedTarget(
            preResolutionResult.Request
                ?? throw new InvalidOperationException(
                    "Profile pre-resolution succeeded without producing a pre-resolved request."
                ),
            writeContentType
                ?? throw new InvalidOperationException(
                    "Resolved-target execution requires a writable content type."
                ),
            scopeCatalog,
            storedDocument,
            isCreate,
            profileName,
            resourceName,
            method,
            operation,
            effectiveSchemaRequiredMembersByScope
        );
    }
}
