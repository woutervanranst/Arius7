# ast-grep Rule Reference for C#

This reference describes the main `ast-grep` rule building blocks using C# examples.

The key mindset is:
- use `ast-grep` for syntax shape
- use Roslyn when you need semantics

## Rule Categories

`ast-grep` rules are usually built from three categories:
- atomic rules: match one node by its own properties
- relational rules: match a node by where it sits relative to other nodes
- composite rules: combine smaller rules with logic

## Rule Object Shape

A rule object matches when all of its fields match.

Common properties:

| Property | Category | Purpose | C# Example |
| :--- | :--- | :--- | :--- |
| `pattern` | Atomic | Match code by syntax pattern | `pattern: logger.LogError($EXCEPTION, $MESSAGE)` |
| `kind` | Atomic | Match a specific syntax kind | `kind: method_declaration` |
| `regex` | Atomic | Match node text with regex | `regex: ^I[A-Z]` |
| `nthChild` | Atomic | Match by sibling position | `nthChild: 1` |
| `range` | Atomic | Match by location | `range: { start: { line: 0, column: 0 }, end: { line: 5, column: 0 } }` |
| `inside` | Relational | Node must appear inside another node | `inside: { kind: catch_clause, stopBy: end }` |
| `has` | Relational | Node must contain a descendant match | `has: { pattern: await $EXPR, stopBy: end }` |
| `precedes` | Relational | Node must appear before another match | `precedes: { pattern: return $VALUE }` |
| `follows` | Relational | Node must appear after another match | `follows: { pattern: _logger.LogInformation($MSG) }` |
| `all` | Composite | All sub-rules must match | `all: [ { kind: method_declaration }, { has: { pattern: await $EXPR, stopBy: end } } ]` |
| `any` | Composite | Any sub-rule may match | `any: [ { pattern: logger.LogError($$$) }, { pattern: logger.LogWarning($$$) } ]` |
| `not` | Composite | Sub-rule must not match | `not: { has: { kind: catch_clause, stopBy: end } }` |
| `matches` | Composite | Reuse a named utility rule | `matches: is-log-call` |

## Atomic Rules

### `pattern`

Use `pattern` first whenever the C# syntax shape is direct.

Simple string form:

```yaml
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
```

For C#, object form is especially useful because declaration snippets often need context.

```yaml
rule:
  pattern:
    context: |
      class C
      {
          public async Task LoadAsync()
          {
              await FetchAsync();
          }
      }
    selector: method_declaration
```

Use `context` and `selector` when you need real declaration nodes such as:
- `method_declaration`
- `property_declaration`
- `class_declaration`
- `record_declaration`

Without surrounding context, a snippet that looks like a method can parse as `local_function_statement`.

Optional `strictness` values can refine matching when needed, but start with the default.

### `kind`

`kind` matches the tree-sitter node kind directly.

```yaml
rule:
  kind: invocation_expression
```

Useful C# kinds commonly worth checking:
- `class_declaration`
- `method_declaration`
- `property_declaration`
- `invocation_expression`
- `await_expression`
- `attribute_list`
- `catch_clause`
- `try_statement`
- `local_declaration_statement`

Confirm the exact kind with `--debug-query` before relying on it.

### `regex`

`regex` matches the full text of a node.

Example: find identifiers or declarations whose text starts with `I`.

```yaml
rule:
  kind: identifier
  regex: ^I[A-Z]
```

Use this as a filter on top of structural matching, not as a replacement for structure.

### `nthChild`

Useful when order among named siblings matters.

```yaml
rule:
  kind: parameter
  nthChild: 1
```

This can help for queries like "first constructor parameter" once the surrounding rule narrows the scope.

### `range`

Use only when location is part of the task.

```yaml
rule:
  range:
    start: { line: 0, column: 0 }
    end: { line: 20, column: 0 }
```

## Relational Rules

Relational rules are where many useful C# searches become practical.

Default advice: add `stopBy: end` unless you have a specific reason not to.

### `inside`

Match nodes inside another syntax context.

Example: `LogError` calls inside `catch` clauses.

```yaml
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
  inside:
    kind: catch_clause
    stopBy: end
```

Example: `await` inside a method declaration.

```yaml
rule:
  pattern: await $EXPR
  inside:
    kind: method_declaration
    stopBy: end
```

### `has`

Match nodes that contain something else.

Example: methods that contain `await`.

```yaml
rule:
  kind: method_declaration
  has:
    pattern: await $EXPR
    stopBy: end
```

Example: classes that have a `[Test]` attribute on any descendant member is usually too broad. Prefer narrowing with `kind` and `inside` instead of scanning huge descendants unnecessarily.

### `precedes` and `follows`

Use these when order matters inside a local sequence.

Example: a log call before `return`.

```yaml
rule:
  pattern: logger.LogInformation($MESSAGE)
  precedes:
    pattern: return $VALUE
    stopBy: end
```

