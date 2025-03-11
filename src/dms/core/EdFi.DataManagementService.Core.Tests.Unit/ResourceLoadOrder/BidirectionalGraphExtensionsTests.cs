// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ResourceLoadOrder;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuickGraph;

namespace EdFi.DataManagementService.Core.Tests.Unit.ResourceLoadOrder
{
    public class BidirectionalGraphExtensionsTests
    {
        private readonly ILogger _logger = NullLogger.Instance;

        [TestFixture]
        public class WhenEvaluatingAGraphWithNoCyclicalDependencies : BidirectionalGraphExtensionsTests
        {
            private BidirectionalGraph<string, IEdge<string>>? _graph;

            [SetUp]
            protected void Setup()
            {
                _graph = new BidirectionalGraph<string, IEdge<string>>();

                /*
                 *   (A) --> (B) --> (C1) --> (D1)
                 *               \
                 *                --> (C2) --> (D2)
                */

                _graph.AddVertex("A");
                _graph.AddVertex("B");
                _graph.AddVertex("C1");
                _graph.AddVertex("D1");
                _graph.AddVertex("C2");
                _graph.AddVertex("D2");

                _graph.AddEdge(new Edge<string>("A", "B"));
                _graph.AddEdge(new Edge<string>("B", "C1"));
                _graph.AddEdge(new Edge<string>("C1", "D1"));
                _graph.AddEdge(new Edge<string>("B", "C2"));
                _graph.AddEdge(new Edge<string>("C2", "D2"));
            }

            [Test]
            public void Should_not_throw_an_exception()
            {
                Action act = () => _graph!.ValidateGraph(_logger);

                act.Should().NotThrow<NonAcyclicGraphException>();
            }
        }

        [TestFixture]
        public class WhenAGraphHasACyclicalDependency : BidirectionalGraphExtensionsTests
        {
            private BidirectionalGraph<string, IEdge<string>>? _graph;

            [SetUp]
            protected void Setup()
            {
                _graph = new BidirectionalGraph<string, IEdge<string>>();

                /*
                 *            .----------------------.
                 *            V                       \
                 *   (A) --> (B) --> (C1) --> (D1)     |
                 *               \                     |
                 *                --> (C2) --> (D2)    |
                 *                         \           |
                 *                          --> (E2) --'
                */

                _graph.AddVertex("A");
                _graph.AddVertex("B");
                _graph.AddVertex("C1");
                _graph.AddVertex("D1");
                _graph.AddVertex("C2");
                _graph.AddVertex("D2");

                _graph.AddEdge(new Edge<string>("A", "B"));
                _graph.AddEdge(new Edge<string>("B", "C1"));
                _graph.AddEdge(new Edge<string>("C1", "D1"));
                _graph.AddEdge(new Edge<string>("B", "C2"));
                _graph.AddEdge(new Edge<string>("C2", "D2"));

                // Add a vertex and edges that creates a cyclical dependency
                _graph.AddVertex("E2");
                _graph.AddEdge(new Edge<string>("C2", "E2"));
                _graph.AddEdge(new Edge<string>("E2", "B"));
            }

            [Test]
            public void Validation_should_throw_a_NonAcyclicGraphException()
            {
                Action act = () => _graph!.ValidateGraph(_logger);

                act.Should().Throw<NonAcyclicGraphException>();
            }

            [Test]
            public void Should_be_able_to_break_cycles_by_removing_any_removable_cycle_edge()
            {
                var clonedGraph = _graph!.Clone();
                clonedGraph.Edges.Any(Is_B_to_C2).Should().BeTrue();
                var removedEdges = clonedGraph.BreakCycles(Is_B_to_C2, _logger);
                clonedGraph.Edges.Any(Is_B_to_C2).Should().BeFalse("Removable edge B-to-C2 was not removed.");
                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 1);
                removedEdges.Single(Is_B_to_C2).Should().NotBeNull();

                clonedGraph = _graph.Clone();
                clonedGraph.Edges.Any(Is_C2_to_E2).Should().BeTrue();
                removedEdges = clonedGraph.BreakCycles(Is_C2_to_E2, _logger);
                clonedGraph
                    .Edges.Any(Is_C2_to_E2)
                    .Should()
                    .BeFalse("Removable edge C2-to-E2 was not removed.");
                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 1);
                removedEdges.Single(Is_C2_to_E2).Should().NotBeNull();

