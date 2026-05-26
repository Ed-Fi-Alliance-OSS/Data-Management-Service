// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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

        [Test]
        public async Task It_can_run_only_tasks_within_a_requested_order_range()
        {
            // Act
            await _orchestrator.RunByOrderRangeAsync(0, 299, CancellationToken.None);

            // Assert
            _executionOrder.Should().Equal(100, 200);
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

    private static AppSettings AuthAppSettings(bool bypassAuthorization = false, bool multiTenancy = false) =>
        new()
        {
            AllowIdentityUpdateOverrides = string.Empty,
            BypassAuthorization = bypassAuthorization,
            MultiTenancy = multiTenancy,
        };

    private static WarmUpOidcMetadataTask CreateOidcTask(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        bool bypassAuthorization = false
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IConfigurationManager<OpenIdConnectConfiguration>)))
            .Returns(configurationManager);

        return new WarmUpOidcMetadataTask(
            serviceProvider,
            Options.Create(AuthAppSettings(bypassAuthorization: bypassAuthorization)),
            NullLogger<WarmUpOidcMetadataTask>.Instance
        );
    }

    private static CacheClaimSetsTask CreateClaimSetsTask(IClaimSetProvider claimSetProvider) =>
        new(
            claimSetProvider,
            A.Fake<IDmsInstanceProvider>(),
            Options.Create(AuthAppSettings(multiTenancy: false)),
            NullLogger<CacheClaimSetsTask>.Instance
        );

    [TestFixture]
    [NonParallelizable]
    public class Given_Both_Auth_Tasks_Are_Registered : DmsStartupOrchestratorTests
    {
        private static List<string> _callOrder = null!;
        private DmsStartupOrchestrator _orchestrator = null!;

        [SetUp]
        public void Setup()
        {
            _callOrder = [];

            var oidcConfigurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
            A.CallTo(() => oidcConfigurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    _callOrder.Add("oidc");
                    return Task.FromResult(
                        new OpenIdConnectConfiguration { Issuer = "https://issuer.example" }
                    );
                });

            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._))
                .ReturnsLazily(() =>
                {
                    _callOrder.Add("claim-sets");
                    return Task.FromResult<IList<ClaimSet>>([]);
                });

            // Intentionally register out of declared order to verify orchestrator sorting.
            var tasks = new List<IDmsStartupTask>
            {
                CreateClaimSetsTask(claimSetProvider),
                CreateOidcTask(oidcConfigurationManager),
            };

            _orchestrator = new DmsStartupOrchestrator(tasks, NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [Test]
        public async Task It_runs_OIDC_warm_up_before_claim_set_cache()
        {
            await _orchestrator.RunAllAsync(CancellationToken.None);

            _callOrder.Should().Equal("oidc", "claim-sets");
        }

        [Test]
        public async Task It_runs_both_auth_tasks_when_executed_by_the_auth_order_range()
        {
            await _orchestrator.RunByOrderRangeAsync(
                DmsStartupTaskOrderRanges.AuthInitializationMinimum,
                DmsStartupTaskOrderRanges.AuthInitializationMaximum,
                CancellationToken.None
            );

            _callOrder.Should().Equal("oidc", "claim-sets");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_The_OIDC_Warm_Up_Task_Fails : DmsStartupOrchestratorTests
    {
        private static List<string> _callOrder = null!;
        private DmsStartupOrchestrator _orchestrator = null!;

        [SetUp]
        public void Setup()
        {
            _callOrder = [];

            var oidcConfigurationManager = A.Fake<IConfigurationManager<OpenIdConnectConfiguration>>();
            A.CallTo(() => oidcConfigurationManager.GetConfigurationAsync(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("OIDC metadata unavailable"));

            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._))
                .Invokes(() => _callOrder.Add("claim-sets"))
                .ReturnsLazily(() => Task.FromResult<IList<ClaimSet>>([]));

            var tasks = new List<IDmsStartupTask>
            {
                CreateOidcTask(oidcConfigurationManager),
                CreateClaimSetsTask(claimSetProvider),
            };

            _orchestrator = new DmsStartupOrchestrator(tasks, NullLogger<DmsStartupOrchestrator>.Instance);
        }

        [Test]
        public async Task It_aborts_startup_with_a_wrapped_exception()
        {
            Func<Task> act = async () => await _orchestrator.RunAllAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("Warm Up OIDC Metadata Cache");
            exception.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        }

        [Test]
        public async Task It_does_not_execute_the_claim_set_task()
        {
            try
            {
                await _orchestrator.RunAllAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            _callOrder.Should().BeEmpty();
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_The_Claim_Set_Task_Has_An_Internal_Failure : DmsStartupOrchestratorTests
    {
        private DmsStartupOrchestrator _orchestrator = null!;

        [SetUp]
        public void Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._))
                .ThrowsAsync(new InvalidOperationException("Configuration Service unreachable"));

            _orchestrator = new DmsStartupOrchestrator(
                [CreateClaimSetsTask(claimSetProvider)],
                NullLogger<DmsStartupOrchestrator>.Instance
            );
        }

        [Test]
        public async Task It_does_not_bubble_out_of_the_orchestrator()
        {
            Func<Task> act = async () => await _orchestrator.RunAllAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }
}
