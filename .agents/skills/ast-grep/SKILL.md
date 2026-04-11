---
name: ast-grep
description: Guide for writing ast-grep rules for C# structural code search and analysis. Use when users need to search C# codebases using AST patterns, find declarations or invocations by syntax shape, or perform structural queries that go beyond text search.
---

# ast-grep for C#

## Overview

This skill helps translate natural language queries into `ast-grep` rules for C# codebases. It is intentionally optimized for `--lang csharp` usage.

`ast-grep` is strongest when the question is about syntax shape rather than compiler semantics.

Use it for:
- finding invocations, declarations, attributes, `await`, `using`, `catch`, and similar syntax forms
- locating code inside specific contexts such as methods, classes, or `catch` blocks
- rewriting repetitive syntax patterns once the pattern is stable

Do not treat it like Roslyn. It does not reliably answer semantic questions such as overload resolution, inheritance, symbol visibility, or type-flow analysis.

## When To Use This Skill

Use this skill when users:
- want structural C# search instead of plain text search
- need to match declarations such as classes, methods, properties, records, or attributes
- need to match invocation shapes such as `logger.LogError(...)`
- need composite searches like "async methods with `await` but no `try`"
- want a starting `ast-grep` rule or a corrected rule that currently does not match

## Workflow

### 1. Clarify the target C# shape

Establish:
- what exact C# syntax should match
- whether the user wants declarations, statements, expressions, or attributes
- what should be excluded
- whether syntax-only matching is sufficient, or whether the task really needs Roslyn

### 2. Build a minimal C# example first

Write the smallest valid C# snippet that expresses the target shape.

Example target: methods containing `await`

```csharp
class Sample
{
    async Task LoadAsync()
    {
        await FetchAsync();
    }
}
```

### 3. Start with the simplest rule

Prefer this progression:
1. `pattern` for direct syntax matches
2. `kind` when you need a specific node type
3. `has` or `inside` for relationships
4. `all`, `any`, `not` for composition

For relational rules, default to `stopBy: end`.

### 4. Watch for the main C# parsing pitfall

Bare member snippets often parse as local statements instead of top-level declarations.

Example:
- `async Task LoadAsync() { await FetchAsync(); }` parses as `local_function_statement`
- `class C { async Task LoadAsync() { await FetchAsync(); } }` contains a real `method_declaration`

When you need declaration kinds such as `method_declaration`, prefer `pattern.context` with `selector`:

```yaml
id: async-method-with-await
language: csharp
rule:
  all:
    - pattern:
        context: |
          class C
          {
              async Task M()
              {
                  await FetchAsync();
              }
          }
        selector: method_declaration
    - has:
        pattern: await $EXPR
        stopBy: end
```

### 5. Inspect the AST before guessing

Use `--debug-query=pattern` to see how `ast-grep` interprets your pattern.

```bash
ast-grep run --pattern 'class C { async Task LoadAsync() { await FetchAsync(); } }' \
  --lang csharp \
  --debug-query=pattern
```

Use this to confirm:
- the node `kind`
- whether a snippet became `method_declaration` or `local_function_statement`
- where attributes, modifiers, and bodies sit in the tree

### 6. Test the rule against a small `.cs` file

For C#, testing against a temporary file is usually clearer than piping fragments through stdin.

```bash
ast-grep scan --rule rule.yml /path/to/test-file.cs
```

### 7. Search the real codebase

For simple searches:

```bash
ast-grep run --pattern 'logger.LogError($EXCEPTION, $MESSAGE)' --lang csharp src
```

For relational or composite rules:

```bash
ast-grep scan --rule rule.yml src
```

## Recommended CLI Usage

### Inspect a pattern

```bash
ast-grep run --pattern 'class C { void M() { logger.LogError(ex, message); } }' \
  --lang csharp \
  --debug-query=pattern
```

### Inspect CST or AST for a file-backed snippet

```bash
ast-grep run --pattern 'class C { void M() {} }' --lang csharp --debug-query=cst
ast-grep run --pattern 'class C { void M() {} }' --lang csharp --debug-query=ast
```

### Quick pattern search

```bash
ast-grep run --pattern 'await $EXPR' --lang csharp src
```

### Complex YAML rule search

```bash
ast-grep scan --rule my_rule.yml src
```

### Inline rules

Inline rules are fine for short iterations, but YAML files are easier to debug once the rule uses `all`, `has`, `inside`, or `not`.

```bash
ast-grep scan --inline-rules 'id: log-error-in-catch
language: csharp
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
  inside:
    pattern:
      context: |
        try
        {
            DoWork();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed");
        }
      selector: catch_clause
    stopBy: end' src
```

## C#-Specific Guidance

### Prefer valid declaration context

If matching declarations, wrap the example in a class or namespace and select the declaration node you actually care about.

Good:

```yaml
pattern:
  context: |
    class C
    {
        public string Name { get; set; }
    }
  selector: property_declaration
```

Risky:

```yaml
pattern: public string Name { get; set; }
```

### Useful C# kinds to expect

Common kinds that are often useful in this repo style of work:
- `class_declaration`
- `method_declaration`
- `property_declaration`
- `invocation_expression`
- `argument_list`
- `attribute_list`
- `catch_clause`
- `await_expression`
- `local_declaration_statement`
- `using_directive`

Always verify with `--debug-query` instead of trusting memory.

### Start with invocation patterns before matching kinds

For many C# searches, a direct invocation pattern is enough:

```yaml
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
```

Only add `kind: invocation_expression` when the simpler version is ambiguous or you need to compose more conditions.

### Use `has` for body contents

Find methods that contain `await`:

```yaml
id: methods-containing-await
language: csharp
rule:
  all:
    - kind: method_declaration
    - has:
        pattern: await $EXPR
        stopBy: end
```

### Use `inside` for context

Find `LogError` calls inside `catch` clauses:

```yaml
id: log-error-in-catch
language: csharp
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
  inside:
    kind: catch_clause
    stopBy: end
```

### Use `not` carefully

Find async methods that `await` but do not contain a `try` block:

```yaml
id: async-await-without-try
language: csharp
rule:
  all:
    - kind: method_declaration
    - has:
        pattern: await $EXPR
        stopBy: end
    - not:
        has:
          pattern:
            context: |
              try
              {
                  DoWork();
              }
              catch (Exception ex)
              {
                  Handle(ex);
              }
            selector: try_statement
          stopBy: end
```

This is syntax-only. It does not prove exception handling is sufficient.

## Troubleshooting

If a C# rule does not match:
1. inspect the pattern with `--debug-query=pattern`
2. if matching declarations, move to `pattern.context` plus `selector`
3. add `stopBy: end` on `has` or `inside`
4. simplify the rule until one sub-rule matches
5. confirm the node kind from actual output instead of guessing

## References

Detailed syntax reference lives in:
- `references/rule_reference.md`

That reference is also C#-first and uses `--lang csharp` examples throughout.
