// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal class ConfigurableStoredStateProjectionInvoker(
    ProfileVisibilityKind rootVisibility,
    ImmutableArray<string> rootHiddenMemberPaths
) : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var rootAddress = new ScopeInstanceAddress("$", []);

        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: rootAddress,
                    Visibility: rootVisibility,
                    HiddenMemberPaths: rootHiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: []
        );
    }
}

internal sealed class RootOnlyStoredStateProjectionInvoker()
    : ConfigurableStoredStateProjectionInvoker(ProfileVisibilityKind.VisiblePresent, []);
