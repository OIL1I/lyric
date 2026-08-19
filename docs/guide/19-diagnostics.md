# Diagnostics

The compiler speaks at four severities. An **error** rejects the program. A **warning** means
the program compiles and something about it deserves fixing. An **info** is neutral
information, and a **hint** is a suggestion: the program is fine, a clearer form exists.

Every diagnostic carries a stable code — `LYR-SEM0071` and its kind. A code keeps its meaning
and its severity forever: a retired code is never reused, and no flag turns a warning into
something else. What changes with a flag is at most the exit code.

## Warnings

```lyr
import std.io.console { println };

fn main(): int {
    let unused = 42;
    println("the program runs all the same");
    return 0;
}
```

```
main.lyr:4:9: warning[LYR-SEM0071]: 'unused' is never used
    let unused = 42;
        ^
  note: name it '_' when the value is deliberately unused
```

What warns today: a local binding, loop variable, catch binding or pattern binding that is
never referenced (`LYR-SEM0071`); an imported name nobody in the file uses (`LYR-SEM0072`); a
statement that can never run because control flow always leaves the block before it
(`LYR-SEM0073`); a static extension method called through an instance (`LYR-SEM0074`) — that
form is deprecated and becomes an error in a future major version; and every use of something
marked `@Deprecated` (`LYR-SEM0076`), with the attribute's message naming the way forward.

Naming a binder `_` says the value is deliberately unused, and no warning fires. Parameters
never warn — a signature is often fixed by an interface — and neither does the shorthand field
pattern `Rect { w, h }`, whose names belong to the fields rather than to you.

Warnings never fail a build by themselves. In CI you can make them:

```bash
lyric check src/main.lyr --deny-warnings
```

The warnings keep their severity in the output; one closing error carries the policy into the
exit code, and a denied `build` writes no file.

## Hints

`LYR-SEM0075` is a hint: a `var` through which nothing is ever changed — no reassignment, no
field or element write, no `mut` method call — could be a `let`. A `var` that documents
mutation keeps its `var`; the hint only speaks when the mutability says nothing.

## Notes

A diagnostic may carry notes: other places that belong to the same finding. A duplicate
declaration points back at the first one, a missing interface method points at the member it
fails — in whatever file that member lives — and an unknown name suggests the closest one in
scope when there is exactly one close candidate:

```
main.lyr:3:12: error[LYR-SEM0002]: unknown identifier 'coutn'
    return coutn;
           ^^^^^
  note: did you mean 'count'?
```

In an editor, notes appear under the message and jump to their place on click; unused and
unreachable code renders faded, and the deprecated instance form is struck through.
