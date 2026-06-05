// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using FluentValidation.TestHelper;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.DataStoreDerivative;

[TestFixture]
public class DataStoreDerivativeUpdateCommandTests
{
    private DataStoreDerivativeUpdateCommand.Validator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new DataStoreDerivativeUpdateCommand.Validator();
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
}
