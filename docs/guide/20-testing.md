# Testing

Tests live in a directory of their own — `tests/` beside your `lyric.json`, or wherever its
`testRoot` points — and only `lyric test` ever compiles them. A production build never sees a
test file, so nothing has to be stripped and nothing ships by accident.

A test is a top-level function marked `@Test`, taking nothing and returning nothing. It fails by
panicking, which is what the assertions in `std.test` do:

```lyr
import std.test { Test, assertEq, assertTrue };

fn double(n: int): int {
    return n * 2;
}

@Test
pub fn doubling_doubles(): void {
    assertEq(double(2), 4);
}

@Test
pub fn doubling_keeps_the_sign(): void {
    assertTrue(double(-3) < 0, "the sign must survive");
}
```

```bash
lyric test
```

```
PASS math_tests.doubling_doubles
PASS math_tests.doubling_keeps_the_sign
2 test(s), all passed
```

The runner compiles every `.lyr` under the test root, finds the `@Test` functions through the
module's attribute rows — the same machinery a host uses to find `@System` functions — and runs
each in a **fresh instance**: module state cannot leak between tests, because no two tests share
an instance. A failing assertion names both values (`expected 5, got 4`); a panic fails the test
with its backtrace; the exit code is `0` when everything passed and `1` otherwise, which is all
a CI step needs.

Test files are ordinary programs of your project: they import your modules through the
`sourceRoot` the `lyric.json` declares, and the whole standard library and every capability are
theirs — a test is your own code running on your own machine, the same standing `lyric run`
has.

A project without a `tests/` directory has no tests, and `lyric test` says so and exits `0`. A
`testRoot` your `lyric.json` names explicitly is a promise, though: if the directory is missing,
that is an error.

`assertEq` wants `[Equatable<T>, Display]` — the comparison needs the one, naming both values in
the failure needs the other. Every built-in scalar and `string` satisfy both; for your own types
you declare the conformances, as [chapter 7](07-interfaces.md) shows.
