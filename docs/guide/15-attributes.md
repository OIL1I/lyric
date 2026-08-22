# Attributes

An attribute attaches data to a declaration — data the program never reads, but a tool outside it
can. A game engine finds the functions it should call each frame; a mod loader reads a module's
name and version before running anything; an editor shows what a script declares.

```lyr
import std.core { OnType, OnFunction };

pub struct Component :: [OnType] { }
pub struct System :: [OnFunction] { order: int = 0 }

@Component
pub struct Health { value: int, max: int }

@System { order = 10 }
pub fn damageTick(dt: float): void { }
```

An attribute **describes; it does nothing**. `damageTick` runs exactly as it would without the
`@System` line — no wrapping, no renaming, no changed behaviour. What a host makes of the row is
the host's business.

## An attribute is a struct

There is no separate attribute declaration form. `System` above is an ordinary struct: it can be
constructed, passed around and read like any other. What makes it usable *as* an attribute is the
marker interface it declares:

| Marker | Allows `@Name` before |
|---|---|
| `OnFunction` | a top-level function |
| `OnType` | a struct, class or enum |
| `OnModule` | the module header |

All three live in `std.core` and are empty — nothing is dispatched through them, so they cost
nothing. Conformance decides, not the name: a struct that never declares `:: [OnFunction]` cannot
sit on a function, however plausible it sounds. That is the same nominal rule the operators
follow, and it exists for the same reason — nothing becomes an attribute by accident.

A struct may declare more than one marker and then sits on both kinds of target:

```lyr
import std.core { OnType, OnFunction };

pub struct Tag :: [OnFunction, OnType] { }
```

## Arguments are values at compile time

The block after the name is the struct initializer, restricted to what can be written into the
compiled module — numbers (a sign is allowed), strings, chars and bools:

```lyr
import std.core { OnFunction };

pub struct Retry :: [OnFunction] { limit: int = 3, label: string = "" }

@Retry { limit = -1 }
pub fn fetch(): void { }
```

The restriction is not taste: the values are written into the compiled module, and what stands in
a file has to be a value at compile time. `limit = 1 + 2` is rejected, and so is `null`.

Since v2.4 an argument may also NAME its value, as long as the name is a `let` whose initializer
is itself such a literal:

```lyr
import std.core { OnFunction };

pub struct On :: [OnFunction] { event: string }

pub let CLEARED = "tetris.cleared";

@On { event = CLEARED }
pub fn onCleared(): void { }

fn main(): int { return 0; }
```

That is what lets a program publish a vocabulary instead of repeating raw strings: a module
exports its event names, whoever handles them imports the module, and a typo is `unknown
identifier` at compile time rather than a handler nobody ever calls. The name may be imported
selectively or written module-qualified, it may point at another such `let`, and a `static let`
on a type works the same way.

Since v2.10 an argument may also be a **unit variant of an enum**, and for a vocabulary that is
the form to reach for:

```lyr
import std.core { OnFunction };

pub enum Layout { Packed, Separate }

pub struct Saved :: [OnFunction] { layout: Layout = Layout.Packed }

@Saved { layout = Layout.Separate }
pub fn store(): void { }

fn main(): int { return 0; }
```

`Layout.Seperate` is then a compile error rather than a row nobody matches — the type system
checks the spelling, which a string could never do. A host reads the variant's name and its tag:
the compiled module carries the tag, and the enum's own entry carries the names, so nothing is
written twice.

A variant WITH a payload is refused, and the message says why: a row holds one value per field,
and a payload is values of its own. `Shape.Circle(1.0)` has nowhere to put its `1.0`.

Two edges worth knowing in advance. A name is resolved, not COMPUTED: `let LIMIT = 1 + 2;` stays
rejected, because the value would have to be worked out and there is no constant folding to work
it out with — the value has to stand in the source. And the named form is slightly stricter than
the written one: `@Retry { limit = 5 }` adapts the literal to an `int32` field, while
`let LIMIT = 5;` is already an `int` and does not adapt, so a narrow field wants a narrow binding
(`let LIMIT: int32 = 5;`).

A field you do not write carries its default — `label` above is `""` without anyone writing it.
That only works when the default itself is such a value — a literal, or a name for one; a field
with a computed default and no written value is an error at the use site, because there would be
nothing to write into the module.

Two more rules, both diagnosed where they happen: the same attribute may not sit on one target
twice, and neither a generic attribute struct nor a generic target is allowed — the compiled
module holds one row, and one row cannot stand for every instance.

