# AGENTS.md

Operating rules for any agent working in this repository. `CLAUDE.md` is a symlink to this file — one source of truth under two names.

**This file is law on how Kairos code is written.** It is complete for the rules that bind every change. Rules that bind only part of the tree live one hop away and load when you touch that part. Product truth — who this serves, what is in scope, what has been rejected — is `PRODUCT.md`.

## Read order

| Read | When |
|---|---|
| this file | always; you already have it |
| `backend/CLAUDE.md` | touching any `.cs` |
| `frontend/CLAUDE.md` | touching any `.ts` or `.tsx` |
| `.agents/invariants.md` | touching `Scheduling/` or the availability write path |
| `.agents/workflow.md` | starting a task, opening a pull request, or editing a dependency |
| `.agents/rules.md` | you need the numbered map of all 21 rules |
| `PRODUCT.md` | you need product truth |
| `docs/ui/DESIGN.md` | changing anything visual |
| `docs/running-locally.md` | you need the app running |

Rules are cited by number in plans and reviews. **A number is an interface: never reused, never renumbered.** `.agents/rules.md` maps every number to the file that holds its text. A plan says "all 21 apply" and names only the ones it strains.

## The rules that bind every change

**2. Respect each language's conventions.** C# is PascalCase; test methods are `Method_Scenario_Expectation`, every segment capitalised. No `lowercase_words_with_underscores` — that is Python wearing a `[Fact]`. TypeScript is camelCase for values, PascalCase for components and types. SQL keywords uppercase. Follow the language, not a house dialect.

**3. One public type per file**, named after the file — as the default, not as a religion. Two test classes in one file is the same mistake as two entities in one file. The one exception: a vertical slice's own request and response records live in the slice file, because they exist only for that endpoint. A type used outside the file that defines it gets its own file.

**4. The best code is the code that was never written.** Before adding a type, a column, an abstraction or a package, try deleting the requirement instead. Two applications already in the codebase: `Booking` carries one identifier rather than a key plus a separate public id, and reschedule mutates the row rather than cancelling and re-inserting. If a task's diff only adds, look again.

**5. Keep it simple.** No nested ternaries, no clever LINQ chains that need a second read, no reflection where a switch works. Short is good; unreadable is not. Readability wins every tie.

**10. `var`, not the type name.** C# locals are `var slots = Generate(...)`, never `IReadOnlyList<Instant> slots = Generate(...)` — the type is on the right-hand side or one hover away, and repeating it makes a rename a two-place edit. `.editorconfig` sets all three `csharp_style_var_*` preferences to `true:error`, so `dotnet format --verify-no-changes` enforces it. Exceptions are where the right-hand side cannot state the type: a collection expression (`BusyBlock[] busy = [...]`), a `default`, a lambda, or a literal whose width matters. Parameters and fields are unaffected.

**12. No magic numbers.** Not in production code, **not in test data**. A literal that carries meaning gets a named constant, and seeded defaults live in one place rather than being retyped wherever a test needs them. Minute bounds, ISO day bounds and breakpoints already exist as constants — reuse them.

**16. A comment earns its place the way a test does.** Comment the non-obvious — an algorithm, a decision that looks wrong until explained, a constraint the code cannot state itself. Delete anything that restates the line below it, narrates what the reader can see, or repeats what the name just said. A comment nobody needs is a comment nobody reads, and unlike a wrong test it rots silently, because nothing ever fails to tell you it has gone stale. Binds a plan's code samples as much as the code they become — samples are read as house style and get copied.

**18. No AI attribution.** Not in a commit message, not in a pull request body, no `Co-Authored-By` trailer, ever. `main` is protected against everyone, admins included.
