---
description: Harden tests and fix high-value bugs for a user-described scope using Stryker
---

Run mutation-driven hardening for a user-described scope.

**Input**: A required scope description such as `/stryker-harden filetree` or `/stryker-harden restore pipeline`.

Use the repo-local `stryker-hardening` skill for the workflow.

If the scope is missing, stop and ask the user what area they want to harden.
