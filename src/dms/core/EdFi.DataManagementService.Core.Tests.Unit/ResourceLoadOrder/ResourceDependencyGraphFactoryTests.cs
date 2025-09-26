// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuickGraph;

namespace EdFi.DataManagementService.Core.Tests.Unit.ResourceLoadOrder;

[TestFixture]
public class ResourceDependencyGraphFactoryTests
{
    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithUnbreakableCycle : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("one")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("two", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("two")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("one", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private ResourceDependencyGraphFactory _graphFactory = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _graphFactory = CreateGraphFactory(apiSchemaProvider);
        }

        [Test]
        public void It_should_throw_exception()
        {
            Action act = () => _graphFactory.Create();

            act.Should().Throw<NonAcyclicGraphException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class GivenAnApiSchemaWithBreakableCycle : ResourceLoadOrderCalculatorTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("one")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("two", [], isRequired: false) // Soft dependency
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithStartResource("two")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference("one", [], isRequired: true)
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        private ResourceDependencyGraphFactory _graphFactory = null!;

        [SetUp]
        public void Setup()
        {
            var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

            A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _graphFactory = CreateGraphFactory(apiSchemaProvider);
        }

        [Test]
        public void It_should_remove_the_cycle_from_the_graph()
        {
            var graph = _graphFactory.Create();

            using (new AssertionScope())
            {
                graph.VertexCount.Should().Be(2);

                graph.Vertices.Should()
                    .Contain(v => v.FullResourceName.ProjectName.Value == "Ed-Fi" && v.FullResourceName.ResourceName.Value == "one");

                graph.Vertices.Should()
                    .Contain(v => v.FullResourceName.ProjectName.Value == "Ed-Fi" && v.FullResourceName.ResourceName.Value == "two");

                graph.EdgeCount.Should().Be(1);

                graph.Edges.Should()
                    .Contain(e =>
                        e.Source.FullResourceName.ProjectName.Value == "Ed-Fi"
                        && e.Source.FullResourceName.ResourceName.Value == "one"
                        && e.Target.FullResourceName.ProjectName.Value == "Ed-Fi"
                        && e.Target.FullResourceName.ResourceName.Value == "two");
            }
        }
    }

    static ResourceDependencyGraphFactory CreateGraphFactory(IApiSchemaProvider apiSchemaProvider,
        IEnumerable<IResourceDependencyGraphTransformer>? graphTransformers = null)
    {
        var graphFactory = new ResourceDependencyGraphFactory(
            apiSchemaProvider,
            graphTransformers ?? [],
            NullLogger<ResourceLoadOrderCalculator>.Instance);

        return graphFactory;
    }
}
