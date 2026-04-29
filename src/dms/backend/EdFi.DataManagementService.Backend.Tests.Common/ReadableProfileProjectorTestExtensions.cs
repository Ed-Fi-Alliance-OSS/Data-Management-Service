// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class ReadableProfileProjectorTestExtensions
{
    public static IServiceCollection AddTestReadableProfileProjector(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IReadableProfileProjector, PassthroughReadableProfileProjector>();

        return services;
    }

    private sealed class PassthroughReadableProfileProjector : IReadableProfileProjector
    {
        public JsonNode Project(
            JsonNode reconstitutedDocument,
            ContentTypeDefinition readContentType,
            IReadOnlySet<string> identityPropertyNames
        )
        {
            ArgumentNullException.ThrowIfNull(reconstitutedDocument);
            ArgumentNullException.ThrowIfNull(readContentType);
            ArgumentNullException.ThrowIfNull(identityPropertyNames);

            return reconstitutedDocument.DeepClone();
        }
    }
}
