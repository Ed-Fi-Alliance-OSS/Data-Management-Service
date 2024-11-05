# Consumer Tests

Pact is a consumer-driven contract testing tool, meaning the API consumer
defines a test outlining its expectations and requirements from the API
provider(s). By unit testing the API client with Pact, we generate a contract
that can be shared with the provider to validate these expectations and help
prevent breaking changes.

## Running The Consumer Tests

Run the tests how you run any other test suite. For example:

- Visual Studio Test Explorer
- from `/src/config/frontend/...ConsumerTests/tests` run: `dotnet test`.
- Once the test are executed you will find a new file inside the `/pacts/`
  folder, this folder contains a json file which is the contract used for the
  provider tests

## Generate the Contract

PactNet will automatically generate a Pact file after running the test,
typically saved to the `/pacts/` folder. This Pact file is the contract your
consumer expects from the provider.

## Share the Pact file with the Provider

Share the generated Pact file with the provider for their own verification,
often by storing it in a Pact Broker or a shared repository.

This approach allows you to create a consumer-driven contract that the provider
can validate to prevent any breaking changes.