                clonedGraph = _graph.Clone();
                clonedGraph.Edges.Any(Is_E2_to_B).Should().BeTrue();
                removedEdges = clonedGraph.BreakCycles(Is_E2_to_B, _logger);
                clonedGraph.Edges.Any(Is_E2_to_B).Should().BeFalse("Removable edge E2-to-B was not removed.");
                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 1);
                removedEdges.Single(Is_E2_to_B).Should().NotBeNull();
            }

            [Test]
            public void Should_break_a_cycle_by_removing_the_deepest_removable_edge()
            {
                var clonedGraph = _graph!.Clone();
                clonedGraph.Edges.Count(Is_any_cycle_edge).Should().Be(3);

                var removedEdges = clonedGraph.BreakCycles(Is_any_cycle_edge, _logger);
                clonedGraph
                    .Edges.Any(Is_E2_to_B)
                    .Should()
                    .BeFalse("Deepest removable edge B-to-C2 was not the edge removed.");
                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 1);
                removedEdges.Single(Is_E2_to_B).Should().NotBeNull();
            }

            [Test]
            public void Should_throw_an_exception_if_none_of_the_cycle_edges_are_removable()
            {
                var clonedGraph = _graph!.Clone();
                Action act = () => clonedGraph.BreakCycles(e => !Is_any_cycle_edge(e), _logger);

                act.Should().Throw<NonAcyclicGraphException>();
            }

            private static bool Is_B_to_C2(IEdge<string> e) => e is { Source: "B", Target: "C2" };

            private static bool Is_C2_to_E2(IEdge<string> e) => e is { Source: "C2", Target: "E2" };

            private static bool Is_E2_to_B(IEdge<string> e) => e is { Source: "E2", Target: "B" };

            private static bool Is_any_cycle_edge(IEdge<string> e) =>
                Is_B_to_C2(e) || Is_C2_to_E2(e) || Is_E2_to_B(e);
        }

        [TestFixture]
        public class WhenAGraphHasTwoCyclesAndASelfReferencingAssociation : BidirectionalGraphExtensionsTests
        {
            private BidirectionalGraph<string, IEdge<string>>? _graph;

            [SetUp]
            protected void Setup()
            {
                _graph = new BidirectionalGraph<string, IEdge<string>>();

                /*    .--------------------------------.
                 *    |       .----------------------.  \
                 *    |     .-|-.    .--.             \  \
                 *    v     V V /    V  /              |  |
                 *   (A) --> (B) --> (C1) --> (D1)     |  |
                 *               \                     |  |
                 *                --> (C2) --> (D2) ---+--'
                 *                         \           |
                 *                          --> (E2) --'
                */

                _graph.AddVertex("A");
                _graph.AddVertex("B");
                _graph.AddVertex("C1");
                _graph.AddVertex("D1");
                _graph.AddVertex("C2");
                _graph.AddVertex("D2");

                _graph.AddEdge(new Edge<string>("A", "B"));
                _graph.AddEdge(new Edge<string>("B", "C1"));
                _graph.AddEdge(new Edge<string>("C1", "D1"));
                _graph.AddEdge(new Edge<string>("B", "C2"));
                _graph.AddEdge(new Edge<string>("C2", "D2"));

                // Add a vertex and edges that creates a cyclical dependency
                _graph.AddVertex("E2");
                _graph.AddEdge(new Edge<string>("C2", "E2"));
                _graph.AddEdge(new Edge<string>("E2", "B"));

                // Add a second cyclical dependency
                _graph.AddEdge(new Edge<string>("D2", "A"));

                // Add a self-referencing dependencies
                _graph.AddEdge(new Edge<string>("B", "B"));
                _graph.AddEdge(new Edge<string>("C1", "C1"));
            }

            [Test]
            public void Validation_should_fail_initially()
            {
                Action act = () => _graph!.ValidateGraph(_logger);
                act.Should().Throw<NonAcyclicGraphException>();
            }

            [Test]
            public void Should_be_able_to_break_the_cycles()
            {
                var clonedGraph = _graph!.Clone();

                IReadOnlyList<IEdge<string>> removedEdges = clonedGraph.BreakCycles(_ => true, _logger);

                Console.WriteLine("Removed edges:");
                foreach (IEdge<string> removedEdge in removedEdges)
                {
                    Console.WriteLine(removedEdge);
                }

                clonedGraph.ValidateGraph(_logger);

                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 4);

                removedEdges.Count.Should().Be(4);
                removedEdges.SingleOrDefault(a => a is { Source: "E2", Target: "B" }).Should().NotBeNull();
                removedEdges.SingleOrDefault(a => a is { Source: "D2", Target: "A" }).Should().NotBeNull();
                removedEdges.SingleOrDefault(a => a is { Source: "C1", Target: "C1" }).Should().NotBeNull();
                removedEdges.SingleOrDefault(a => a is { Source: "B", Target: "B" }).Should().NotBeNull();
            }
        }

        [TestFixture]
        public class WhenEvaluatingAGraphWithSelfReferencingVertices : BidirectionalGraphExtensionsTests
        {
            private BidirectionalGraph<string, IEdge<string>>? _graph;

            [SetUp]
            protected void Setup()
            {
                _graph = new BidirectionalGraph<string, IEdge<string>>();

                /*
                 *                   .--.
                 *                   V  /
                 *   (A) --> (B) --> (C) --> (D)
                */

                _graph.AddVertex("A");
                _graph.AddVertex("B");
                _graph.AddVertex("C");
                _graph.AddVertex("D");

                _graph.AddEdge(new Edge<string>("A", "B"));
                _graph.AddEdge(new Edge<string>("B", "C"));
                _graph.AddEdge(new Edge<string>("C", "D"));

                // Add a self-referencing edge that creates a cyclical dependency
                // But is not something that we are concerned with for resources/aggregates
                _graph.AddEdge(new Edge<string>("C", "C"));
            }

            [Test]
            public void Should_throw_an_exception_on_validation()
            {
                Action act = () => _graph!.ValidateGraph(_logger);
                act.Should().Throw<NonAcyclicGraphException>();
            }

            [Test]
            public void Should_be_able_to_break_the_cycle_by_removing_the_self_referencing_edge()
            {
                var clonedGraph = _graph!.Clone();

                var removedEdges = clonedGraph.BreakCycles(_ => true, _logger);

                clonedGraph.Edges.Count().Should().Be(_graph.Edges.Count() - 1);

                removedEdges.Count.Should().Be(1);
                removedEdges.SingleOrDefault(a => a.Source == "C" && a.Target == "C").Should().NotBeNull();
            }
        }
    }
}
