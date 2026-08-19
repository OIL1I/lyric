# Functions

```lyr
fn add(a: int, b: int): int {
    return a + b;
}

fn main(): int {
    return add(2, 3);
}
```

A function without a return type returns `void`. The return type is written after the parameters.

## Default and variadic parameters

A parameter may have a default. `params` on the last parameter collects the rest into an array.

```lyr
import std.io.console { println };

fn greet(name: string, greeting: string = "Hello"): string {
    return greeting + ", " + name;
}

fn total(params values: int[]): int {
    var sum = 0;
    for (v in values) { sum = sum + v; }
    return sum;
}

fn main(): int {
    println(greet("Ada"));
    println(greet("Ada", "Good evening"));
    return total(1, 2, 3);
}
```

A finished array may be passed to a variadic parameter as a whole.

## Functions as values

A function name used without parentheses is a value of type `fn(...) -> R`.

```lyr
fn double(n: int): int { return n * 2; }

fn apply(f: fn(int) -> int, value: int): int {
    return f(value);
}

fn main(): int {
    return apply(double, 21);
}
```

## Lambdas

A lambda is written with `=>`. Its body is an expression or a block.

```lyr
fn apply(f: fn(int) -> int, value: int): int {
    return f(value);
}

fn main(): int {
    let triple = (n: int) => n * 3;
    let describe = (n: int): int => { return n + 1; };

    return apply(triple, 3) + apply(describe, 5);
}
```

A lambda captures the variables it uses. A block-bodied lambda infers its return type from its
`return` statements when neither an annotation nor a context provides one — the returns must
agree, the same rule match arms follow.

## Static methods

A type can carry functions that need no instance. They are called through the type.

```lyr
struct Point {
    x: int,
    y: int,

    static fn origin(): Point { return Point { x = 0, y = 0 }; }
    static let ZERO: int = 0;
}

fn main(): int {
    let p = Point.origin();
    return p.x + Point.ZERO;
}
```

On a generic type the type arguments belong to the type:

```lyr
struct Pair<T> {
    a: T,
    b: T,

    static fn both(value: T): Pair<T> { return Pair<T> { a = value, b = value }; }
}

fn main(): int {
    let p = Pair<int>.both(4);
    return p.a + p.b;
}
```
