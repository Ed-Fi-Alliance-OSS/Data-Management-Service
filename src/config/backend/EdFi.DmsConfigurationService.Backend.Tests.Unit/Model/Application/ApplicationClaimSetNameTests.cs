// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Application;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.Application;

public class ApplicationClaimSetNameTests
{
    private const string SharedMessage = "Claim set name must not contain white spaces or commas.";

    private static readonly string[] _namesWithCommas = ["Alpha,Beta", ",Alpha", "Alpha,", ","];
    private static readonly string[] _validNames = ["Alpha", "Alpha-Beta"];

    [TestFixture]
    public class Given_ApplicationInsertCommand_claim_set_names
    {
        private static ApplicationInsertCommand Command(string claimSetName) =>
            new()
            {
                ApplicationName = "Application",
                VendorId = 1,
                ClaimSetName = claimSetName,
                EducationOrganizationIds = [255901],
                DataStoreIds = [1],
            };

        [TestCaseSource(typeof(ApplicationClaimSetNameTests), nameof(_namesWithCommas))]
        public void It_rejects_a_name_containing_a_comma(string name)
        {
            var result = new ApplicationInsertCommand.Validator().Validate(Command(name));

            result.Errors.Select(error => error.PropertyName).Should().Equal("ClaimSetName");
            result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
        }

        [Test]
        public void It_rejects_a_whitespace_name_with_the_shared_message()
        {
            var result = new ApplicationInsertCommand.Validator().Validate(Command("Alpha Beta"));

            result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
        }

        [TestCaseSource(typeof(ApplicationClaimSetNameTests), nameof(_validNames))]
        public void It_accepts_a_valid_name(string name)
        {
            new ApplicationInsertCommand.Validator().Validate(Command(name)).IsValid.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_ApplicationUpdateCommand_claim_set_names
    {
        private static ApplicationUpdateCommand Command(string claimSetName) =>
            new()
            {
                Id = 1,
                ApplicationName = "Application",
                VendorId = 1,
                ClaimSetName = claimSetName,
                EducationOrganizationIds = [255901],
            };

        [TestCaseSource(typeof(ApplicationClaimSetNameTests), nameof(_namesWithCommas))]
        public void It_rejects_a_name_containing_a_comma(string name)
        {
            var result = new ApplicationUpdateCommand.Validator().Validate(Command(name));

            result.Errors.Select(error => error.PropertyName).Should().Equal("ClaimSetName");
            result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
        }

        [Test]
        public void It_rejects_a_whitespace_name_with_the_shared_message()
        {
            var result = new ApplicationUpdateCommand.Validator().Validate(Command("Alpha Beta"));

            result.Errors.Select(error => error.ErrorMessage).Should().Equal(SharedMessage);
        }

        [TestCaseSource(typeof(ApplicationClaimSetNameTests), nameof(_validNames))]
        public void It_accepts_a_valid_name(string name)
        {
            new ApplicationUpdateCommand.Validator().Validate(Command(name)).IsValid.Should().BeTrue();
        }
    }
}
