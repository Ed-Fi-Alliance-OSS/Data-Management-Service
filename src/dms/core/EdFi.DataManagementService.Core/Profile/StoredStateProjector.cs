// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Thin orchestrator for C6 stored-state projection. Calls
/// <see cref="StoredBodyShaper.Shape"/> to filter the stored document through
/// writable-profile visibility rules, then assembles a
/// <see cref="ProfileAppliedWriteContext"/> from the shaped body plus
/// pre-computed scope/item classifications from C5's
/// <see cref="StoredSideExistenceLookupResult"/>.
/// </summary>
internal sealed class StoredStateProjector(JsonNode storedDocument, ProfileVisibilityClassifier classifier)
{
    /// <summary>
    /// Projects the stored document into a <see cref="ProfileAppliedWriteContext"/>
    /// by shaping the stored body and combining it with the request contract and
    /// pre-classified stored-side scope/item state.
    /// </summary>
    /// <param name="request">
    /// The request-side contract produced by the profile write pipeline.
    /// </param>
    /// <param name="existenceLookupResult">
    /// Pre-computed stored-side classifications from C5's existence lookup builder.
    /// </param>
    /// <returns>
    /// A fully assembled <see cref="ProfileAppliedWriteContext"/> containing the
    /// request, the visibility-filtered stored body, and classified scope/item state.
    /// </returns>
    public ProfileAppliedWriteContext ProjectStoredState(
        ProfileAppliedWriteRequest request,
        StoredSideExistenceLookupResult existenceLookupResult
    )
    {
        var shaper = new StoredBodyShaper(classifier);
        JsonNode visibleStoredBody = shaper.Shape(storedDocument);

        return new ProfileAppliedWriteContext(
            request,
            visibleStoredBody,
            existenceLookupResult.ClassifiedStoredScopes,
            existenceLookupResult.ClassifiedStoredCollectionRows
        );
    }
}
