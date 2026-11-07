// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsDataStoreProvisionerReadinessTests
{
    [TestFixture]
    public class Given_no_verdict_has_been_recorded
    {
        private DmsProvisionerReadiness _current = null!;

        [SetUp]
        public void Setup() => _current = new DmsDataStoreProvisionerReadiness().Current;

        [Test]
        public void It_is_not_ready_pending_startup() =>
            _current
                .Should()
                .BeOfType<DmsProvisionerReadiness.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        DmsDataStoreProvisionerReadiness.StartupPendingProbe
                    )
                );
    }

    [TestFixture]
    public class Given_a_recorded_verdict
    {
        private DmsDataStoreProvisionerReadiness _readiness = null!;
        private Action _recordAgain = () => { };

        [SetUp]
        public void Setup()
        {
            _readiness = new DmsDataStoreProvisionerReadiness();
            _readiness.Record(new DmsProvisionerReadiness.Disabled());
            _recordAgain = () =>
                _readiness.Record(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow));
        }

        [Test]
        public void It_exposes_the_verdict() =>
            _readiness.Current.Should().BeOfType<DmsProvisionerReadiness.Disabled>();

        [Test]
        public void It_refuses_a_second_verdict_without_a_restart()
        {
            _recordAgain.Should().Throw<InvalidOperationException>();
            _readiness.Current.Should().BeOfType<DmsProvisionerReadiness.Disabled>();
        }
    }
}
