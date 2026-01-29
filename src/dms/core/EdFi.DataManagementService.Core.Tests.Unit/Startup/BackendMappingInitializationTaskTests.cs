// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class BackendMappingInitializationTaskTests
{
    [TestFixture]
    public class Given_A_Backend_Mapping_Initializer : BackendMappingInitializationTaskTests
    {
        private IBackendMappingInitializer _mockInitializer = null!;
        private BackendMappingInitializationTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _mockInitializer = A.Fake<IBackendMappingInitializer>();
            _task = new BackendMappingInitializationTask(
                _mockInitializer,
                NullLogger<BackendMappingInitializationTask>.Instance
            );
        }

        [Test]
        public void It_has_order_300()
        {
            _task.Order.Should().Be(300);
        }

        [Test]
        public void It_has_expected_name()
        {
            _task.Name.Should().Be("Backend Mapping Initialization");
        }

        [Test]
        public async Task It_calls_initializer_with_cancellation_token()
        {
            // Arrange
            using var cts = new CancellationTokenSource();

            // Act
            await _task.ExecuteAsync(cts.Token);

            // Assert
            A.CallTo(() => _mockInitializer.InitializeAsync(cts.Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_propagates_exceptions_from_initializer()
        {
            // Arrange
            A.CallTo(() => _mockInitializer.InitializeAsync(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("Initialization failed"));

            // Act
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Initialization failed");
        }
    }
}
