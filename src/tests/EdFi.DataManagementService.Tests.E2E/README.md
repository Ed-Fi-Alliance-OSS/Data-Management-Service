# Data Management System End to End Tests

This is a suite of end to end tests that will cover different scenarios. They
are run against a local DMS container that must be rebuilt to stay in sync with
the codebase.

## Testing Process

You may either run the E2E tests locally from docker containers as is done in
the github actions, or you may run them against your locally debugged API
instance. See the steps below. 

### Testing locally with docker compose

From the `eng` directory:

- If you have stale volumes with old data run `./start-local-dms.ps1 -d -v` to
  stop services and delete volumes
- Run `./start-local-dms.ps1 -EnvironmentFile ./.env.e2e`

### Testing locally with API in debug mode

To debug the API while running the tests, change `ApiUrl` in
`OpenSearchContainerSetup.cs` to `http://localhost:5198/` and run
`EdFi.DataManagementService.Frontend.AspNetCore` in debug mode as usual.

> [!WARNING] Database Warning
> Your database tables will be truncated after each
> feature file runs. Double check your `DatabaseConnection` in
> `appSettings.json` and be aware of this before you run the tests.

## Running The Tests

Run the tests how you run any other test suite. For example:

- Visual Studio Test Explorer
- from `/src/tests` run: `dotnet test
  EdFi.DataManagementService.Tests.E2E`.

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
