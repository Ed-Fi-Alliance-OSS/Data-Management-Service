You are implementing the story at reference/design/backend-redesign/epics/07-relational-write-path/04-propagated-reference-identity-columns.md,
It is part of a epic defined in the same directory, and part of a design found in reference/design/backend-redesign/design-docs/summary.md
The tasks for you to complete the story are in tasks.json in the repo root folder.

Check for a PROBLEMS.md file in the repo root folder. If one is present, stop.

Read progress.txt in the repo root folder to see what you have done so far. If the file does not exist yet, create an empty progress.txt file first. Then take these actions:

1. Decide which task in tasks.json to work on next based on your assessment of the highest priority, not necessarily the first in the list.
- When choosing the next task, prioritize in this order:
  1. Architectural decisions and core abstractions
  2. Integration points between modules
  3. Unknown unknowns and spike work
  4. Standard features and implementation
  5. Polish, cleanup, and quick wins

2. Check all feedback loops, such as compile and tests.
- If any feedback loop fails, you must fix the issues.

3. After completing each task:
- Append to progress.txt:
  - Task completed and story reference
  - Key decisions made and reasoning
  - Files changed
  - Any blockers or notes for next iteration
  Keep entries concise. Sacrifice grammar for the sake of concision. This file helps future iterations skip exploration.
- Update completed to "true" for this task in tasks.json

4. Make a git commit of this task.

Only work on a single task.

Important: Never modify the authoritative input test files, for example src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json and src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json, they are production-level goldens. If there are issues with them you cannot resolve, for example if their format is a mismatch with your understanding of the task, output the issue in detail to a PROBLEMS.md file in the repo root folder and stop.

Important: If you are unable to make an integration or E2E test work and determine the issue is outside the scope of this story, output the issue in detail to a PROBLEMS.md file in the repo root folder and stop.

If, while implementing the task, you notice that all work is complete, output <status>COMPLETE</status>.
