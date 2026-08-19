# Generics

Functions, structs, classes, enums and interfaces take type parameters.

```lyr
fn first<T>(items: T[]): T {
    return items[0];
}

fn main(): int {
    return first([4, 5, 6]);
}
```

Type arguments are inferred from the arguments where possible. Where the arguments do not
determine them, write them at the call:

```lyr
import std.collections { List };

fn main(): int {
    let numbers = List<int>.empty();
    numbers.push(3);
    return numbers.length();
}
```

## Generic types

```lyr
class Box<T> {
    value: T,

    fn get(): T { return this.value; }
}

fn main(): int {
    let b = Box<int> { value = 9 };
    return b.get();
}
```

A generic type in a value position always takes its arguments explicitly; they are not inferred
from the field values.

## Constraints

A constraint requires the type argument to satisfy interfaces. It is written after `::`.

```lyr
import std.io.console { println };

interface Describable {
    fn describe(): string;
}

class Coin :: [Describable] {
    fn describe(): string { return "coin"; }
}

fn report<T :: [Describable]>(item: T): void {
    println(item.describe());
}

fn main(): int {
    report(Coin { });
    return 0;
}
```

Several constraints stand side by side: `<K :: [Hashable<K>, Equatable<K>]>`. A constraint may
mention its own parameter.

## Monomorphization

Each combination of type arguments becomes its own function and its own layout at compile time.
`Box<int>` and `Box<string>` share no code and no field layout. There is no runtime type
information and no boxing.
