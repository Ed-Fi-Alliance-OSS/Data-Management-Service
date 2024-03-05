// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Middleware;

[TestFixture]
public class PipelineProviderTests
{
    [TestFixture]
    public class Given_a_pipeline_with_no_steps : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();
        private readonly PipelineProvider pipeline = new([]);

        [Test]
        public void It_should_not_throw_an_exception_when_run()
        {
            Func<Task> act = async () => await pipeline.Run(context);
            act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    public class Given_a_pipeline_with_one_step : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static bool wasExecuted = false;

        public class PipelineStep : IPipelineStep
        {
            public async Task Execute(PipelineContext context, Func<Task> next)
            {
                wasExecuted = true;
                await next();
            }
        }

        [SetUp]
        public async Task Setup()
        {
            var pipeline = new PipelineProvider([new PipelineStep()]);
            await pipeline.Run(context);
        }

        [Test]
        public void It_should_run_the_step()
        {
            wasExecuted.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_a_pipeline_with_three_steps : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static List<int> executionOrder = [];

        public class PipelineStep(int order) : IPipelineStep
        {
            public async Task Execute(PipelineContext context, Func<Task> next)
            {
                executionOrder.Add(order);
                await next();
            }
        }

        [SetUp]
        public async Task Setup()
        {
            var pipeline = new PipelineProvider(
                [new PipelineStep(1), new PipelineStep(2), new PipelineStep(3)]
            );
            await pipeline.Run(context);
        }

        [Test]
        public void It_should_run_the_steps_in_order()
        {
            executionOrder.Should().Equal(1, 2, 3);
        }
    }

    [TestFixture]
    public class Given_a_pipeline_where_a_middle_step_does_not_call_next : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static List<int> executionOrder = [];

        public class NextingPipelineStep(int order) : IPipelineStep
        {
            public async Task Execute(PipelineContext context, Func<Task> next)
            {
                executionOrder.Add(order);
                await next();
            }
        }

        public class NonNextingPipelineStep(int order) : IPipelineStep
        {
            public Task Execute(PipelineContext context, Func<Task> next)
            {
                executionOrder.Add(order);
                return Task.CompletedTask;
            }
        }

        [SetUp]
        public async Task Setup()
        {
            var pipeline = new PipelineProvider(
                [new NextingPipelineStep(1), new NonNextingPipelineStep(2), new NextingPipelineStep(3)]
            );
            await pipeline.Run(context);
        }

        [Test]
        public void It_should_stop_running_after_the_second_step()
        {
            executionOrder.Should().Equal(1, 2);
        }
    }
}
