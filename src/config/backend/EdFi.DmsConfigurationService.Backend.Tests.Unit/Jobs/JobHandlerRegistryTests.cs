// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobHandlerRegistryTests
{
    private static Exception? ThrownBy(Action registration)
    {
        try
        {
            registration();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [TestFixture]
    public class Given_a_registered_handler
    {
        private ServiceProvider _provider = null!;
        private IJobHandlerRegistry _registry = null!;

        [SetUp]
        public void Setup()
        {
            _provider = JobServices.WithRefreshHandler();
            _registry = _provider.GetRequiredService<IJobHandlerRegistry>();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_finds_the_registration_by_its_job_type()
        {
            _registry
                .TryGet(JobServices.RefreshJobType, out JobHandlerRegistration? registration)
                .Should()
                .BeTrue();
            registration!.PayloadVersions.Should().BeEquivalentTo(new short[] { 1, 2 });
            registration.PayloadType.Should().Be(typeof(RefreshPayload));
            registration.HandlerType.Should().Be(typeof(RefreshHandler));
            registration.ValidatorType.Should().Be(typeof(RefreshValidator));
        }

        [Test]
        public void It_matches_the_job_type_exactly() =>
            _registry.TryGet(JobServices.RefreshJobType.ToLowerInvariant(), out _).Should().BeFalse();

        [Test]
        public void It_lists_the_registration() => _registry.Registrations.Should().ContainSingle();

        [Test]
        public void It_registers_the_handler_and_the_validator()
        {
            _provider.GetRequiredService<RefreshHandler>().Should().NotBeNull();
            _provider.GetRequiredService<RefreshValidator>().Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_a_job_type_registered_twice
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup()
        {
            ServiceCollection services = new();
            services.AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                JobServices.RefreshJobType,
                1
            );
            _thrown = ThrownBy(() =>
                services.AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                    JobServices.RefreshJobType,
                    2
                )
            );
        }

        [Test]
        public void It_fails_the_registration() =>
            _thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain($"'{JobServices.RefreshJobType}' is registered more than once");
    }

    [TestFixture("")]
    [TestFixture("1Refresh")]
    [TestFixture("Data Store")]
    [TestFixture("DataStore.Refresh ")]
    [TestFixture("DataStore.Refresh\n")]
    [TestFixture("DataStore/Refresh")]
    public class Given_a_job_type_that_breaks_the_key_syntax(string jobType)
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup() =>
            _thrown = ThrownBy(() =>
                new ServiceCollection().AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                    jobType,
                    1
                )
            );

        [Test]
        public void It_fails_the_registration() =>
            _thrown.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("jobType");
    }

    [TestFixture]
    public class Given_a_payload_type_that_breaks_the_contract
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup() =>
            _thrown = ThrownBy(() =>
                new ServiceCollection().AddJobHandler<
                    TimestampedHandler,
                    TimestampedPayload,
                    TimestampedValidator
                >("Probe.DateTime", 1)
            );

        [Test]
        public void It_fails_the_registration() =>
            _thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("breaks the payload contract");
    }

    [TestFixture]
    public class Given_payload_versions_that_are_not_accepted
    {
        private readonly Dictionary<string, Exception?> _thrown = [];

        [SetUp]
        public void Setup()
        {
            _thrown.Clear();
            _thrown["none"] = ThrownBy(() =>
                new ServiceCollection().AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                    "Probe.None"
                )
            );
            _thrown["zero"] = ThrownBy(() =>
                new ServiceCollection().AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                    "Probe.Zero",
                    0
                )
            );
            _thrown["repeated"] = ThrownBy(() =>
                new ServiceCollection().AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(
                    "Probe.Repeated",
                    1,
                    1
                )
            );
        }

        [Test]
        public void It_requires_at_least_one_version() =>
            _thrown["none"].Should().BeOfType<ArgumentException>();

        [Test]
        public void It_requires_positive_versions() =>
            _thrown["zero"].Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_rejects_a_repeated_version() =>
            _thrown["repeated"].Should().BeOfType<ArgumentException>();
    }
}
