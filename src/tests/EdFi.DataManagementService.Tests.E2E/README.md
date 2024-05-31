# Data Management System End to End Tests

This is a suite of end to end tests that will cover different scenarios. They are run against a local DMS container that must be rebuilt to stay in sync with the codebase.

## Building the DMS container

From the `src` directory, run `docker build -t local/edfi-data-management-service . -f Dockerfile`. This will 
copy the current DMS solution into the container, build it and start it.

## Running The Tests 

* Run from the Visual Studio Test Explorer.
* cd into `/src/tests` and run `dotnet test EdFi.DataManagementService.Tests.E2E`.

## Test Environments

The tests can be executed against an isolated environment or your current running environment.

By default, the tests run against an isolated [TestContainers](https://dotnet.testcontainers.org/) environment. This will setup the Docker containers for the API and the Backend, see [ContainerSetup.cs](./Management/ContainerSetup.cs) for more information.

You can also run the tests against your current running environment, by setting UseTestContainers to false in the [appsettings](./appsettings.json)

## Adding new Tests

### Setup

This project uses an Open Source fork of SpecFlow called [Reqnroll](https://reqnroll.net/), therefore, you need to install the [Reqnroll extension for Visual Studio](https://marketplace.visualstudio.com/items?itemName=Reqnroll.ReqnrollForVisualStudio2022) (This should work with the SpecFlow extension for syntax highlighting and browsing), for VSCode you can use the [Cucumber extension](https://marketplace.visualstudio.com/items?itemName=CucumberOpen.cucumber-official) to add new tests and browse to the step definition.

### Syntax

This runs the tests using the Gherkin syntax by setting the *Given*, *When* and *Then* conditions for the scenarios. These scenarios should be created in a non-technical way to clearly understand what the test does without having to worry about the implementation.

Additionally, the Hooks folder contains the Setup and Teardown instructions for the environment and for Playwright to run the tests.
