// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Builds a <see cref="ResolvedReferenceSet"/> the way the resolver does: the natural-key map is derived from
/// the successful document references through the production factory, so a fixture cannot hand a stage a map
/// that disagrees with the by-path view.
/// </summary>
public static class ResolvedReferenceSets
{
    public static ResolvedReferenceSet Create(
        IReadOnlyDictionary<JsonPath, ResolvedDocumentReference>? successfulDocumentReferencesByPath = null,
        IReadOnlyDictionary<JsonPath, ResolvedDescriptorReference>? successfulDescriptorReferencesByPath =
            null,
        IReadOnlyList<DocumentReferenceFailure>? invalidDocumentReferences = null,
        IReadOnlyList<DescriptorReferenceFailure>? invalidDescriptorReferences = null
    )
    {
        successfulDocumentReferencesByPath ??= new Dictionary<JsonPath, ResolvedDocumentReference>();

        return new ResolvedReferenceSet(
            successfulDocumentReferencesByPath,
            successfulDescriptorReferencesByPath ?? new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            new ResolvedDocumentReferenceMapFactory().Create(successfulDocumentReferencesByPath.Values),
            invalidDocumentReferences ?? [],
            invalidDescriptorReferences ?? []
        );
    }
}
