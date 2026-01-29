// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class DmsStartupOrchestratorTests
{
    [TestFixture]
    public class Given_No_Tasks : DmsStartupOrchestratorTests
    {
        private DmsStartupOrchestrator _orchestrator = null!;

        [SetUp]
        public void Setup()
        {
            _orchestrator = new DmsStartupOrchestrator([], NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [Test]
        public async Task It_completes_successfully()
        {
            // Act
            Func<Task> act = async () => await _orchestrator.RunAllAsync(CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_Multiple_Tasks_With_Different_Orders : DmsStartupOrchestratorTests
    {
        private static List<int> _executionOrder = null!;
        private DmsStartupOrchestrator _orchestrator = null!;

        private class TestTask(int order, string name) : IDmsStartupTask
        {
            public int Order => order;
            public string Name => name;

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                _executionOrder.Add(order);
                return Task.CompletedTask;
            }
        }

        [SetUp]
        public void Setup()
        {
            _executionOrder = [];

            // Register tasks out of order to verify sorting
            var tasks = new List<IDmsStartupTask>
            {
                new TestTask(300, "Third Task"),
                new TestTask(100, "First Task"),
                new TestTask(200, "Second Task"),
            };

            _orchestrator = new DmsStartupOrchestrator(tasks, NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [Test]
        public async Task It_executes_tasks_in_order_by_Order_property()
        {
            // Act
            await _orchestrator.RunAllAsync(CancellationToken.None);

            // Assert
            _executionOrder.Should().Equal(100, 200, 300);
        }
    }

    [TestFixture]
    public class Given_A_Task_That_Fails : DmsStartupOrchestratorTests
    {
        private DmsStartupOrchestrator _orchestrator = null!;

        private class FailingTask : IDmsStartupTask
        {
            public int Order => 100;
            public string Name => "Failing Task";

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Task failed intentionally");
            }
        }

        [SetUp]
        public void Setup()
        {
            _orchestrator = new DmsStartupOrchestrator(
                [new FailingTask()],
                NullLogger<DmsStartupOrchestrator>.Instance
            );
        }

        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            // Act
            Func<Task> act = async () => await _orchestrator.RunAllAsync(CancellationToken.None);

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Startup task 'Failing Task' failed:*");
        }

        [Test]
        public async Task It_wraps_the_original_exception()
        {
            // Act
            Func<Task> act = async () => await _orchestrator.RunAllAsync(CancellationToken.None);

            // Assert
            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.InnerException.Should().BeOfType<InvalidOperationException>();
            exception.Which.InnerException!.Message.Should().Be("Task failed intentionally");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_Task_Fails_After_Others_Succeed : DmsStartupOrchestratorTests
    {
        private static List<int> _executionOrder = null!;
        private DmsStartupOrchestrator _orchestrator = null!;

        private class SuccessTask(int order) : IDmsStartupTask
        {
            public int Order => order;
            public string Name => $"Success Task {order}";

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                _executionOrder.Add(order);
                return Task.CompletedTask;
            }
        }

        private class FailingTask : IDmsStartupTask
        {
            public int Order => 200;
            public string Name => "Failing Task";

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                _executionOrder.Add(200);
                throw new InvalidOperationException("Task failed");
            }
        }

        [SetUp]
        public void Setup()
        {
            _executionOrder = [];

            var tasks = new List<IDmsStartupTask>
            {
                new SuccessTask(100),
                new FailingTask(),
                new SuccessTask(300),
            };

            _orchestrator = new DmsStartupOrchestrator(tasks, NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [Test]
        public async Task It_stops_execution_after_failure()
        {
            // Act
            try
            {
                await _orchestrator.RunAllAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Assert - Third task (300) should not have run
            _executionOrder.Should().Equal(100, 200);
        }
    }

    [TestFixture]
    public class Given_Cancellation_Is_Requested : DmsStartupOrchestratorTests
    {
        private DmsStartupOrchestrator _orchestrator = null!;

        private class SlowTask : IDmsStartupTask
        {
            public int Order => 100;
            public string Name => "Slow Task";

            public async Task ExecuteAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(5000, cancellationToken);
            }
        }

        [SetUp]
        public void Setup()
        {
            _orchestrator = new DmsStartupOrchestrator(
                [new SlowTask()],
                NullLogger<DmsStartupOrchestrator>.Instance
            );
        }

        [Test]
        public async Task It_throws_OperationCanceledException()
        {
            // Arrange - use an already-cancelled token
            var cancelledToken = new CancellationToken(canceled: true);

            // Act
            Func<Task> act = async () => await _orchestrator.RunAllAsync(cancelledToken);

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_Cancellation_Before_Second_Task : DmsStartupOrchestratorTests
    {
        private static List<int> _executionOrder = null!;
        private DmsStartupOrchestrator _orchestrator = null!;
        private CancellationTokenSource _cts = null!;

        private class CancellingTask(CancellationTokenSource cts) : IDmsStartupTask
        {
            public int Order => 100;
            public string Name => "Cancelling Task";

            public async Task ExecuteAsync(CancellationToken cancellationToken)
            {
                _executionOrder.Add(100);
                await cts.CancelAsync();
            }
        }

        private class SecondTask : IDmsStartupTask
        {
            public int Order => 200;
            public string Name => "Second Task";

            public Task ExecuteAsync(CancellationToken cancellationToken)
            {
                _executionOrder.Add(200);
                return Task.CompletedTask;
            }
        }

        [SetUp]
        public void Setup()
        {
            _executionOrder = [];
            _cts = new CancellationTokenSource();

            var tasks = new List<IDmsStartupTask> { new CancellingTask(_cts), new SecondTask() };

            _orchestrator = new DmsStartupOrchestrator(tasks, NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _cts.Dispose();
        }

        [Test]
        public async Task It_does_not_execute_subsequent_tasks()
        {
            // Act
            try
            {
                await _orchestrator.RunAllAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Assert - Second task should not have run
            _executionOrder.Should().Equal(100);
        }
    }
}
