// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Category = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCategory;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class EducationOrganizationProjectionFailureTests
{
    /// <summary>The §5.5 job code of every permanent failure code.</summary>
    private static readonly Dictionary<Code, string> _permanentJobCodes = new()
    {
        [Code.NotConfigured] = "EdOrgProjectionNotConfigured",
        [Code.DiscoveryInvalid] = "EdOrgProjectionDiscoveryInvalid",
        [Code.UnsupportedContract] = "EdOrgProjectionUnsupported",
        [Code.Unsupported] = "EdOrgProjectionUnsupported",
        [Code.TokenRejected] = "EdOrgProjectionUnauthorized",
        [Code.Unauthorized] = "EdOrgProjectionUnauthorized",
        [Code.Forbidden] = "EdOrgProjectionForbidden",
        [Code.TargetNotFound] = "EdOrgProjectionTargetNotFound",
        [Code.TargetNotRoutable] = "EdOrgProjectionTargetNotRoutable",
        [Code.TargetSchemaIncompatible] = "EdOrgProjectionTargetSchemaIncompatible",
        [Code.DataInvalid] = "EdOrgProjectionDataInvalid",
        [Code.InvalidRequest] = "EdOrgProjectionInvalidRequest",
        [Code.MalformedResponse] = "EdOrgProjectionMalformedResponse",
        [Code.UnexpectedResponse] = "EdOrgProjectionUnexpectedResponse",
        [Code.LimitExceeded] = "EdOrgProjectionLimitExceeded",
    };

    private static readonly Code[] _transientCodes =
    [
        Code.DiscoveryUnavailable,
        Code.TokenUnavailable,
        Code.ProjectionChanged,
        Code.RateLimited,
        Code.ServiceUnavailable,
        Code.NetworkError,
        Code.Timeout,
    ];

    private static IEnumerable<Code> PermanentCodes() => _permanentJobCodes.Keys;

    private static IEnumerable<Code> TransientCodes() => _transientCodes;

    [TestFixture]
    public class Given_every_failure_code
    {
        [Test]
        public void It_is_listed_here_as_exactly_one_of_permanent_or_transient() =>
            PermanentCodes()
                .Concat(TransientCodes())
                .Should()
                .OnlyHaveUniqueItems()
                .And.BeEquivalentTo(Enum.GetValues<Code>());

        [TestCaseSource(typeof(EducationOrganizationProjectionFailureTests), nameof(PermanentCodes))]
        public void It_categorizes_a_permanent_code_as_permanent(Code code) =>
            EducationOrganizationProjectionFailure.CategoryOf(code).Should().Be(Category.Permanent);

        [TestCaseSource(typeof(EducationOrganizationProjectionFailureTests), nameof(TransientCodes))]
        public void It_categorizes_a_transient_code_as_transient(Code code) =>
            EducationOrganizationProjectionFailure.CategoryOf(code).Should().Be(Category.Transient);

        [Test]
        public void It_rejects_an_undefined_code() =>
            FluentActions
                .Invoking(() => EducationOrganizationProjectionFailure.CategoryOf((Code)999))
                .Should()
                .Throw<ArgumentOutOfRangeException>();
    }

    [TestFixture]
    public class Given_a_permanent_failure
    {
        [TestCaseSource(typeof(EducationOrganizationProjectionFailureTests), nameof(PermanentCodes))]
        public void It_maps_to_the_registered_job_code_and_message(Code code)
        {
            JobErrorCode? jobCode = EducationOrganizationProjectionFailure
                .Create(code, Stage.Page)
                .ToJobErrorCode();

            string expected = _permanentJobCodes[code];
            jobCode
                .Should()
                .Be(
                    new JobErrorCode(
                        expected,
                        EducationOrganizationProjectionJobErrorCodes
                            .All.Single(pair => pair.Key == expected)
                            .Value
                    )
                );
        }

        [Test]
        public void It_uses_every_registered_job_code() =>
            _permanentJobCodes
                .Values.Distinct()
                .Should()
                .BeEquivalentTo(EducationOrganizationProjectionJobErrorCodes.All.Select(pair => pair.Key));
    }

    [TestFixture]
    public class Given_a_transient_failure
    {
        [TestCaseSource(typeof(EducationOrganizationProjectionFailureTests), nameof(TransientCodes))]
        public void It_has_no_job_code(Code code) =>
            EducationOrganizationProjectionFailure
                .Create(code, Stage.Page)
                .ToJobErrorCode()
                .Should()
                .BeNull();
    }

    [TestFixture]
    public class Given_a_failure_whose_category_does_not_match_its_code
    {
        [TestCase(Category.Permanent, Code.Timeout)]
        [TestCase(Category.Transient, Code.Unauthorized)]
        public void It_refuses_to_map_it(Category category, Code code) =>
            FluentActions
                .Invoking(() =>
                    new EducationOrganizationProjectionFailure(
                        category,
                        code,
                        Stage.Page,
                        401,
                        null,
                        null,
                        0,
                        0
                    ).ToJobErrorCode()
                )
                .Should()
                .Throw<InvalidOperationException>();
    }

    [TestFixture]
    public class Given_hostile_problem_fields
    {
        private EducationOrganizationProjectionFailure _failure = null!;

        [SetUp]
        public void Setup() =>
            _failure = EducationOrganizationProjectionFailure.Create(
                Code.Forbidden,
                Stage.Token,
                500,
                "urn:ed-fi:api:x\r\nFORGED <script> " + new string('a', 400),
                "0HN:01\r\n{secret} " + new string('9', 200),
                3,
                2
            );

        [Test]
        public void It_derives_the_category_from_the_code() =>
            _failure.Category.Should().Be(Category.Permanent);

        [Test]
        public void It_keeps_the_stage_status_and_counts() =>
            (_failure.Stage, _failure.HttpStatus, _failure.PagesRead, _failure.Restarts)
                .Should()
                .Be((Stage.Token, 500, 3, 2));

        [Test]
        public void It_keeps_only_safe_problem_type_characters_up_to_the_limit() =>
            _failure.ProblemType.Should().Be(("urn:ed-fi:api:xFORGEDscript" + new string('a', 400))[..256]);

        [Test]
        public void It_keeps_only_safe_correlation_id_characters_up_to_the_limit() =>
            _failure.CorrelationId.Should().Be(("0HN:01secret" + new string('9', 200))[..128]);
    }

    [TestFixture]
    public class Given_problem_fields_with_no_safe_characters
    {
        [Test]
        public void It_keeps_neither()
        {
            EducationOrganizationProjectionFailure failure = EducationOrganizationProjectionFailure.Create(
                Code.ServiceUnavailable,
                Stage.Discovery,
                503,
                "\r\n<>{}",
                "  "
            );
            failure.ProblemType.Should().BeNull();
            failure.CorrelationId.Should().BeNull();
        }
    }
}
