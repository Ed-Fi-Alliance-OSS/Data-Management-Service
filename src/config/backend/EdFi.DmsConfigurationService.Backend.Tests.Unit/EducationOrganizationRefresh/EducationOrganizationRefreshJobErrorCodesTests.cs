// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshJobErrorCodesTests
{
    [TestFixture]
    public class Given_the_refresh_error_codes
    {
        private IReadOnlyList<KeyValuePair<string, string>> _all = null!;

        private static bool IsFixedText(string message) =>
            message.Length > 0
            && message.Length <= 1000
            && !message.Any(char.IsControl)
            && !message.Contains('{')
            && !message.Contains('}');

        [SetUp]
        public void Setup() => _all = EducationOrganizationRefreshJobErrorCodes.All;

        [Test]
        public void It_lists_exactly_the_single_codes() =>
            _all.Select(pair => pair.Key)
                .Should()
                .Equal(
                    "EdOrgRefreshDataStoreNotFound",
                    "EdOrgRefreshTemporarilyUnavailable",
                    "EdOrgRefreshTargetBusy"
                );

        [Test]
        public void It_leaves_the_composite_code_out_of_the_single_codes() =>
            _all.Select(pair => pair.Key)
                .Should()
                .NotContain(EducationOrganizationRefreshJobErrorCodes.Incomplete);

        [Test]
        public void It_follows_the_job_error_code_grammar() =>
            _all.Select(pair => pair.Key)
                .Append(EducationOrganizationRefreshJobErrorCodes.Incomplete)
                .Should()
                .OnlyContain(code => Regex.IsMatch(code, @"^[A-Za-z][A-Za-z0-9]{0,63}\z"));

        [Test]
        public void It_uses_fixed_messages() =>
            _all.Select(pair => pair.Value)
                .Append(EducationOrganizationRefreshJobErrorCodes.IncompleteMessage)
                .Should()
                .OnlyContain(message => IsFixedText(message));

        [Test]
        public void It_keeps_the_composite_message_at_the_length_the_capacity_check_assumes() =>
            EducationOrganizationRefreshJobErrorCodes.IncompleteMessage.Length.Should().Be(137);

        [Test]
        public void It_redefines_no_infrastructure_or_projection_code() =>
            _all.Select(pair => pair.Key)
                .Append(EducationOrganizationRefreshJobErrorCodes.Incomplete)
                .Should()
                .NotIntersectWith(JobErrorCode.Infrastructure.Select(errorCode => errorCode.Code))
                .And.NotIntersectWith(
                    EducationOrganizationProjectionJobErrorCodes.All.Select(pair => pair.Key)
                );
    }
}
