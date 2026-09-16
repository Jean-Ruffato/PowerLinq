---
name: csharp-formatting
description: Format C# code for readability — indentation, brace style, line-length handling, breaking long parenthesized expressions, method calls, boolean conditions, LINQ chains, and multi-argument LINQ operators, plus the convention calls behind them (`var` vs explicit type, braces on single-statement blocks) and how to triage Roslyn analyzer output against an `.editorconfig`. Use this skill whenever the user asks to format, clean up, reformat, tidy, or improve the readability/layout of C# code, or when producing new C# where line-breaking decisions matter (long method calls, complex conditionals, fluent/LINQ chains, long lambdas). Trigger even if the user just says "make this readable" or "fix the formatting" about C# code; also trigger when reviewing IDE warnings/suggestions, IDExxxx diagnostics, or `dotnet format` output, or when writing or reconciling an `.editorconfig`. Prefer these rules over ad-hoc formatting when writing any non-trivial C#.
---

# C# Code Formatting & Readability

This skill encodes conventions for laying out C# so that structure is visible at a glance. The guiding principle throughout: **break at points of lowest precedence/grouping, put the joining operator at the START of the continuation line, and reach for naming (variables/methods) before chasing the perfect indentation.** Formatting organizes what exists; naming reduces what has to be read.

## When you're applying this

Two modes:
- **Reformatting existing code** the user pasted/uploaded — preserve behavior exactly, change only layout, spacing, and (only if asked) extract-to-name refactors. Never alter logic silently.
- **Writing new C#** — apply these rules as the default so line-breaking is consistent from the start.

**Precedence**, when the three sources disagree:

1. **The user's explicit instruction**, always. A live decision overrides a config file — including one they wrote themselves.
2. **The existing `.editorconfig`** on mechanical rules (indent size, brace placement, trailing commas). Read it first if present.
3. **The surrounding code's established convention**, where the `.editorconfig` is silent. A consistent house style is worth more than any individual rule here.
4. **This skill**, as the default when nothing above settles it.

When you follow a live instruction that contradicts the committed `.editorconfig`, say so and offer to update the file — otherwise you've left the repo asserting one convention while the code follows another, which is exactly what fills the analyzer panel with noise.

## Baseline mechanical rules

- **Indent** 4 spaces per level, no tabs mixed in.
- **Braces**: Allman style (opening brace on its own new line) — the official .NET convention. Whether single-statement `if`/`for` also take braces is a team choice; see "Braces on single-statement blocks" below.
- **Blank lines**: one between logical members/blocks; never multiple consecutive.
- **Line length**: aim for ~100–120 chars. This is a *ceiling that forces* a break, not a *target*. You may break earlier when reading benefits; you're never obliged to break just because you technically could.
- **Naming**: `PascalCase` for types/methods/properties/constants; `camelCase` for locals/params; `_camelCase` for private fields.
- **Usings**: `System.*` first, then the rest, alphabetical within each group; one block at the top of the file. Fully automatable (`dotnet_sort_system_directives_first`) — never a manual decision, and never "append the new one at the bottom".
- **Trailing commas** on multi-line initializers and argument lists — cleaner diffs, easier reordering.

## Braces on single-statement blocks

Allman placement is settled; whether a one-line `if`/`for` body takes braces is not. Both positions are defensible and the choice is a **team agreement**, like the fluent-chain threshold below:

- **Always braces** — the safest default. A body that later grows can't silently fall out of the `if`, and `if/else` chains can't develop a dangling-else. Cost: two lines per guard clause.
- **Braces only when the statement spans multiple lines** — keeps a wall of guard clauses compact and scannable, which is where most single-statement `if`s actually live. Braces kick in as soon as the condition wraps or the body is more than one line, which is where the maintenance risk really is.

```csharp
// One-line guard clause — no braces under the "when multiline" convention
if (call.Method.Name == "Count")
    return new DaxFunctionCall("COUNTROWS", [table]);

// Condition wraps → braces, under either convention
if (expression is MemberExpression { Member: PropertyInfo property, Expression: var owner }
    && owner == parameter)
{
    return property;
}
```

What is **not** optional: the codebase and the `.editorconfig` must agree. `csharp_prefer_braces = true` against a codebase full of brace-less guard clauses produces one warning per guard clause — hundreds of them — and a warning list nobody reads is worse than either convention. Read the existing code first, pick the convention it already follows unless the user says otherwise, and set `csharp_prefer_braces` (`true` / `when_multiline`) to match.

## `var` vs explicit type

The most frequent readability decision in C#, and the one that generates the most analyzer noise when unstated. The rule is **whether the right-hand side makes the type apparent**:

