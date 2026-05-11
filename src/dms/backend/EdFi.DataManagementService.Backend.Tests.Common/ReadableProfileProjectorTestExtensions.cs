// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
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
        // RelationalReadMaterializer's constructor requires IDocumentLinkSlugResolver. Tests
        // that don't exercise link injection register a no-op resolver here. The default
        // ResourceLinksOptions configuration also flips Enabled=false so the reconstituter
        // never actually invokes the resolver — link-injection-specific fixtures override
        // both via services.Replace / services.Configure to opt back in.
        services.TryAddSingleton<IDocumentLinkSlugResolver, NoOpDocumentLinkSlugResolver>();
        services.Configure<ResourceLinksOptions>(static options => options.Enabled = false);

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

    /// <summary>
    /// Test-only resolver that throws if invoked. Pairs with <c>ResourceLinksOptions.Enabled
    /// = false</c> in the same test extension so the reconstituter never calls it. If a
    /// fixture flips <c>Enabled = true</c> without also replacing this registration, the
    /// throw surfaces the wiring gap fast.
    /// </summary>
    private sealed class NoOpDocumentLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException(
                "NoOpDocumentLinkSlugResolver was invoked but ResourceLinksOptions.Enabled is "
                    + "expected to be false under AddTestReadableProfileProjector. Register a "
                    + "real DocumentLinkSlugResolver (via services.Replace) before enabling "
                    + $"link emission. ResourceKeyId was {resourceKeyId}."
            );
    }
}
