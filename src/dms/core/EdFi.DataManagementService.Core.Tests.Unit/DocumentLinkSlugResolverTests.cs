// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.ApiSchema;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
public class DocumentLinkSlugResolverTests
{
    private const short SchoolResourceKeyId = 1;
    private const short UnknownResourceKeyId = 99;

    private static ApiSchemaDocumentNodes BuildEdFiSchoolApiSchema() =>
        new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

    private static MappingSet BuildMappingSetWith(params ResourceKeyEntry[] entries)
    {
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "rmv-test",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: (short)entries.Length,
            ResourceKeySeedHash: Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, "project-hash"),
            ],
            ResourceKeysInIdOrder: entries
        );

        return new MappingSet(
            Key: new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                SqlDialect.Pgsql,
                effectiveSchema.RelationalMappingVersion
            ),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: entries.ToDictionary(e => e.Resource, e => e.ResourceKeyId),
            ResourceKeyById: entries.ToDictionary(e => e.ResourceKeyId),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static DocumentLinkSlugResolver CreateResolver(ApiSchemaDocumentNodes nodes)
    {
        var provider = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => provider.GetApiSchemaNodes()).Returns(nodes);
        return new DocumentLinkSlugResolver(provider, NullLogger<DocumentLinkSlugResolver>.Instance);
    }

    private static ResourceKeyEntry SchoolEntry() =>
        new(
            ResourceKeyId: SchoolResourceKeyId,
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            ResourceVersion: "5.0.0",
            IsAbstractResource: false
        );

    [Test]
    public void It_resolves_valid_resource_key_to_expected_triple()
    {
        var resolver = CreateResolver(BuildEdFiSchoolApiSchema());
        var mappingSet = BuildMappingSetWith(SchoolEntry());

        var triple = resolver.Resolve(mappingSet, SchoolResourceKeyId);

        triple
            .Should()
            .Be(
                new DocumentLinkSlugTriple(
                    ProjectEndpointName: "ed-fi",
                    EndpointName: "schools",
                    ResourceName: "School"
                )
            );
    }

    [Test]
    public void It_throws_when_resource_key_id_is_not_in_mapping_set()
    {
        var resolver = CreateResolver(BuildEdFiSchoolApiSchema());
        var mappingSet = BuildMappingSetWith(SchoolEntry());

        Action act = () => resolver.Resolve(mappingSet, UnknownResourceKeyId);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*ResourceKeyId {UnknownResourceKeyId}*not present*");
    }

    [Test]
    public void It_throws_when_project_schema_is_absent_for_the_resolved_project_name()
    {
        var resolver = CreateResolver(BuildEdFiSchoolApiSchema());
        var orphanedEntry = new ResourceKeyEntry(
            ResourceKeyId: SchoolResourceKeyId,
            Resource: new QualifiedResourceName("MissingProject", "School"),
            ResourceVersion: "5.0.0",
            IsAbstractResource: false
        );
        var mappingSet = BuildMappingSetWith(orphanedEntry);

        Action act = () => resolver.Resolve(mappingSet, SchoolResourceKeyId);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProjectSchema*MissingProject*not found*");
    }

    [Test]
    public void It_returns_the_cached_triple_on_repeat_call_for_the_same_key()
    {
        var resolver = CreateResolver(BuildEdFiSchoolApiSchema());
        var mappingSet = BuildMappingSetWith(SchoolEntry());

        var first = resolver.Resolve(mappingSet, SchoolResourceKeyId);
        var second = resolver.Resolve(mappingSet, SchoolResourceKeyId);

        second.Should().BeSameAs(first);
    }

    [Test]
    public void It_isolates_cache_entries_per_mapping_set_instance()
    {
        // The ConditionalWeakTable key is the MappingSet instance, so two different mapping
        // sets — even with the same resource-key id — get separate cache entries. This is
        // what enables old entries to be GC'd when their MappingSet is released.
        var resolver = CreateResolver(BuildEdFiSchoolApiSchema());
        var firstMappingSet = BuildMappingSetWith(SchoolEntry());
        var secondMappingSet = BuildMappingSetWith(SchoolEntry());

        var first = resolver.Resolve(firstMappingSet, SchoolResourceKeyId);
        var second = resolver.Resolve(secondMappingSet, SchoolResourceKeyId);

        // Equal values (same resolution rules), but the cache produced separate instances
        // — one per mapping-set entry — confirming the per-instance partitioning.
        first.Should().Be(second);
        second.Should().NotBeSameAs(first);
    }
}
