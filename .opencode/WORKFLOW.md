# OpenSpec Task Workflow

This document defines the standard workflow for completing OpenSpec tasks in this repository.

## Completion Checklist

After completing any OpenSpec task, you **MUST** follow these steps before continuing to the next task:

### 1. Build
Run the project build to ensure all changes compile correctly.

### 2. Run Tests
Execute the full test suite to verify all tests pass and no regressions were introduced.

### 3. Commit with Conventional Commit Message
Create a meaningful git commit using the conventional commits format:

- `feat:` for new features
- `fix:` for bug fixes
- `refactor:` for refactoring
- `test:` for test additions/changes
- `docs:` for documentation changes
- `chore:` for maintenance tasks
- `perf:` for performance improvements
- `ci:` for CI/CD changes

**Example:**
```bash
git commit -m "feat: add user authentication to API endpoints"
```

## Why This Workflow?

- **Build**: Catches compilation errors early
- **Tests**: Ensures code quality and prevents regressions
- **Conventional Commits**: Maintains clear, searchable commit history and enables automated changelog generation

## Exceptions

Only skip this workflow if explicitly requested by the user, and document the reason why.
