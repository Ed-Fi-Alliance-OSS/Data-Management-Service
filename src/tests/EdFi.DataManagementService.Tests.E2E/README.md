# Data Management System End to End Tests

This is a suite of end to end tests that will cover different scenarios. They
are run against a local DMS container that must be rebuilt to stay in sync with
the codebase.

## Testing On Containers Locally

### Compose the docker environment

From the `eng` directory:

- If you have stale volumes with old data run `./start-local-dms.ps1 -d -v` to stop services and delete volumes
- Run `./start-local-dms.ps1 -EnvironmentFile ./.env.e2e`

## Debugging API Locally

To debug the API while running the tests, change `ApiUrl` in `OpenSearchContainerSetup.cs` to `http://localhost:5198/`. 

## Running The Tests

- Run from the Visual Studio Test Explorer.
- cd into `/src/tests` and run:
  `dotnet test EdFi.DataManagementService.Tests.E2E`.

### Setup

This project uses an Open Source fork of SpecFlow called
[Reqnroll](https://reqnroll.net/), therefore, you need to install the [Reqnroll
extension for Visual
Studio](https://marketplace.visualstudio.com/items?itemName=Reqnroll.ReqnrollForVisualStudio2022)
(This should work with the SpecFlow extension for syntax highlighting and
browsing), for VSCode you can use the [Cucumber
extension](https://marketplace.visualstudio.com/items?itemName=CucumberOpen.cucumber-official)
to add new tests and browse to the step definition.

### Syntax

This runs the tests using the Gherkin syntax by setting the _Given_, _When_ and
_Then_ conditions for the scenarios. These scenarios should be created in a
non-technical way to clearly understand what the test does without having to
worry about the implementation.

Additionally, the Hooks folder contains the Setup and Teardown instructions for
the environment and for Playwright to run the tests.

### Logging

Test logs are output to the console as well as the file system according to
`appsettings.json`. The API container logs are output to the same file system
log at the end of the test run before the container is destroyed. You can find
them by searching for `API stdout logs`
