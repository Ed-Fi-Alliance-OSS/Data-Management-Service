// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsProvisioningDiagnosticTests
{
    [TestFixture]
    public class Given_an_exception_whose_message_and_data_contain_secrets
    {
        private const string Secret = "hunter2-9f3c";
        private DmsProvisioningDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup()
        {
            InvalidOperationException exception = new($"Login failed; Host=db.internal;Password={Secret}");
            exception.Data["ConnectionString"] = $"Server=db.internal;Password={Secret}";
            _diagnostic = DmsProvisioningDiagnostic.From(exception, "AcquireTargetLock");
        }

        [Test]
        public void It_records_the_type_chain_and_operation_only()
        {
            _diagnostic.ExceptionTypeChain.Should().Be("System.InvalidOperationException");
            _diagnostic.Operation.Should().Be("AcquireTargetLock");
            _diagnostic.ProviderErrorCode.Should().BeNull();
        }

        [Test]
        public void It_never_carries_the_message_or_exception_data() =>
            _diagnostic.ToString().Should().NotContain(Secret).And.NotContain("db.internal");
    }

    [TestFixture]
    public class Given_nested_exceptions
    {
        private DmsProvisioningDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup()
        {
            Exception exception = new(
                "outer secret",
                new InvalidOperationException("middle secret", new TimeoutException("inner secret"))
            );
            _diagnostic = DmsProvisioningDiagnostic.From(exception, "RestoreIntoStaging", "57014");
        }

        [Test]
        public void It_joins_the_full_type_names_outermost_first() =>
            _diagnostic
                .ExceptionTypeChain.Should()
                .Be("System.Exception/System.InvalidOperationException/System.TimeoutException");

        [Test]
        public void It_keeps_the_provider_code_beside_the_chain() =>
            _diagnostic.ProviderErrorCode.Should().Be("57014");

        [Test]
        public void It_carries_no_message_from_any_level() =>
            _diagnostic.ToString().Should().NotContain("secret");
    }

    [TestFixture]
    public class Given_provider_error_codes
    {
        private readonly Dictionary<string, bool> _valid = [];
        private Action _constructWithFreeText = () => { };

        [SetUp]
        public void Setup()
        {
            foreach (
                string code in new[]
                {
                    "42P04",
                    "55P03",
                    "57014",
                    "1801",
                    "-2",
                    "4060",
                    "abc",
                    "",
                    "42p04",
                    "12345678901",
                    "4 2P04",
                    "ERROR",
                }
            )
            {
                _valid[code] = DmsProvisioningDiagnostic.IsValidProviderErrorCode(code);
            }

            _constructWithFreeText = () =>
                _ = new DmsProvisioningDiagnostic("System.Exception", "password=hunter2", "Fence");
        }

        [Test]
        public void It_accepts_sqlstates()
        {
            _valid["42P04"].Should().BeTrue();
            _valid["55P03"].Should().BeTrue();
            _valid["57014"].Should().BeTrue();
        }

        [Test]
        public void It_accepts_sql_server_error_numbers()
        {
            _valid["1801"].Should().BeTrue();
            _valid["4060"].Should().BeTrue();
            _valid["-2"].Should().BeTrue();
        }

        [Test]
        public void It_rejects_anything_else()
        {
            _valid["abc"].Should().BeFalse();
            _valid[""].Should().BeFalse();
            _valid["42p04"].Should().BeFalse();
            _valid["12345678901"].Should().BeFalse();
            _valid["4 2P04"].Should().BeFalse();
            _valid["ERROR"]
                .Should()
                .BeTrue("five uppercase letters are a syntactically valid SQLSTATE class");
        }

        [Test]
        public void It_refuses_free_text_as_a_provider_code() =>
            _constructWithFreeText.Should().Throw<ArgumentException>();
    }

    [TestFixture]
    public class Given_a_blank_operation
    {
        private Action _construct = () => { };

        [SetUp]
        public void Setup() => _construct = () => DmsProvisioningDiagnostic.ForOperation(" ");

        [Test]
        public void It_is_rejected() => _construct.Should().Throw<ArgumentException>();
    }
}
