// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.OwnershipToken;

[TestFixture]
public class OwnershipTokenCommandTests
{
    [TestFixture]
    public class Given_valid_ownership_token_insert_command
    {
        private OwnershipTokenInsertCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new OwnershipTokenInsertCommand.Validator();
        }

        [Test]
        public void It_should_be_valid()
        {
            var command = new OwnershipTokenInsertCommand { Description = "District owner" };

            _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
        }
    }

    [TestFixture]
    public class Given_ownership_token_insert_command_with_blank_description
    {
        private OwnershipTokenInsertCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new OwnershipTokenInsertCommand.Validator();
        }

        [TestCase("")]
        [TestCase("   ")]
        public void It_should_have_validation_error(string description)
        {
            var command = new OwnershipTokenInsertCommand { Description = description };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Description);
        }
    }

    [TestFixture]
    public class Given_ownership_token_insert_command_with_long_description
    {
        private OwnershipTokenInsertCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new OwnershipTokenInsertCommand.Validator();
        }

        [Test]
        public void It_should_have_validation_error()
        {
            var command = new OwnershipTokenInsertCommand { Description = new string('A', 51) };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Description);
        }
    }

    [TestFixture]
    public class Given_valid_ownership_token_update_command
    {
        private OwnershipTokenUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new OwnershipTokenUpdateCommand.Validator();
        }

        [TestCase(1)]
        [TestCase(32767)]
        public void It_should_be_valid(int id)
        {
            var command = new OwnershipTokenUpdateCommand { Id = id, Description = "District owner" };

            _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
        }
    }

    [TestFixture]
    public class Given_ownership_token_update_command_with_out_of_range_id
    {
        private OwnershipTokenUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new OwnershipTokenUpdateCommand.Validator();
        }

        [TestCase(0)]
        [TestCase(32768)]
        public void It_should_have_validation_error(int id)
        {
            var command = new OwnershipTokenUpdateCommand { Id = id, Description = "District owner" };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Id);
        }
    }

    [TestFixture]
    public class Given_valid_api_client_ownership_update_command
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [Test]
        public void It_should_allow_null_creator_and_empty_read_modify_tokens()
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = [],
            };

            _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
        }

        [Test]
        public void It_should_allow_creator_and_read_modify_token_boundaries()
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = 32767,
                OwnershipTokenIds = [1, 32767],
            };

            _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_invalid_api_client_id
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void It_should_have_validation_error(int apiClientId)
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = apiClientId,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = [],
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.ApiClientId);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_invalid_creator_token_id
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [TestCase(0)]
        [TestCase(32768)]
        public void It_should_have_validation_error(int creatorOwnershipTokenId)
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = creatorOwnershipTokenId,
                OwnershipTokenIds = [],
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.CreatorOwnershipTokenId);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_null_read_modify_tokens
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [Test]
        public void It_should_have_validation_error()
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = null!,
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.OwnershipTokenIds);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_duplicate_read_modify_tokens
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [Test]
        public void It_should_have_validation_error()
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = [1, 2, 1],
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.OwnershipTokenIds);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_too_many_read_modify_tokens
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [Test]
        public void It_should_have_validation_error()
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = Enumerable.Range(1, 2000).ToArray(),
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.OwnershipTokenIds);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_update_command_with_out_of_range_read_modify_tokens
    {
        private ApiClientOwnershipUpdateCommand.Validator _validator = null!;

        [SetUp]
        public void Setup()
        {
            _validator = new ApiClientOwnershipUpdateCommand.Validator();
        }

        [TestCase(0)]
        [TestCase(32768)]
        public void It_should_have_validation_error(int ownershipTokenId)
        {
            var command = new ApiClientOwnershipUpdateCommand
            {
                ApiClientId = 1,
                CreatorOwnershipTokenId = null,
                OwnershipTokenIds = [ownershipTokenId],
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.OwnershipTokenIds);
        }
    }
}
