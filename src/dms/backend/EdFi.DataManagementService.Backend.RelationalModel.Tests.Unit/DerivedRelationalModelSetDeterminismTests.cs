// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_HandAuthored_EffectiveSchemaSet_With_Reordered_Inputs
{
    private string _orderedManifest = default!;
    private string _unorderedManifest = default!;

    [SetUp]
    public void Setup()
    {
        var orderedEffectiveSchemaSet =
            EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var unorderedEffectiveSchemaSet =
            EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet(
                reverseProjectOrder: true,
                reverseResourceOrder: true
            );

        var orderedExtensionSites = new ExtensionSiteCapturePass();
        var unorderedExtensionSites = new ExtensionSiteCapturePass();

        var orderedBuilder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new BaseTraversalRelationalModelSetPass(), orderedExtensionSites }
        );

        var unorderedBuilder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalRelationalModelSetPass(),
                unorderedExtensionSites,
            }
        );

        var orderedModelSet = orderedBuilder.Build(
            orderedEffectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        var unorderedModelSet = unorderedBuilder.Build(
            unorderedEffectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );

        _orderedManifest = BuildDeterminismManifest(orderedModelSet, orderedExtensionSites);
        _unorderedManifest = BuildDeterminismManifest(unorderedModelSet, unorderedExtensionSites);
    }

    [Test]
    public void It_should_produce_identical_determinism_manifests()
    {
        _orderedManifest.Should().Be(_unorderedManifest);
    }

    private static string BuildDeterminismManifest(
        DerivedRelationalModelSet modelSet,
        ExtensionSiteCapturePass extensionSiteCapture
    )
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("dialect", modelSet.Dialect.ToString());

            writer.WritePropertyName("projects");
            writer.WriteStartArray();

            foreach (var project in modelSet.ProjectSchemasInEndpointOrder)
            {
                writer.WriteStartObject();
                writer.WriteString("project_endpoint_name", project.ProjectEndpointName);
                writer.WriteString("project_name", project.ProjectName);
                writer.WriteString("project_version", project.ProjectVersion);
                writer.WriteBoolean("is_extension", project.IsExtensionProject);
                writer.WriteString("physical_schema", project.PhysicalSchema.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WritePropertyName("resources");
            writer.WriteStartArray();

            foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
            {
                var extensionSites = extensionSiteCapture.GetExtensionSites(resource.ResourceKey.Resource);
                var manifest = RelationalModelManifestEmitter.Emit(resource.RelationalModel, extensionSites);

                using var manifestDocument = JsonDocument.Parse(manifest);
                manifestDocument.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    private sealed class ExtensionSiteCapturePass : IRelationalModelSetPass
    {
        private readonly Dictionary<QualifiedResourceName, IReadOnlyList<ExtensionSite>> _sitesByResource =
            new();

        public int Order { get; } = 100;

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            foreach (var resource in context.ConcreteResourcesInNameOrder)
            {
                _sitesByResource[resource.ResourceKey.Resource] = context.GetExtensionSitesForResource(
                    resource.ResourceKey.Resource
                );
            }
        }

        public IReadOnlyList<ExtensionSite> GetExtensionSites(QualifiedResourceName resource)
        {
            return _sitesByResource.TryGetValue(resource, out var sites)
                ? sites
                : Array.Empty<ExtensionSite>();
        }
    }
}
