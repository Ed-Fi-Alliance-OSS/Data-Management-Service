// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DataStore;

[TestFixture]
public class DataStoreInsertCommandTests
{
    private DataStoreInsertCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new DataStoreInsertCommand.Validator();
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
}
