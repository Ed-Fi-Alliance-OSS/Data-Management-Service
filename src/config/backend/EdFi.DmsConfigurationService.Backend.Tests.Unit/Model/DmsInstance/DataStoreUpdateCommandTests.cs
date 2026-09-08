// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DataStore;

[TestFixture]
public class DataStoreUpdateCommandTests
{
    private DataStoreUpdateCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new DataStoreUpdateCommand.Validator();
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
}