Example: `return` after a guard clause log call.

```yaml
rule:
  pattern: return $VALUE
  follows:
    pattern: logger.LogWarning($MESSAGE)
    stopBy: end
```

### `stopBy`

`stopBy` controls how far the relational search travels.

Useful values:
- `neighbor`: default, often too shallow
- `end`: usually the safest choice for C# body searches
- rule object: stop when a boundary rule matches

Example:

```yaml
has:
  pattern: await $EXPR
  stopBy: end
```

## Composite Rules

### `all`

Use `all` when multiple conditions must hold.

Example: async methods that contain `await`.

```yaml
rule:
  all:
    - kind: method_declaration
    - has:
        pattern: await $EXPR
        stopBy: end
```

`all` is also the safest way to combine metavariable-dependent logic because it makes evaluation order explicit.

### `any`

Use `any` for alternatives.

Example: any common log level call.

```yaml
rule:
  any:
    - pattern: logger.LogDebug($$$ARGS)
    - pattern: logger.LogInformation($$$ARGS)
    - pattern: logger.LogWarning($$$ARGS)
    - pattern: logger.LogError($$$ARGS)
```

### `not`

Use `not` as a filter after you have already matched the main shape.

Example: methods with `await` but without `try`.

```yaml
rule:
  all:
    - kind: method_declaration
    - has:
        pattern: await $EXPR
        stopBy: end
    - not:
        has:
          kind: try_statement
          stopBy: end
```

This is often good enough for audits, but remember it is syntax-only.

### `matches`

Use `matches` to reuse utility rules when a query grows beyond one readable block.

Example root config excerpt:

```yaml
utils:
  is-log-call:
    any:
      - pattern: logger.LogDebug($$$ARGS)
      - pattern: logger.LogInformation($$$ARGS)
      - pattern: logger.LogWarning($$$ARGS)
      - pattern: logger.LogError($$$ARGS)

rule:
  matches: is-log-call
```

## Metavariables

Metavariables are placeholders inside `pattern` values.

### `$VAR`

Matches one named node.

```yaml
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
```

Typical C# matches:
- `$EXCEPTION` can match `ex`
- `$MESSAGE` can match `"failed"`

Reusing the same metavariable name requires the same syntax subtree on both sides.

### `$$VAR`

Matches one unnamed node such as punctuation or an operator.

Example for binary operators:

```yaml
rule:
  kind: binary_expression
  has:
    field: operator
    pattern: $$OP
```

### `$$$VAR`

Matches zero or more nodes.

Very useful for C# argument and statement lists.

```yaml
rule:
  pattern: logger.LogInformation($$$ARGS)
```

Also useful in declarations:

```yaml
rule:
  pattern:
    context: |
      class C
      {
          void M($$$PARAMS)
          {
              $$$BODY
          }
      }
    selector: method_declaration
```

### Non-capturing names

Names beginning with `_` are non-capturing and may match different subtrees each time.

```yaml
rule:
  pattern: $_LOGGER.LogInformation($_MESSAGE)
```

### Metavariable limits

Metavariables only work when they occupy the full AST node slot.

These do not work reliably:
- `Log$LEVEL`
- `"value: $X"`
- `prefix_$NAME`

## C# Examples

### Find methods containing `await`

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

### Find `LogError` calls inside `catch`

```yaml
id: log-error-in-catch
language: csharp
rule:
  pattern: logger.LogError($EXCEPTION, $MESSAGE)
  inside:
    kind: catch_clause
    stopBy: end
```

### Find properties with expression bodies

```yaml
id: expression-bodied-properties
language: csharp
rule:
  pattern:
    context: |
      class C
      {
          string Name => GetName();
      }
    selector: property_declaration
```

### Find methods annotated with `[Test]`

```yaml
id: test-methods
language: csharp
rule:
  all:
    - kind: method_declaration
    - has:
        kind: attribute_list
        stopBy: end
```

If you need the specific attribute name, test the concrete pattern with real code and add a narrower `has` sub-rule from the inspected AST.

### Find `using var` declarations

```yaml
id: using-var-declarations
language: csharp
rule:
  pattern: using var $NAME = $VALUE;
```

## Debugging

### Inspect how a pattern parses

```bash
ast-grep run --pattern 'class C { async Task LoadAsync() { await FetchAsync(); } }' \
  --lang csharp \
  --debug-query=pattern
```

### Check a rule against a sample file

```bash
ast-grep scan --rule rule.yml /path/to/sample.cs
```

### Search the repo once the rule is stable

```bash
ast-grep scan --rule rule.yml src
```

## Practical Advice

1. Start with `pattern`, not `kind`.
2. If declarations do not match, move to `pattern.context` plus `selector`.
3. Add `stopBy: end` for `has` and `inside` unless you know you need a tighter boundary.
4. Use `--debug-query=pattern` before trying to memorize C# node kinds.
5. Escalate to Roslyn when the task depends on symbols or types.