```csharp
var pedidos = new List<Pedido>();           // constructor names the type → var
var pedido = (Pedido)item;                  // cast names the type → var
var total = CalcularTotal(itens);           // type invisible → say it
decimal total = CalcularTotal(itens);
```

`var` when the type is already on the line; the explicit type when the reader would otherwise have to hover the call to know what they're holding. This is the same principle as the rest of the skill — put the information where it's read, don't make the reader reconstruct it.

Two practical notes:

- **Consistency beats the rule.** A file that uses `var` everywhere is easier to read than one that alternates by a rule applied unevenly. When reformatting existing code, follow what the file already does; raise the rule as a suggestion, not a silent rewrite.
- **Make the `.editorconfig` agree with the code.** `csharp_style_var_elsewhere = false` over a codebase that uses `var` throughout means an IDE0008 suggestion on nearly every local — hundreds of entries that train the team to ignore the whole panel. Either conform the code or relax the setting; don't leave them in disagreement.

## The core rule for line breaks: operator-first

When any expression is too long, break it and put the joining token at the **start** of the next line, not trailing the previous one. The eye identifies the relationship (`&&`, `+`, `.`, `?`/`:`) before processing the operand. This single rule covers booleans, arithmetic, fluent chains, and ternaries.

```csharp
if (pedido.Ativo
    && pedido.Valor > limiteMinimo
    && (cliente.Premium || cliente.Antiguidade > 5)
    && !pedido.Cancelado)
{
    // ...
}
```

Keep parenthesized subgroups on one line when they fit — the parentheses already communicate the grouping.

## Breaking an argument list is all-or-nothing

A call or declaration either fits on one line and stays there, or it breaks with **one argument per line**. Breaking after the opening paren and then cramming the arguments onto a single continuation line is the common half-measure — it costs a line without gaining readability. See `references/breaking-long-lines.md` for the before/after.

```csharp
// Wrong
var keyColumns = Translator.TranslateKey(
    _keySelector.Body, _definition.TableName, _localizer);

// Right
var keyColumns = Translator.TranslateKey(
    _keySelector.Body,
    _definition.TableName,
    _localizer);
```

**A statement that spans multiple lines gets a blank line after it.** Once a statement occupies its own block of lines, the next statement needs separation or the two blur into a single wall of indented text — the reader can't tell where one ends and the next begins. The blank line restores the statement boundary that the single-line form got for free.

```csharp
var keyColumns = Translator.TranslateKey(
    _keySelector.Body,
    _definition.TableName,
    _localizer);

var extensions = Translator.TranslateProjection(
    resultSelector,
    _definition.TableName,
    _localizer);

var syntax = QueryBuilder.BuildGroupBy(definition, keyColumns, extensions);
```

This applies to any multi-line statement, not just calls — broken initializers, long ternaries, fluent chains. Single-line statements keep grouping freely; the rule only kicks in once a statement breaks. Members (fields, properties, methods) already get the blank line from the one-blank-line-between-members rule.

## Long parenthesized expressions — by case

The detailed patterns for the tricky cases live in `references/breaking-long-lines.md`. Read it whenever the code involves any of:
- method calls or declarations with many arguments
- complex boolean / conditional expressions
- arithmetic with nested parentheses
- ternary expressions that don't fit
- fluent (`.`-chained) call sequences

Load that file when formatting anything beyond trivial statements — it has the concrete before/after for each.

## LINQ — the highest-value area

LINQ is where formatting decisions bite hardest, and where "the line is long" is usually a signal the operator is doing too much. Full patterns are in `references/linq-formatting.md`. Read it whenever a LINQ query needs formatting. The headlines:

- **Long lambda in `Where`/`Select`**: break the lambda body (operator-first), or better, extract the predicate to a named `static bool` method — it becomes self-documenting and testable.
- **`Select` projecting many properties**: one assignment per line, trailing comma.
- **Multi-argument operators** (`GroupBy`/`Aggregate`/`Join` with multiple selectors): one argument per line, and use **named arguments** (`keySelector:`, `seed:`, `func:`, `resultSelector:`) when the lambdas alone don't make each role obvious.
- **A single operator hard to format** almost always means: split the query into named steps or extract lambdas. Deferred execution is preserved until a `ToList`/`foreach`, so splitting costs nothing at runtime and each line carries one intention.

## Fluent chains and the length threshold

Whether to inline a chain or break it is a **readability judgment, not just a character count**:

- Short chain (2–3 trivial operators) that fits comfortably → keep inline. Forcing a break adds vertical noise with no gain.
- ~4+ operators, or any non-trivial lambda → fluent form (one `.Operator(...)` per line), even if it fits on one line, because each transformation reads in isolation.
- Match the surrounding code — a lone inline chain amid multi-line fluent queries breaks the visual pattern.

