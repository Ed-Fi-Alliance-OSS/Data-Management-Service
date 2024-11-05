# Provider Tests

Provider tests in contract testing verify that the provider (API server) meets
the expectations defined by the consumer in the contract (Pact file). These
tests ensure the provider adheres to the contract, helping prevent breaking
changes for the consumer.

## Running The Provider Tests

Run the tests how you run any other test suite. For example:

- Visual Studio Test Explorer
- from `/src/config/frontend/...ProviderTests/tests` run: `dotnet test`.

> [!IMPORTANT]
Pact contract verification includes a single test in the code, using
> only one .verify method from Pact to run the contract-based tests. If any
> failures occur, an error will be displayed in the terminal, and the test
> framework will show a single failure result. To view the details of these
> failures, you’ll need to scroll up in the terminal output.

## Key Steps in contract testing

### Setup Pact Verification

The provider test will read the consumer's Pact file, usually stored in a Pact
Broker or as a local file, to retrieve the expected interactions. This file
defines the requests that the provider should handle and the corresponding
responses the consumer expects.

### Configure the provider Test

In the provider tests, configure the Pact verifier to:

- Specify the provider details (e.g., name and base URL).
- Specify the Pact source, currently we are not using the Pact Broker feature.
  Pact File should be specified in the Provider Test.
- Specify the provider states url, make sure url matches the PactBase URL.

### Running the Provider Test and Verify Interactions

The Pact verifier sends the requests to the provider based on the interactions
in the Pact file and checks that the provider responds with the expected
responses. Each test run will verify that the provider's responses match the
expected responses, as defined in the contract.

## Share the Pact file with the Provider

Share the generated Pact file with the provider for their own verification, in
our case we are storing the Pact File in the repo.

This approach allows you to create a consumer-driven contract that the provider
can validate to prevent any breaking changes.
