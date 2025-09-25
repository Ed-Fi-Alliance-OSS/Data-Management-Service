// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DmsInstance;

[TestFixture]
public class DmsInstanceUpdateCommandTests
{
    private DmsInstanceUpdateCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new DmsInstanceUpdateCommand.Validator();
    }

    [TestFixture]
    public class Given_valid_update_command : DmsInstanceUpdateCommandTests
    {
        private DmsInstanceUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = "Production",
                InstanceName = "Test Instance",
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
    public class Given_update_command_with_zero_id : DmsInstanceUpdateCommandTests
    {
        private DmsInstanceUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceUpdateCommand
            {
                Id = 0,
                InstanceType = "Production",
                InstanceName = "Test Instance",
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
    public class Given_update_command_with_negative_id : DmsInstanceUpdateCommandTests
    {
        private DmsInstanceUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceUpdateCommand
            {
                Id = -1,
                InstanceType = "Production",
                InstanceName = "Test Instance",
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
    public class Given_update_command_with_empty_instance_type : DmsInstanceUpdateCommandTests
    {
        private DmsInstanceUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = "",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.InstanceType);
        }
    }

    [TestFixture]
    public class Given_update_command_with_empty_instance_name : DmsInstanceUpdateCommandTests
    {
        private DmsInstanceUpdateCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = "Production",
                InstanceName = "",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.InstanceName);
        }
    }

    [TestFixture]
    public class Given_update_command_with_long_fields : DmsInstanceUpdateCommandTests
    {
        [Test]
        public void It_should_have_validation_error_for_instance_type()
        {
            var command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = new string('A', 51),
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.InstanceType);
        }

        [Test]
        public void It_should_have_validation_error_for_instance_name()
        {
            var command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = "Production",
                InstanceName = new string('A', 257),
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.InstanceName);
        }

        [Test]
        public void It_should_have_validation_error_for_connection_string()
        {
            var command = new DmsInstanceUpdateCommand
            {
                Id = 1,
                InstanceType = "Production",
                InstanceName = "Test Instance",
                ConnectionString = new string('A', 1001),
            };

            _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.ConnectionString);
        }
    }
}
