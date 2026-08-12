# Modules

A file is a module. The header names it; without a header the name comes from the file name.

```lyr
module app.geometry;

pub struct Point { x: int, y: int, }

pub fn origin(): Point { return Point { x = 0, y = 0 }; }

fn helper(): int { return 1; }
```

`pub` exports. Everything else is visible only inside the module.

## Importing

```lyr
import std.io.console;                     // qualified: console.println(…)
import std.math { sqrt, pi };              // selective
import std.collections as coll;            // alias

fn main(): int {
    console.println(f"{sqrt(4.0)}");
    let list = coll.emptyList<int>();
    return list.length();
}
```

A qualified import binds the last path segment as the name. A selective import binds the listed
names directly. An alias renames the module.

## Module constants

A module-level `let` is a constant. It is initialized once before `main` runs, in declaration
order.

```lyr
let VERSION = "1.0";
let BANNER = "lyric " + VERSION;

fn main(): int {
    return if (BANNER == "lyric 1.0") 0 else 1;
}
```

An initializer may read a constant declared before it, not one after. There is no module-level
`var`.

## Type aliases

```lyr
type Id = int;

fn describe(id: Id): int {
    let copy: Id = id;
    return copy;
}

fn main(): int { return describe(1); }
```

An alias is a name for a type, not a new type: `Id` and `int` are interchangeable.
