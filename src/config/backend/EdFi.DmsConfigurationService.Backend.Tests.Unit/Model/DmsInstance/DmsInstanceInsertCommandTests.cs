// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DmsInstance;

[TestFixture]
public class DmsInstanceInsertCommandTests
{
    private DmsInstanceInsertCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new DmsInstanceInsertCommand.Validator();
    }

    [TestFixture]
    public class Given_valid_insert_command : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
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
    public class Given_insert_command_without_connection_string : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
                InstanceType = "Development",
                InstanceName = "Test Instance",
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
    public class Given_insert_command_with_empty_instance_type : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
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
    public class Given_insert_command_with_empty_instance_name : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
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
    public class Given_insert_command_with_long_instance_type : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
                InstanceType = new string('A', 51),
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
    public class Given_insert_command_with_long_instance_name : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = new string('A', 257),
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
    public class Given_insert_command_with_long_connection_string : DmsInstanceInsertCommandTests
    {
        private DmsInstanceInsertCommand _command = null!;

        [SetUp]
        public new void Setup()
        {
            _command = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = "Test Instance",
                ConnectionString = new string('A', 1001),
            };
        }

        [Test]
        public void It_should_have_validation_error()
        {
            _validator.TestValidate(_command).ShouldHaveValidationErrorFor(x => x.ConnectionString);
        }
    }
}
