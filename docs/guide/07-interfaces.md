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
