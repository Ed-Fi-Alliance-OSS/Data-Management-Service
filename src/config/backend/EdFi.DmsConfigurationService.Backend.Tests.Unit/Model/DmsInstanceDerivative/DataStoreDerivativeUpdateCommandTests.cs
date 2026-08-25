// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using FakeItEasy;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DataStoreDerivative;

[TestFixture]
public class DataStoreDerivativeUpdateCommandTests
{
    private IDataStoreConnectionStringValidator _connectionStringValidator = null!;
    private DataStoreDerivativeUpdateCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        // Stubbed so these fixtures keep testing the command's own rules. What each engine accepts
        // is covered by the validator's own tests.
        _connectionStringValidator = A.Fake<IDataStoreConnectionStringValidator>();
        A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
            .Returns(new ConnectionStringValidationResult.Valid());
        _validator = new DataStoreDerivativeUpdateCommand.Validator(_connectionStringValidator);
    }

    [TestFixture]
    public class Given_valid_update_command : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
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
    public class Given_valid_update_command_with_snapshot_type : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "Snapshot",
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
    public class Given_update_command_without_connection_string : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
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
    public class Given_update_command_with_zero_id : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 0,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
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
    public class Given_update_command_with_negative_id : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = -1,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
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
    public class Given_update_command_with_zero_instance_id : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 0,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DataStoreId);
        }
    }

    [TestFixture]
    public class Given_update_command_with_negative_instance_id : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = -1,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DataStoreId);
        }
    }

    [TestFixture]
    public class Given_update_command_with_empty_derivative_type : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DerivativeType);
        }
    }

    [TestFixture]
    public class Given_update_command_with_invalid_derivative_type : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "InvalidType",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DerivativeType);
        }
    }

    [TestFixture]
    public class Given_update_command_with_long_derivative_type : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = new string('A', 51),
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.DerivativeType);
        }
    }

    [TestFixture]
    public class Given_update_command_with_long_connection_string : DataStoreDerivativeUpdateCommandTests
    {
        private DataStoreDerivativeUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DataStoreDerivativeUpdateCommand
            {
                Id = 1,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
                ConnectionString = new string('A', 1001),
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.ConnectionString);
        }
    }

    /// <summary>
    /// Whatever the configured engine rejected reaches the response as a ConnectionString failure.
    /// </summary>
    [TestFixture]
    public class Given_the_configured_engine_rejects_the_connection_string
        : DataStoreDerivativeUpdateCommandTests
    {
        private const string RejectionMessage = "'Connection String' was rejected by the engine.";

        private TestValidationResult<DataStoreDerivativeUpdateCommand> _result = null!;

        [SetUp]
        public void SetUpRejection()
        {
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._))
                .Returns(new ConnectionStringValidationResult.Invalid(RejectionMessage));

            _result = _validator.TestValidate(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = 1,
                    DataStoreId = 1,
                    DerivativeType = "ReadReplica",
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
    /// keeps the one message this command publishes.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_longer_than_the_limit : DataStoreDerivativeUpdateCommandTests
    {
        private TestValidationResult<DataStoreDerivativeUpdateCommand> _result = null!;

        [SetUp]
        public void SetUpTooLong()
        {
            _result = _validator.TestValidate(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = 1,
                    DataStoreId = 1,
                    DerivativeType = "ReadReplica",
                    ConnectionString = new string('A', 1001),
                }
            );
        }

        [Test]
        public void It_reports_only_the_length_message() =>
            _result
                .ShouldHaveValidationErrorFor(x => x.ConnectionString)
                .WithErrorMessage("ConnectionString must be 1000 characters or fewer.");

        [Test]
        public void It_reports_one_connection_string_error() =>
            _result
                .Errors.Count(error =>
                    error.PropertyName == nameof(DataStoreDerivativeUpdateCommand.ConnectionString)
                )
                .Should()
                .Be(1);

        [Test]
        public void It_does_not_ask_the_configured_engine() =>
            A.CallTo(() => _connectionStringValidator.Validate(A<string?>._)).MustNotHaveHappened();
    }
}
