# T20 broker-backed CI selection

Reviewed 2026-09-06 against `8d3e298bae1d7a30f35cbb93832a11d6416362a6` plus
this task's fixture changes. Supports the [retirement procedure](../../operations-runbook.md#retire-binding-generation),
the [binding lifecycle owner](../../../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
and CDC-INV-14/15 (supporting CDC-INV-11).

`Given_CdcControlRetirementOperator` now carries both `CdcControlBrokerBacked` and
`CdcRetirementOperator`, retaining `NonParallelizable`. Manual review of
`.github/workflows/on-dms-pullrequest.yml` confirmed the existing control broker job
supplies Kafka, immutable Connect, and PostgreSQL image inputs and executes
`Category=CdcControlBrokerBacked`. No workflow or second lane was needed.

[Commands](commands.txt) record discovery, formatting, image digests, and the full
category invocation from the repository root. [Discovery output](discovery.txt)
contains the three retirement method entries; this adapter prints the same method
name for each parameterized fixture. The executed TRX's test definitions distinguish
the scenarios, as preserved in [exact final results](results.txt):

| Selection | Executed | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| Existing `Given_CdcControlBrokerBackedStack` | 21 | 21 | 0 | 0 |
| Retirement `registered` | 1 | 1 | 0 | 0 |
| Retirement `neverRegistered` | 1 | 1 | 0 | 0 |
| Retirement `providerTimeout` | 1 | 1 | 0 | 0 |

Final command exited 0, duration 1m46s. .NET SDK 10.0.102, VSTest 18.0.1,
Docker client 29.8.0 / engine 28.3.2; Linux/Bash. Builds and CSharpier formatting
passed; `git diff --check` passed. No automated documentation assertions were run.

The full selection exposed fixture assumptions that needed correction before it
could pass. [Prior attempts](prior-attempts.txt) retain exact failed and retirement
case identities and counters; each prior test run had 23 passed, 1 failed, 0 skipped:

- The lag test supplied the deployment topic prefix instead of the rendered
  connector `topic.prefix`. It now reads that value from the rendered configuration,
  matching the lag reader's metric lookup contract.
- An immediate topic configuration read could return `UnknownTopicOrPart` after
  creation. The existing fixture reader now retries only that error within its
  existing 60-second visibility window; other errors propagate immediately.
- One retirement setup returned policy `unknown` before reaching retirement. Setup
  now retries unknown evidence within one minute and includes the final diagnostic
  codes/messages on failure. A nonconforming policy is not retried. The original
  assertion did not capture diagnostics, so its precise unknown cause is unproved.

One intermediate build caught analyzer S6603 on `List.All`; changed to `TrueForAll`
before the final run. All corrections are in test fixtures; production behavior and
the retirement assertions are unchanged.

Raw logs, TRX, and fixture JSON attachments remain at `/tmp/dms-1326-t20`.
Published discovery replaces the checkout path with `<repo-root>`; result extracts
preserve counters, parameterized identities, and failure messages without publishing
payloads or connection strings. All fixture-owned Docker stacks were disposed;
pre-existing local servers were retained. These are local broker/provider results,
not a claim that GitHub CI ran. The [T17 evidence boundaries](../t17/README.md) still
apply: provider timeout is injected, anonymous-superuser ACL metadata does not prove
production isolation, and fixture cleanup does not prove platform/consumer purge.
