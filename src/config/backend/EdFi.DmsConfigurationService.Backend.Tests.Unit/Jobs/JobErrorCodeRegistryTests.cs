// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobErrorCodeRegistryTests
{
    private const string ValidMessage = "The data store could not be refreshed.";

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
    public class Given_a_consumer_error_code
    {
        private ServiceProvider _provider = null!;
        private IJobErrorCodeRegistry _registry = null!;

        [SetUp]
        public void Setup()
        {
            ServiceCollection services = new();
            services.AddJobErrorCode("DataStoreRefreshFailed", ValidMessage);
            _provider = services.BuildServiceProvider();
            _registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_finds_the_consumer_code() =>
            _registry.TryGet("DataStoreRefreshFailed", out _).Should().BeTrue();

        [Test]
        public void It_keeps_the_fixed_message()
        {
            _registry.TryGet("DataStoreRefreshFailed", out JobErrorCode? errorCode);
            errorCode.Should().Be(new JobErrorCode("DataStoreRefreshFailed", ValidMessage));
        }

        [Test]
        public void It_finds_every_infrastructure_code()
        {
            foreach (JobErrorCode infrastructure in JobErrorCode.Infrastructure)
            {
                _registry
                    .TryGet(infrastructure.Code, out JobErrorCode? found)
                    .Should()
                    .BeTrue(infrastructure.Code);
                found.Should().Be(infrastructure);
            }
        }

        [Test]
        public void It_matches_codes_exactly() =>
            _registry.TryGet("datastorerefreshfailed", out _).Should().BeFalse();
    }

    [TestFixture("")]
    [TestFixture("1Failed")]
    [TestFixture("Refresh-Failed")]
    [TestFixture("Refresh Failed")]
    [TestFixture("RefreshFailed\n")]
    [TestFixture("Abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public class Given_an_error_code_that_breaks_the_code_syntax(string code)
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup() =>
            _thrown = ThrownBy(() => new ServiceCollection().AddJobErrorCode(code, ValidMessage));

        [Test]
        public void It_fails_the_registration() =>
            _thrown.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("code");
    }

    [TestFixture("")]
    [TestFixture("   ")]
    [TestFixture("The refresh\nfailed.")]
    [TestFixture("The refresh of {0} failed.")]
    [TestFixture("The refresh failed.}")]
    public class Given_a_message_that_is_not_fixed_text(string message)
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup() =>
            _thrown = ThrownBy(() => new ServiceCollection().AddJobErrorCode("RefreshFailed", message));

        [Test]
        public void It_fails_the_registration() =>
            _thrown.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("message");
    }

    [TestFixture]
    public class Given_messages_either_side_of_the_length_limit
    {
        private Exception? _atLimit;
        private Exception? _overLimit;

        [SetUp]
        public void Setup()
        {
            _atLimit = ThrownBy(() =>
                new ServiceCollection().AddJobErrorCode("AtLimit", new string('a', 1_000))
            );
            _overLimit = ThrownBy(() =>
                new ServiceCollection().AddJobErrorCode("OverLimit", new string('a', 1_001))
            );
        }

        [Test]
        public void It_accepts_1000_characters() => _atLimit.Should().BeNull();

        [Test]
        public void It_rejects_1001_characters() => _overLimit.Should().BeOfType<ArgumentException>();
    }

    [TestFixture]
    public class Given_an_infrastructure_code_redefined
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup() =>
            _thrown = ThrownBy(() =>
                new ServiceCollection().AddJobErrorCode(JobErrorCode.HandlerFailed.Code, ValidMessage)
            );

        [Test]
        public void It_fails_the_registration() =>
            _thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("infrastructure code");
    }

    [TestFixture]
    public class Given_a_code_registered_twice
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup()
        {
            ServiceCollection services = new();
            services.AddJobErrorCode("RefreshFailed", ValidMessage);
            _thrown = ThrownBy(() => services.AddJobErrorCode("RefreshFailed", "Another message."));
        }

        [Test]
        public void It_fails_the_registration() =>
            _thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("registered more than once");
    }
}
