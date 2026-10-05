// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class EducationOrganizationProjectionJobErrorCodesTests
{
    [TestFixture]
    public class Given_the_projection_error_codes
    {
        private IReadOnlyList<KeyValuePair<string, string>> _all = null!;

        [SetUp]
        public void Setup() => _all = EducationOrganizationProjectionJobErrorCodes.All;

        [Test]
        public void It_lists_exactly_the_spec_codes() =>
            _all.Select(pair => pair.Key)
                .Should()
                .Equal(
                    "EdOrgProjectionNotConfigured",
                    "EdOrgProjectionDiscoveryInvalid",
                    "EdOrgProjectionUnsupported",
                    "EdOrgProjectionUnauthorized",
                    "EdOrgProjectionForbidden",
                    "EdOrgProjectionTargetNotFound",
                    "EdOrgProjectionTargetNotRoutable",
                    "EdOrgProjectionTargetSchemaIncompatible",
                    "EdOrgProjectionDataInvalid",
                    "EdOrgProjectionInvalidRequest",
                    "EdOrgProjectionMalformedResponse",
                    "EdOrgProjectionUnexpectedResponse",
                    "EdOrgProjectionLimitExceeded"
                );

        [Test]
        public void It_follows_the_job_error_code_grammar() =>
            _all.Should().OnlyContain(pair => Regex.IsMatch(pair.Key, @"^[A-Za-z][A-Za-z0-9]{0,63}\z"));

        [Test]
        public void It_uses_fixed_messages() =>
            _all.Should()
                .OnlyContain(pair =>
                    pair.Value.Length > 0
                    && pair.Value.Length <= 1000
                    && !pair.Value.Any(char.IsControl)
                    && !pair.Value.Contains('{')
                    && !pair.Value.Contains('}')
                );

        [Test]
        public void It_redefines_no_infrastructure_code() =>
            _all.Select(pair => pair.Key)
                .Should()
                .NotIntersectWith(JobErrorCode.Infrastructure.Select(errorCode => errorCode.Code));
    }
}
