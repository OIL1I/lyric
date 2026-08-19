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

There is no class inheritance. A type that needs two contracts lists both:

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

## Interface inheritance

An interface may name one **parent**. Conforming to the child implies conforming to the parent:
the implementing type provides the abstract members of the whole chain, inherits its default
methods, and satisfies a constraint on the parent wherever one is required.

```lyr
import std.io.console { println };

interface Named {
    fn name(): string;
}

interface Labeled :: [Named] {
    fn label(): string { return "[" + this.name() + "]"; }
}

struct Tag :: [Labeled] {
    fn name(): string { return "tag"; }
}

fn describe<T :: [Named]>(x: T): string {
    return x.name();
}

fn main(): int {
    let t = Tag { };
    println(t.label());          // the inherited default calls the parent's member
    println(describe(t));        // 'Labeled' satisfies a 'Named' constraint
    let n: Named = t;            // the implied conformance carries the value into the parent
    println(n.name());
    return 0;
}
```

The parent list holds exactly one entry — several requirements side by side are what constraints
are for (`<T :: [A, B]>`). The chain cannot be circular, and a member of the chain cannot be
redeclared: an inherited member keeps its declaring interface.

A value of interface type reaches the members of its whole chain (`Labeled` values answer
`name()`). What it does not do is convert to the parent's type: `Named` in the example is built
from the concrete `Tag`, not from a `Labeled` value. Where a parent-typed value is needed, take
the concrete value through the parent directly.

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

The last operator is `as`. Beyond the numeric casts, which keep their built-in meaning, a cast is a
conversion the operand's type declared through `Into`:

```lyr
import std.core { Into };
import std.io.console { println };
import std.string { fromInt };

struct Fahrenheit { degrees: int, }

struct Celsius :: [Into<Fahrenheit>] {
    degrees: int,
    fn into(): Fahrenheit {
        return Fahrenheit { degrees = this.degrees * 9 / 5 + 32 };
    }
}

fn main(): int {
    let boiling = Celsius { degrees = 100 };
    let f = boiling as Fahrenheit;
    println(fromInt(f.degrees));
    return 0;
}
```

Three boundaries, each deliberate. Conversions are **explicit** — nothing converts on its own, and
`1 as float` never goes through `Into`. A type has **one** conversion target, because `into` is a
member name and a type has one member of a name; the second conversion is an ordinary named method.
And a conversion that can **fail** does not belong here: `Into` returns `T`, not `?T` — parsing a
string into a number is a named function returning an optional.