## The module header

An attribute before `module` describes the file as a whole:

```lyr
@Plugin { name = "mymod", api = 2 }
module mymod;

import std.core { OnModule };

pub struct Plugin :: [OnModule] { name: string, api: int }
```

This is the row a host reads **before** deciding to run anything — identity and required API
version for mods and downloaded scripts. A file without a `module` header cannot carry module
attributes; an attribute at the top of such a file belongs to the first declaration.

## What ends up in the module, and who reads it

The compiled `.lyrbc` carries one row per attribute: the target, the attribute type and one value
per field. For every type a row references, the module also carries the **field names** — which is
worth pausing on, because otherwise field names never appear in compiled code. It is what lets an
engine read

```lyr
import std.core { OnType };

pub struct Component :: [OnType] { }

@Component
pub struct Health { value: int, max: int }
```

and learn not just that `Health` has two `int` fields, but which is `value` and which is `max` —
enough to allocate its own storage for a type the script declared.

A function carrying an attribute is never removed as dead code, even if nothing in the script
calls it: the row is a promise that the function exists, and the host is a caller the compiler
cannot see.

How a C# host asks these questions — enumerating rows, calling the functions they name, reading a
type's shape — is the embedding chapter's topic: see
[Attributes: what a script says about itself](14-embedding.md#attributes-what-a-script-says-about-itself).

## Where the vocabulary comes from

The markers make a struct *placeable*; they say nothing about which attributes exist. That
vocabulary belongs to whoever reads the rows — an engine ships `Component` and `System` in its
SDK the same way it ships its native functions, and a script imports them:

```lyr
import engine.ecs { System };

@System { order = 10 }
pub fn hunt(dt: float): void { }
```

Attribute names are unqualified in the compiled module: `System`, not `engine.ecs.System`. An SDK
owns its attribute names the way it owns its native names.

## The one attribute the compiler reads

Exactly one attribute means something to the compiler itself: `@Deprecated`, from `std.core`.
It marks a function, a type or a module, and every use of the marked thing warns
(`LYR-SEM0076`) with the message naming the way forward:

```lyr
import std.core { Deprecated };

@Deprecated { message = "use renew" }
pub fn old(): int {
    return 1;
}

pub fn renew(): int {
    return 2;
}

fn main(): int {
    return renew();
}
```

It changes diagnostics and nothing else — a program that ignores the warning compiles to the
same module, and a deprecated function may keep calling itself and its deprecated siblings
without the compiler nagging the one place allowed not to care.

### Saying when it goes

A deprecation makes two promises: use something else, and this will disappear. Since 2.13 the
second one can be written down where the compiler can keep it:

```lyr
import std.core { Deprecated };

@Deprecated { message = "use renew", until = "99.0" }
pub fn old(): int {
    return 1;
}

fn main(): int {
    return 0;
}
```

`until` names the version that **removes** the declaration, and building with a toolchain that
has reached it is an error (`LYR-SEM0081`) — `until = "3.5"` fails at 3.5, not one release later.
(The snippet above says `99.0` for the obvious reason: every snippet in this guide is compiled by
the test suite, and one promising 3.5 would stop the build the day 3.5 arrives.)
The check sits at the declaration, so it fires whether or not anything still calls the function:
a form kept past its date is wrong either way, and dead code would never trip a use-site warning.

Leave `until` out for the ordinary policy — warn now, remove at the next major. Write it when the
date is a commitment somebody is entitled to rely on.

Since 2.1 the same attribute — and only it — may sit on a MEMBER: a method, a field or a
`static let` of a struct, class or enum, and on an extend method. Every other attribute
there is refused, because the compiled module has no metadata rows for members and
`@Deprecated` is the one attribute that needs none. Interface members carry no attributes at
all: deprecating an abstract member would raise questions about implementations nobody has
answered yet.

```lyr
import std.core { Deprecated };

class Counter {
    n: int,

    @Deprecated { message = "use tick()" }
    pub mut fn bump(): void {
        this.n = this.n + 1;
    }

    pub mut fn tick(): void {
        this.n = this.n + 1;
    }
}

fn main(): int {
    var c = Counter { n = 0 };
    c.tick();
    return c.n;
}
```

The compiler-read set is part of the language's contract, which is why it grows by decision
rather than by convention: `@Deprecated` is in it, `@Inline` and its kind are not. Everything
else stays inert — an attribute the compiler does not know describes, and does nothing.
