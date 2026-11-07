// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class UnavailableDmsTargetEngineTests
{
    [TestFixture]
    public class Given_environment_validation
    {
        private UnavailableDmsTargetEngine _engine = null!;
        private DmsEngineEnvironment _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _engine = new UnavailableDmsTargetEngine("mssql");
            _result = await _engine.ValidateEnvironmentAsync(CancellationToken.None);
        }

        [Test]
        public void It_reports_the_configured_provider() => _engine.Provider.Should().Be("mssql");

        [Test]
        public void It_is_never_ready() => _result.Should().BeOfType<DmsEngineEnvironment.NotReady>();

        [Test]
        public void It_names_the_adapter_as_the_only_reason() =>
            _result
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Which.Reasons.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdapterUnavailable,
                        UnavailableDmsTargetEngine.Probe
                    )
                );
    }

    [TestFixture]
    public class Given_every_other_member_of_the_port
    {
        private readonly Dictionary<string, Exception?> _thrown = [];

        [SetUp]
        public void Setup()
        {
            UnavailableDmsTargetEngine engine = new("postgresql");
            IEnumerable<MethodInfo> members = typeof(IDmsTargetEngine)
                .GetMethods()
                .Where(method =>
                    !method.IsSpecialName && method.Name != nameof(IDmsTargetEngine.ValidateEnvironmentAsync)
                );

            foreach (MethodInfo member in members)
            {
                object?[] arguments = [.. member.GetParameters().Select(DefaultArgument)];
                try
                {
                    member.Invoke(engine, arguments);
                    _thrown[member.Name] = null;
                }
                catch (TargetInvocationException exception)
                {
                    _thrown[member.Name] = exception.InnerException;
                }
            }
        }

        private static object? DefaultArgument(ParameterInfo parameter) =>
            parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;

        [Test]
        public void It_covers_the_whole_port()
        {
            int otherMembers =
                typeof(IDmsTargetEngine).GetMethods().Count(method => !method.IsSpecialName) - 1;
            _thrown.Should().HaveCount(otherMembers).And.HaveCountGreaterThan(10);
        }

        [Test]
        public void It_throws_synchronously_from_each_member()
        {
            using AssertionScope scope = new();
            foreach ((string member, Exception? exception) in _thrown)
            {
                exception.Should().BeOfType<InvalidOperationException>("member {0} must never run", member);
            }
        }
    }

    [TestFixture]
    public class Given_a_blank_provider
    {
        private Action _construct = () => { };

        [SetUp]
        public void Setup() => _construct = () => _ = new UnavailableDmsTargetEngine(" ");

        [Test]
        public void It_rejects_the_provider() => _construct.Should().Throw<ArgumentException>();
    }
}
