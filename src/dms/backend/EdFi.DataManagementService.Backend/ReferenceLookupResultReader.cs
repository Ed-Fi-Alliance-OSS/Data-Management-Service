// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared relational reader for reference-lookup result rows emitted by dialect adapters.
/// </summary>
internal static class ReferenceLookupResultReader
{
    private const string ReferentialIdColumnName = "ReferentialId";
    private const string DocumentIdColumnName = "DocumentId";
    private const string ResourceKeyIdColumnName = "ResourceKeyId";
    private const string ReferentialIdentityResourceKeyIdColumnName = "ReferentialIdentityResourceKeyId";
    private const string IsDescriptorColumnName = "IsDescriptor";

    public static async Task<IReadOnlyList<ReferenceLookupResult>> ReadAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<ReferenceLookupResult> lookupResults = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lookupResults.Add(
                new ReferenceLookupResult(
                    ReferentialId: new ReferentialId(
                        reader.GetRequiredFieldValue<Guid>(ReferentialIdColumnName)
                    ),
                    DocumentId: reader.GetRequiredFieldValue<long>(DocumentIdColumnName),
                    ResourceKeyId: reader.GetRequiredFieldValue<short>(ResourceKeyIdColumnName),
                    ReferentialIdentityResourceKeyId: reader.GetRequiredFieldValue<short>(
                        ReferentialIdentityResourceKeyIdColumnName
                    ),
                    IsDescriptor: reader.GetRequiredFieldValue<bool>(IsDescriptorColumnName)
                )
            );
        }

        return lookupResults;
    }
}
