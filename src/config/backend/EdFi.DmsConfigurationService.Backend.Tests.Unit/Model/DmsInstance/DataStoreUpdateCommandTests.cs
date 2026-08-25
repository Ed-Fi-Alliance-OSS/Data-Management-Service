// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FakeItEasy;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DataStore;

[TestFixture]
public class DataStoreUpdateCommandTests
{
    private IDataStoreConnectionStringValidator _connectionStringValidator = null!;
    private DataStoreUpdateCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        // Stubbed so these fixtures keep testing the command's own rules. What each engine accepts
        // is covered by the validator's own tests.
        _connectionStringValidator = A.Fake<IDataStoreConnectionStringValidator>();
        A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
            .Returns(new ConnectionStringValidationResult.Valid());
        _validator = new DataStoreUpdateCommand.Validator(_connectionStringValidator);
    }

    [TestFixture]
    public class Given_valid_update_command : DataStoreUpdateCommandTests
    {
        private DataStoreUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_be_valid()
        {
            _validator.TestValidate(_command).ShouldNotHaveAnyValidationErrors();
        }
    }

    [TestFixture]
    public class Given_update_command_with_zero_id : DataStoreUpdateCommandTests
    {
        private DataStoreUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreUpdateCommand
            {
                Id = 0,
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.Id);
        }
    }

    [TestFixture]
    public class Given_update_command_with_negative_id : DataStoreUpdateCommandTests
    {
        private DataStoreUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreUpdateCommand
            {
                Id = -1,
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.Id);
        }
    }

    [TestFixture]
    public class Given_update_command_with_empty_instance_type : DataStoreUpdateCommandTests
    {
        private DataStoreUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = "",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DataStoreType);
        }
    }

    [TestFixture]
    public class Given_update_command_with_empty_instance_name : DataStoreUpdateCommandTests
    {
        private DataStoreUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = "Production",
                Name = "",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.Name);
        }
    }

    [TestFixture]
    public class Given_update_command_with_long_fields : DataStoreUpdateCommandTests
    {
        [Test]
        public void It_should_have_validation_error_for_instance_type()
        {
            var command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = new string('A', 51),
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.DataStoreType);
        }

        [Test]
        public void It_should_have_validation_error_for_instance_name()
        {
            var command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = "Production",
                Name = new string('A', 257),
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Name);
        }

        [Test]
        public void It_should_have_validation_error_for_connection_string()
        {
            var command = new DataStoreUpdateCommand
            {
                Id = 1,
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = new string('A', 1001),
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.ConnectionString);
        }
    }

    /// <summary>
    /// Whatever the configured engine rejected reaches the response as a ConnectionString failure.
    /// </summary>
    [TestFixture]
    public class Given_the_configured_engine_rejects_the_connection_string : DataStoreUpdateCommandTests
    {
        private const string RejectionMessage = "'Connection String' was rejected by the engine.";

        private TestValidationResult<DataStoreUpdateCommand> _result = null!;

        [SetUp]
        public void SetUpRejection()
        {
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
                .Returns(new ConnectionStringValidationResult.Invalid(RejectionMessage));

            _result = _validator.TestValidate(
                new DataStoreUpdateCommand
                {
                    Id = 1,
                    DataStoreType = "Production",
                    Name = "Test Instance",
                    ConnectionString = "Server=localhost;Database=TestDb;",
                }
            );
        }

        [Test]
        public void It_reports_the_engine_message_on_the_connection_string() =>
            _result.ShouldHaveValidationErrorFor(x => x.ConnectionString).WithErrorMessage(RejectionMessage);
    }

    /// <summary>
    /// An over-long value stops at the length rule, so the engine is never asked and the response
    /// keeps the single message it has always carried.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_longer_than_the_limit : DataStoreUpdateCommandTests
    {
        private TestValidationResult<DataStoreUpdateCommand> _result = null!;

        [SetUp]
        public void SetUpTooLong()
        {
            _result = _validator.TestValidate(
                new DataStoreUpdateCommand
                {
                    Id = 1,
                    DataStoreType = "Production",
                    Name = "Test Instance",
                    ConnectionString = new string('A', 1001),
                }
            );
        }

        [Test]
        public void It_reports_one_connection_string_error() =>
            _result
                .Errors.Count(error => error.PropertyName == nameof(DataStoreUpdateCommand.ConnectionString))
                .Should()
                .Be(1);

        [Test]
        public void It_does_not_ask_the_configured_engine() =>
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._)).MustNotHaveHappened();
    }
}
