// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class MappingSetResourceLookupSupport
{
    public static bool TryGetConcreteResourceModel(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        [NotNullWhen(true)] out ConcreteResourceModel? concreteResourceModel
    )
    {
        return mappingSet.TryGetConcreteResourceModel(resource, out concreteResourceModel);
    }

    public static string FormatResource(QualifiedResourceName resource)
    {
        return MappingSetResourceLookupExtensions.FormatResource(resource);
    }
}
