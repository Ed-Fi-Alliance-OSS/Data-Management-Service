// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobDiagnosticsTests
{
    [TestFixture]
    public class Given_an_exception_whose_message_contains_an_unlabelled_secret
    {
        private const string Secret = "hunter2-9f3c";
        private JobFailureDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup()
        {
            InvalidOperationException exception = new($"Login failed; host=db.internal;{Secret}");
            exception.Data["ConnectionString"] = $"Server=db;Password={Secret}";
            _diagnostic = JobDiagnostics.From(exception, "ClaimNext");
        }

        [Test]
        public void It_records_the_type_chain_and_operation_only()
        {
            _diagnostic.ExceptionTypeChain.Should().Be("System.InvalidOperationException");
            _diagnostic.Operation.Should().Be("ClaimNext");
            _diagnostic.ProviderErrorCode.Should().BeNull();
        }

        [Test]
        public void It_never_carries_the_message_or_exception_data() =>
            _diagnostic.ToString().Should().NotContain(Secret).And.NotContain("db.internal");
    }

    [TestFixture]
    public class Given_nested_exceptions
    {
        private JobFailureDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup()
        {
            Exception exception = new(
                "outer secret",
                new InvalidOperationException(
                    "middle secret",
                    new AggregateException("aggregate secret", new TimeoutException("inner secret"))
                )
            );
            _diagnostic = JobDiagnostics.From(exception, "Complete");
        }

        [Test]
        public void It_joins_the_full_type_names_outermost_first() =>
            _diagnostic
                .ExceptionTypeChain.Should()
                .Be(
                    "System.Exception/System.InvalidOperationException/System.AggregateException/System.TimeoutException"
                );

        [Test]
        public void It_carries_no_message_from_any_level() =>
            _diagnostic.ToString().Should().NotContain("secret");
    }

    [TestFixture]
    public class Given_a_provider_error_code
    {
        private JobFailureDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup() =>
            _diagnostic = JobDiagnostics.From(new InvalidOperationException("lock"), "Renew", "55P03");

        [Test]
        public void It_keeps_the_code_beside_the_type_chain() =>
            _diagnostic
                .Should()
                .Be(new JobFailureDiagnostic("System.InvalidOperationException", "55P03", "Renew"));
    }

    [TestFixture]
    public class Given_identifiers_with_control_characters
    {
        private string _jobId = string.Empty;
        private string _leaseOwner = string.Empty;
        private string _empty = "not empty";

        [SetUp]
        public void Setup()
        {
            _jobId = JobDiagnostics.SafeIdentifier("3f2a9c1e\r\n2026-09-23 INFO forged entry\t\u001b[31m");
            _leaseOwner = JobDiagnostics.SafeIdentifier("cms-replica-1:worker\u0000\u0007");
            _empty = JobDiagnostics.SafeIdentifier(null);
        }

        [Test]
        public void It_removes_line_breaks_and_control_characters()
        {
            _jobId.Should().Be("3f2a9c1e2026-09-23 INFO forged entry31m");
            _leaseOwner.Should().Be("cms-replica-1:worker");
            _jobId.Concat(_leaseOwner).Should().NotContain(character => char.IsControl(character));
        }

        [Test]
        public void It_logs_a_missing_identifier_as_empty() => _empty.Should().BeEmpty();
    }
}
