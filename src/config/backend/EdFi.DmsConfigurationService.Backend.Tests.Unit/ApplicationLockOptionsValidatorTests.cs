// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class ApplicationLockOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(ApplicationLockOptions options) =>
        new ApplicationLockOptionsValidator().Validate(null, options);

    [TestFixture]
    public class Given_a_zero_acquire_timeout
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new ApplicationLockOptions { AcquireTimeout = TimeSpan.Zero });

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_negative_acquire_timeout
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(-1) });

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_acquire_timeout_above_the_maximum
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(61) });

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_acquire_timeout_at_the_maximum_boundary
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(60) });

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_default_acquire_timeout
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(new ApplicationLockOptions());

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_minimal_positive_acquire_timeout
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromMilliseconds(1) });

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }
}
