# Interfaces

An interface names methods. A type declares which interfaces it satisfies with `::`.

```lyr
import std.io.console { println };

interface Drawable {
    fn draw(): string;
}

class Circle :: [Drawable] {
    radius: float,
    fn draw(): string { return "circle"; }
}

class Square :: [Drawable] {
    side: float,
    fn draw(): string { return "square"; }
}

fn show(d: Drawable): void {
    println(d.draw());
}

fn main(): int {
    show(Circle { radius = 1.0 });
    show(Square { side = 2.0 });
    return 0;
}
```

A value used through its interface dispatches dynamically. That is the only dynamic dispatch in
the language.

There is no inheritance and no interface inheritance. A type that needs two contracts lists both:

```lyr
interface Named { fn name(): string; }
interface Sized { fn size(): int; }

class Box :: [Named, Sized] {
    fn name(): string { return "box"; }
    fn size(): int { return 1; }
}

fn main(): int { return Box { }.size(); }
```

## Default methods

An interface method with a body is a default. A type may use it or override it; its own member
wins.

```lyr
import std.io.console { println };

interface Greeter {
    fn name(): string;
    fn greet(): string { return "Hello, " + this.name(); }
}

class Formal :: [Greeter] {
    fn name(): string { return "Ada"; }
    fn greet(): string { return "Good evening, " + this.name(); }
}

class Casual :: [Greeter] {
    fn name(): string { return "Grace"; }
}

fn main(): int {
    println(Formal { }.greet());
    println(Casual { }.greet());
    return 0;
}
```

## Extending a type

`extend` adds methods to a type you did not declare, including a built-in one.

```lyr
import std.io.console { println };

extend int {
    fn doubled(): int { return this * 2; }
}

interface Drawable { fn draw(): string; }

class Plain { }

extend Plain :: [Drawable] {
    fn draw(): string { return "plain"; }
}

fn main(): int {
    println(Plain { }.draw());
    return 21.doubled();
}
```

An `extend` block may also declare that the type satisfies an interface.

## Operators through interfaces

`==` and `!=` work on any type that conforms to `Equatable` from `std.core`. There is no separate
operator declaration: the operator *is* the interface method, written as mathematics. `a == b` calls
`a.equals(b)`, and `a != b` negates it.

```lyr
import std.core { Equatable };
import std.io.console { println };

struct Point :: [Equatable<Point>] {
    x: int,
    y: int,
    fn equals(other: Point): bool {
        return this.x == other.x && this.y == other.y;
    }
}

fn main(): int {
    let a = Point { x = 1, y = 2 };
    let b = Point { x = 1, y = 2 };
    if (a == b) {
        println("same place");
    }
    return 0;
}
```

The conformance is what enables the operator, not the method alone. A type with an `equals` method
that never declares `:: [Equatable<Point>]` keeps its method — but `==` stays an error. That is
deliberate: the conformance names the contract, and without it any method that happens to be called
`equals` would silently become an operator.

Inside generic code the constraint is enough:

```lyr
import std.core { Equatable };

fn contains<T :: [Equatable<T>]>(xs: T[], wanted: T): bool {
    for (x in xs) {
        if (x == wanted) {
            return true;
        }
    }
    return false;
}

fn main(): int {
    return if (contains([1, 2, 3], 2)) 0 else 1;
}
```

Because the built-in types conform through `extend` blocks in `std.core`, the same generic function
serves an `int`, a `string`, and any type of yours that declares the conformance. Monomorphization
turns each use into a direct call — the operator costs no more than the method it stands for.

Optionals stay outside this rule: a `?T` compares against `null`, and the value inside compares
after narrowing.

Ordering works the same way, through `Ordered` and its single `compare` method — negative, zero or
positive, as `strcmp`. All four comparison operators derive from it:

```lyr
import std.core { Ordered };
import std.io.console { println };

struct Version :: [Ordered<Version>] {
    major: int,
    minor: int,
    fn compare(other: Version): int {
        if (this.major != other.major) {
            return if (this.major < other.major) -1 else 1;
        }
        if (this.minor != other.minor) {
            return if (this.minor < other.minor) -1 else 1;
        }
        return 0;
    }
}

fn main(): int {
    let old = Version { major = 1, minor = 4 };
    let new = Version { major = 1, minor = 5 };
    if (old < new) {
        println("upgrade available");
    }
    return 0;
}
```

`string` conforms to `Ordered<string>` in the standard library, so `"apple" < "banana"` works out of
the box — lexicographic over code points, the same order `compare` defines.

Arithmetic follows the same rule, through one interface per operator: `Add`, `Sub`, `Mul` and `Div`
from `std.core`, each with a single method of the same name. The operands are homogeneous — `T` with
`T`, giving `T`:

```lyr
import std.core { Add, Sub };
import std.io.console { println };
import std.string { fromInt };

struct Vec2 :: [Add<Vec2>, Sub<Vec2>] {
    x: int,
    y: int,
    fn add(other: Vec2): Vec2 {
        return Vec2 { x = this.x + other.x, y = this.y + other.y };
    },
    fn sub(other: Vec2): Vec2 {
        return Vec2 { x = this.x - other.x, y = this.y - other.y };
    }
}

fn main(): int {
    let position = Vec2 { x = 10, y = 20 };
    let velocity = Vec2 { x = 1, y = -2 };
    let next = position + velocity;
    println(fromInt(next.x));
    println(fromInt(next.y));
    return 0;
}
```

The built-in numerics and `string` conform in `std.core`, so a generic function constrained on `Add`
serves them and your types alike:

```lyr
import std.core { Add };

fn total<T :: [Add<T>]>(a: T, b: T): T {
    return a + b;
}

fn main(): int {
    return total(40, 2) - 42;
}
```

A mixed form such as `Vec2 * float` does not exist yet: the interfaces are homogeneous by design,
and `%` stays numeric-only. Compound assignment (`v += w`) does not reach through the interfaces
either — write `v = v + w`.
