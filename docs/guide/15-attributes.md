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

## Arguments are literals

The block after the name is the struct initializer, restricted to literals — numbers (a sign is
allowed), strings, chars and bools:

```lyr
import std.core { OnFunction };

pub struct Retry :: [OnFunction] { limit: int = 3, label: string = "" }

@Retry { limit = -1 }
pub fn fetch(): void { }
```

The restriction is not taste: the values are written into the compiled module, and what stands in
a file has to be a value at compile time. `limit = 1 + 2` is rejected, and so is `null`.

A field you do not write carries its default — `label` above is `""` without anyone writing it.
That only works when the default itself is a literal; a field with a computed default and no
written value is an error at the use site, because there would be nothing to write into the
module.

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

No attribute means anything to the compiler itself. There is no `@Inline`, no `@Deprecated` — the
set stays open precisely because every attribute is inert.
