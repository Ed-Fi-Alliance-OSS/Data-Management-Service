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
        services.AddNoOpDocumentLinkSlugResolver();
        services.Configure<ResourceLinksOptions>(static options => options.Enabled = false);

        return services;
    }

    /// <summary>
    /// Registers a no-op <see cref="IDocumentLinkSlugResolver"/> that returns a placeholder
    /// slug triple. Use this when a fixture builds its own service provider and needs to
    /// satisfy <see cref="EdFi.DataManagementService.Backend.RelationalReadMaterializer"/>'s
    /// constructor dependency without exercising real link emission. Under the DMS-1005
    /// caller-agnostic emission contract the reconstituter always invokes the resolver
    /// regardless of <c>ResourceLinksOptions.Enabled</c>; the strip pass at the
    /// response-serialization boundary removes link decoration when the flag is false.
    /// </summary>
    public static IServiceCollection AddNoOpDocumentLinkSlugResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDocumentLinkSlugResolver, NoOpDocumentLinkSlugResolver>();

        return services;
    }

    /// <summary>
    /// Factory for the real (non-passthrough) <see cref="IReadableProfileProjector"/>. Lets
    /// test projects without <see cref="EdFi.DataManagementService.Core"/>'s
    /// <c>InternalsVisibleTo</c> grant exercise the production projector logic — the projector
    /// type is <c>internal</c> in Core, but Common has the InternalsVisibleTo grant, so the
    /// construction happens here and the result is handed back via the public interface.
    /// </summary>
    public static IReadableProfileProjector CreateProductionReadableProfileProjector() =>
        new ReadableProfileProjector();

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

    private sealed class NoOpDocumentLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            new("ed-fi", "noop", "NoOp");
    }
}
