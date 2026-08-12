# Structs and classes

Both group named fields. They differ in what a binding holds.

- A **struct** is a value. Assigning it copies.
- A **class** is a reference. Assigning it shares.

```lyr
struct Point { x: int, y: int, }

class Counter { value: int, }

fn main(): int {
    var a = Point { x = 1, y = 2 };
    var b = a;
    b.x = 99;                       // a.x is still 1

    let first = Counter { value = 0 };
    let second = first;
    second.value = 5;               // first.value is now 5 as well

    return a.x + first.value;
}
```

A field may have a default, and an initializer may then leave it out:

```lyr
struct Config {
    retries: int = 3,
    verbose: bool = false,
}

fn main(): int {
    let c = Config { verbose = true };
    return c.retries;
}
```

## Methods

Members are separated by `,`. A method that writes to `this` is marked `mut`.

```lyr
class Counter {
    value: int,

    fn get(): int { return this.value; }

    mut fn increment(): void { this.value = this.value + 1; }
}

fn main(): int {
    let c = Counter { value = 0 };
    c.increment();
    c.increment();
    return c.get();
}
```

`mut` on a method of a struct means the receiver is written back to the caller's value.

## Constants on a type

`static let` attaches a constant to a type. `static fn` attaches a function that needs no
receiver.

```lyr
struct Vec2 {
    x: float,
    y: float,

    static let DIMENSIONS: int = 2;

    static fn zero(): Vec2 { return Vec2 { x = 0.0, y = 0.0 }; }

    fn sum(): float { return this.x + this.y; }
}

fn main(): int {
    let v = Vec2.zero();
    return Vec2.DIMENSIONS + v.sum() as int;
}
```

Constants are initialized in declaration order before `main` runs. An initializer may read a
constant declared before it, not one declared after.
