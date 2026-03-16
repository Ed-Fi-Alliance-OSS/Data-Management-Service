// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
public class ResourceKeyValidatorTests
{
    private static ResourceKeyRow Row(short id, string project, string resource, string version) =>
        new(id, project, resource, version);

    private static DatabaseFingerprint Fingerprint(short count, byte[] seedHash) =>
        new("1.0", "abc123", count, seedHash.ToImmutableArray());

    private static byte[] HashA() => new byte[32];

    private static byte[] HashB()
    {
        var h = new byte[32];
        h[0] = 1;
        return h;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fast_Path_Where_Count_And_Hash_Match : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;
        private IResourceKeyRowReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IResourceKeyRowReader>();
            var validator = new ResourceKeyValidator(_reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow>
            {
                Row(1, "Ed-Fi", "Student", "5.0.0"),
                Row(2, "Ed-Fi", "School", "5.0.0"),
            };

            _result = await validator.ValidateAsync(
                Fingerprint(2, HashA()),
                2,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_success()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationSuccess>();
        }

        [Test]
        public void It_does_not_call_the_row_reader()
        {
            A.CallTo(() => _reader.ReadResourceKeyRowsAsync(A<string>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fast_Path_Where_Count_Mismatches : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;
        private IResourceKeyRowReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => _reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow>
                        {
                            Row(1, "Ed-Fi", "Student", "5.0.0"),
                            Row(2, "Ed-Fi", "School", "5.0.0"),
                            Row(3, "Ed-Fi", "Section", "5.0.0"),
                        }
                    )
                );

            var validator = new ResourceKeyValidator(_reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow>
            {
                Row(1, "Ed-Fi", "Student", "5.0.0"),
                Row(2, "Ed-Fi", "School", "5.0.0"),
            };

            _result = await validator.ValidateAsync(
                Fingerprint(3, HashA()),
                2,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_calls_the_row_reader()
        {
            A.CallTo(() => _reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_includes_unexpected_rows_in_diff_report()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("3");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fast_Path_Where_Hash_Mismatches_But_Count_Matches : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;
        private IResourceKeyRowReader _reader = null!;

        [SetUp]
        public async Task Setup()
        {
            _reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => _reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow>
                        {
                            Row(1, "TPDM", "Student", "5.0.0"),
                            Row(2, "Ed-Fi", "School", "5.0.0"),
                        }
                    )
                );

            var validator = new ResourceKeyValidator(_reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow>
            {
                Row(1, "Ed-Fi", "Student", "5.0.0"),
                Row(2, "Ed-Fi", "School", "5.0.0"),
            };

            _result = await validator.ValidateAsync(
                Fingerprint(2, HashB()),
                2,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_calls_the_row_reader()
        {
            A.CallTo(() => _reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_includes_modified_rows_in_diff_report()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("ProjectName");
            failure.DiffReport.Should().Contain("Ed-Fi");
            failure.DiffReport.Should().Contain("TPDM");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Slow_Path_With_Missing_Rows : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow> { Row(1, "Ed-Fi", "Student", "5.0.0") }
                    )
                );

            var validator = new ResourceKeyValidator(reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow>
            {
                Row(1, "Ed-Fi", "Student", "5.0.0"),
                Row(2, "Ed-Fi", "School", "5.0.0"),
                Row(3, "Ed-Fi", "Section", "5.0.0"),
            };

            _result = await validator.ValidateAsync(
                Fingerprint(1, HashB()),
                3,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_reports_missing_rows()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("Missing rows");
            failure.DiffReport.Should().Contain("2");
            failure.DiffReport.Should().Contain("3");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Slow_Path_With_Extra_Rows : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow>
                        {
                            Row(1, "Ed-Fi", "Student", "5.0.0"),
                            Row(2, "Ed-Fi", "School", "5.0.0"),
                            Row(3, "Ed-Fi", "Section", "5.0.0"),
                        }
                    )
                );

            var validator = new ResourceKeyValidator(reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow> { Row(1, "Ed-Fi", "Student", "5.0.0") };

            _result = await validator.ValidateAsync(
                Fingerprint(3, HashB()),
                1,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_reports_unexpected_rows()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("Unexpected rows");
            failure.DiffReport.Should().Contain("2");
            failure.DiffReport.Should().Contain("3");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Slow_Path_With_Mismatched_Fields : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow> { Row(1, "Ed-Fi", "School", "4.0.0") }
                    )
                );

            var validator = new ResourceKeyValidator(reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow> { Row(1, "Ed-Fi", "Student", "5.0.0") };

            _result = await validator.ValidateAsync(
                Fingerprint(1, HashB()),
                1,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_reports_resource_name_mismatch()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("ResourceName");
            failure.DiffReport.Should().Contain("Student");
            failure.DiffReport.Should().Contain("School");
        }

        [Test]
        public void It_reports_resource_version_mismatch()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("ResourceVersion");
            failure.DiffReport.Should().Contain("5.0.0");
            failure.DiffReport.Should().Contain("4.0.0");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Slow_Path_With_Mixed_Mismatches : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<ResourceKeyRow>>(
                        new List<ResourceKeyRow>
                        {
                            Row(1, "Ed-Fi", "Student", "4.0.0"),
                            Row(4, "TPDM", "Candidate", "1.0.0"),
                        }
                    )
                );

            var validator = new ResourceKeyValidator(reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = new List<ResourceKeyRow>
            {
                Row(1, "Ed-Fi", "Student", "5.0.0"),
                Row(2, "Ed-Fi", "School", "5.0.0"),
                Row(3, "Ed-Fi", "Section", "5.0.0"),
            };

            _result = await validator.ValidateAsync(
                Fingerprint(2, HashB()),
                3,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_reports_missing_rows()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("Missing rows");
            failure.DiffReport.Should().Contain("2");
            failure.DiffReport.Should().Contain("3");
        }

        [Test]
        public void It_reports_unexpected_rows()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("Unexpected rows");
            failure.DiffReport.Should().Contain("4");
        }

        [Test]
        public void It_reports_modified_rows()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("Modified rows");
            failure.DiffReport.Should().Contain("ResourceKeyId=1");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Slow_Path_With_More_Than_Twenty_Missing_Rows : ResourceKeyValidatorTests
    {
        private ResourceKeyValidationResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var reader = A.Fake<IResourceKeyRowReader>();
            A.CallTo(() => reader.ReadResourceKeyRowsAsync("conn1", A<CancellationToken>._))
                .Returns(Task.FromResult<IReadOnlyList<ResourceKeyRow>>(new List<ResourceKeyRow>()));

            var validator = new ResourceKeyValidator(reader, NullLogger<ResourceKeyValidator>.Instance);

            var expectedKeys = Enumerable
                .Range(1, 25)
                .Select(i => Row((short)i, "Ed-Fi", $"Resource{i}", "5.0.0"))
                .ToList();

            _result = await validator.ValidateAsync(
                Fingerprint(0, HashB()),
                25,
                HashA().ToImmutableArray(),
                expectedKeys,
                "conn1"
            );
        }

        [Test]
        public void It_returns_validation_failure()
        {
            _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
        }

        [Test]
        public void It_truncates_missing_rows_to_twenty()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("... and 5 more");
        }

        [Test]
        public void It_lists_first_twenty_missing_row_ids()
        {
            var failure = _result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>().Subject;
            failure.DiffReport.Should().Contain("20");
            failure.DiffReport.Should().NotContain("21");
        }
    }
}
