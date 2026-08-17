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
