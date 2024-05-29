// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Pipeline;

[TestFixture]
public class PipelineProviderTests
{
    [TestFixture]
    public class Given_A_Pipeline_With_No_Steps : PipelineProviderTests
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
    public class Given_A_Pipeline_With_One_Step : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static bool wasExecuted = false;

        internal class PipelineStep : IPipelineStep
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
    public class Given_A_Pipeline_With_Three_Steps : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static List<int> executionOrder = [];

        internal class PipelineStep(int order) : IPipelineStep
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
    public class Given_A_Pipeline_Where_A_Middle_Step_Does_Not_Call_Next : PipelineProviderTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        private static List<int> executionOrder = [];

        internal class NextingPipelineStep(int order) : IPipelineStep
        {
            public async Task Execute(PipelineContext context, Func<Task> next)
            {
                executionOrder.Add(order);
                await next();
            }
        }

        internal class NonNextingPipelineStep(int order) : IPipelineStep
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