```csharp
// Fits, but 4 operators — fluent reads better
var resultado = pedidos
    .Where(p => p.Ativo)
    .OrderBy(p => p.Data)
    .Select(p => p.Id)
    .Distinct()
    .ToList();

// Short and simple — inline is correct
var nomes = pedidos.Where(p => p.Ativo).Select(p => p.Nome).ToList();
```

This 2-vs-4 threshold is the one thing a formatter can't decide for you; if the team wants consistency, the common convention is: inline up to 2–3 trivial operators, fluent multi-line beyond — a team agreement, not an automatable rule.

## Refactors that beat formatting (offer, don't force)

When reformatting existing code, these change behavior-neutrally but improve readability more than layout ever will. **Suggest them explicitly and let the user opt in** — don't apply silently, since they change the code beyond formatting:

- **Guard clauses / early return** instead of deep nested `if`.
- **Extract complex boolean/arithmetic subexpressions** into named `bool`/local variables.
- **Extract a long lambda or predicate** into a named method.
- **Split a heavy LINQ chain** into named intermediate queries.
- **Expression-bodied members** for trivial one-liners; `switch` expressions / pattern matching to flatten nesting.

The IDE proposes most of these on its own — IDE0022/IDE0021 (expression body), IDE0032 (auto-property), IDE0290 (primary constructor), IDE0042 (deconstruction), IDE0028/IDE0305 (collection expressions). Analyzer provenance changes nothing about how to handle them: they are still *offers*. Applying a batch of them alongside a formatting pass produces a diff where real layout changes and behavioral-surface changes are indistinguishable to the reviewer. Keep them in a separate commit.

## Triaging analyzer output

Skill, `.editorconfig`, and the analyzer panel will disagree. Sort every diagnostic into one of three buckets — the bucket, not the severity, decides what to do:

**1. Mechanical and auto-fixable** (using order, indentation, spacing). Run `dotnet format`. There is nothing to discuss and no reason for these to ever appear in a review.

**2. A convention choice** (`var` vs explicit, braces on single statements, naming). Decide **once**, record it in `.editorconfig`, then *conform the code to the decision*. The failure mode here is leaving the config asserting one thing while the codebase does another: the panel fills with hundreds of entries, the team learns the panel is noise, and it stops catching the diagnostics that matter. A suggestion that survives in bulk is not a backlog — it's a decision nobody made.

**3. A deliberate deviation.** Suppress it explicitly in `.editorconfig` with a comment saying why. Leaving it to keep firing is how bucket 2 happens by accident. Example: DI registration extensions conventionally live in `Microsoft.Extensions.DependencyInjection` or the library's own `*.Extensions.DependencyInjection` namespace so `AddX(...)` resolves without an extra `using` — that deliberately violates IDE0130 (namespace must match folder), and the right answer is a scoped suppression, not three permanent warnings.

**Out of scope: performance analyzers.** CA1859, CA1822, CA1862, CA1873 and friends are not style rules and don't belong in a formatting pass. Some actively conflict with good design — CA1859 will tell you to change a return type from `IReadOnlyList<T>` to `List<T>`, or from an interface to a concrete class, trading a deliberate API boundary for a micro-optimization. Evaluate them on their own merits, in their own change, with a reason to care about the hot path.

## Automate the mechanical part

Remind the user that the mechanical rules shouldn't be a recurring manual decision: an `.editorconfig` at the repo root plus `dotnet format` / IDE format-on-save plus Roslyn analyzers settle indentation, brace style, and trailing commas once, versioned with the code, so style stops being debated in PRs. What stays a human call — and where this skill's real value is — is the *extract-to-name* and *inline-vs-break* judgment the formatter won't make.

Writing the `.editorconfig` is the easy half; keeping it honest is the half that gets skipped. A config asserting conventions the code doesn't follow is worse than no config — it produces a permanently non-empty analyzer panel, and a panel that is never empty is a panel nobody reads. After adding or changing rules, measure:

```bash
dotnet format style --verify-no-changes --severity info
dotnet format analyzers --verify-no-changes --severity info
```

The IDE only surfaces diagnostics for open files, so per-file inspection systematically understates the count — run it across the solution. Then aggregate by rule ID, because the shape of the result is the finding: a handful of rules generating the overwhelming majority of diagnostics means unsettled conventions (bucket 2 above), not a codebase in disrepair. Drive that to zero by deciding, not by suppressing wholesale.

## Output expectations

- When reformatting, return the reformatted code and a short note of what changed (layout only vs. suggested refactors).
- Keep suggested refactors separate from pure formatting so the user can accept them independently.
- Preserve the user's existing naming and comments unless asked otherwise.
