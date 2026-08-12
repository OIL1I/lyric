# Optionals

`?T` is a value that may be absent. It is the only way to express "no value"; there is no null
reference for an ordinary type.

```lyr
fn find(names: string[], wanted: string): ?string {
    for (n in names) {
        if (n == wanted) { return n; }
    }
    return null;
}

fn main(): int {
    let hit = find(["Ada", "Grace"], "Ada");
    return if (hit != null) 1 else 0;
}
```

Optionals do not nest: `??T` does not exist. Wrapping an optional again leaves it at one level.

## Narrowing

A comparison against `null` narrows the value in the branch that follows:

```lyr
import std.io.console { println };

fn main(): int {
    let name: ?string = "Ada";

    if (name != null) {
        println(name);      // 'name' is a string here, not a ?string
    }
    return 0;
}
```

Narrowing applies inside `while` and to the right of `&&`. It ends at a reassignment.

## Operators

| Form | Meaning |
|---|---|
| `a ?? b` | `a` if present, otherwise `b` |
| `a ??= b` | assign `b` only when `a` is absent |
| `x?.member` | member access that yields `null` when `x` is absent |
| `x?.method()` | call that does not happen when `x` is absent |
| `x!` | unwrap; panics when absent |

```lyr
class Profile {
    nickname: ?string,
    fn display(): string { return "profile"; }
}

fn main(): int {
    let p: ?Profile = Profile { nickname = null };

    let shown = p?.display() ?? "nobody";
    let nick = p?.nickname ?? "anonymous";

    var fallback: ?string = null;
    fallback ??= "default";

    return if (shown == "nobody") 0 else 1;
}
```

The right-hand side of `??` and the member behind `?.` are evaluated only when needed. With `?.`
the arguments of the call are not evaluated either when the receiver is absent.

`?.` works on a method, not on a field that holds a function value; read such a field into a
variable first.

## The library side

`std.option` operates on optionals without unwrapping them:

```lyr
import std.option { map, andThen, filter, expect };

fn main(): int {
    let value: ?int = 5;

    let doubled = map(value, (n: int) => n * 2);
    let large = filter(doubled, (n: int) => n > 5);

    return large ?? 0;
}
```
