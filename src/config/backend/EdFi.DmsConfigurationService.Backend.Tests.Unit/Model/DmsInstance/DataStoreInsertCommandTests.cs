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
public class DataStoreInsertCommandTests
{
    private IDataStoreConnectionStringValidator _connectionStringValidator = null!;
    private DataStoreInsertCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        // Stubbed so these fixtures keep testing the command's own rules. What each engine accepts
        // is covered by the validator's own tests.
        _connectionStringValidator = A.Fake<IDataStoreConnectionStringValidator>();
        A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
            .Returns(new ConnectionStringValidationResult.Valid());
        _validator = new DataStoreInsertCommand.Validator(_connectionStringValidator);
    }

    [TestFixture]
    public class Given_valid_insert_command : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
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
    public class Given_insert_command_without_connection_string : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
                DataStoreType = "Development",
                Name = "Test Instance",
                ConnectionString = null,
            };
        }

        [Test]
        public void It_should_be_valid()
        {
            _validator.TestValidate(_command).ShouldNotHaveAnyValidationErrors();
        }
    }

    [TestFixture]
    public class Given_insert_command_with_empty_instance_type : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
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
    public class Given_insert_command_with_empty_instance_name : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
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
    public class Given_insert_command_with_long_instance_type : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
                DataStoreType = new string('A', 51),
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
    public class Given_insert_command_with_long_instance_name : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = new string('A', 257),
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
    public class Given_insert_command_with_long_connection_string : DataStoreInsertCommandTests
    {
        private DataStoreInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = new string('A', 1001),
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.ConnectionString);
        }
    }

    [TestFixture]
    public class Given_insert_command_with_supported_provider : DataStoreInsertCommandTests
    {
        [TestCase("postgresql")]
        [TestCase("sqlserver")]
        [TestCase(null)]
        public void It_should_be_valid(string? provider)
        {
            var command = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
                Provider = provider,
            };

            _validator.TestValidate(command).ShouldNotHaveValidationErrorFor(x => x.Provider);
        }
    }

    [TestFixture]
    public class Given_insert_command_with_invalid_provider : DataStoreInsertCommandTests
    {
        [Test]
        public void It_reports_the_documented_message_for_an_unknown_provider()
        {
            var command = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
                Provider = "sqlite",
            };

            _validator
                .TestValidate(command)
                .ShouldHaveValidationErrorFor(x => x.Provider)
                .WithErrorMessage("Provider must be 'postgresql' or 'sqlserver'.");
        }

        [Test]
        public void It_reports_the_documented_message_for_an_over_length_provider()
        {
            var command = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
                Provider = new string('p', 51),
            };

            _validator
                .TestValidate(command)
                .ShouldHaveValidationErrorFor(x => x.Provider)
                .WithErrorMessage("Provider must be 50 characters or fewer.");
        }
    }

    /// <summary>
    /// Whatever the configured engine rejected reaches the response as a ConnectionString failure.
    /// </summary>
    [TestFixture]
    public class Given_the_configured_engine_rejects_the_connection_string : DataStoreInsertCommandTests
    {
        private const string RejectionMessage = "'Connection String' was rejected by the engine.";

        private TestValidationResult<DataStoreInsertCommand> _result = null!;

        [SetUp]
        public void SetUpRejection()
        {
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
                .Returns(new ConnectionStringValidationResult.Invalid(RejectionMessage));

            _result = _validator.TestValidate(
                new DataStoreInsertCommand
                {
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
    public class Given_a_connection_string_longer_than_the_limit : DataStoreInsertCommandTests
    {
        private TestValidationResult<DataStoreInsertCommand> _result = null!;

        [SetUp]
        public void SetUpTooLong()
        {
            _result = _validator.TestValidate(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Test Instance",
                    ConnectionString = new string('A', 1001),
                }
            );
        }

        [Test]
        public void It_reports_one_connection_string_error() =>
            _result
                .Errors.Count(error => error.PropertyName == nameof(DataStoreInsertCommand.ConnectionString))
                .Should()
                .Be(1);

        [Test]
        public void It_does_not_ask_the_configured_engine() =>
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._)).MustNotHaveHappened();
    }
}
