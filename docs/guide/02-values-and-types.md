# Values and types

Every binding has a type. `let` binds a name once, `var` allows reassignment.

```lyr
fn main(): int {
    let name = "Ada";        // string, inferred
    let count: int = 3;      // explicit
    var total = 0;           // reassignable

    total = total + count;
    return total;
}
```

`let` binds the *name*, not the contents. A list behind a `let` can still be modified; the name
just cannot be pointed at something else.

## Primitive types

| Type | Width | Notes |
|---|---|---|
| `int` | 64 bit, signed | alias for `int64` |
| `uint` | 64 bit, unsigned | alias for `uint64` |
| `float` | 64 bit | alias for `float64` |
| `int8` … `int64` | sized | |
| `uint8` … `uint64` | sized | |
| `float32`, `float64` | sized | |
| `bool` | | `true`, `false` |
| `char` | one Unicode code point | `'a'`, `'\u{1F600}'` |
| `string` | immutable UTF-8 | |

Numeric literals take a suffix when the default is not wanted: `7i32`, `9u8`, `1.5f32`.

## Conversion

There is no implicit numeric conversion. `as` converts between numeric types and nothing else:

```lyr
fn main(): int {
    let small: int32 = 7i32;
    let wide: int = small as int;
    let back = wide as int32;
    return back as int;
}
```

## Strings

Strings are immutable. `+` concatenates, `*` repeats.

```lyr
import std.io.console { println };

fn main(): int {
    let greeting = "Hello, " + "world";
    let line = "-" * 20;

    println(greeting);
    println(line);
    return 0;
}
```

An `f`-string interpolates expressions. A format specifier follows a colon:

```lyr
import std.io.console { println };

fn main(): int {
    let pi = 3.14159;
    println(f"pi is roughly {pi:N2}");
    println(f"{1 + 1} and {"nested" + "!"}");
    return 0;
}
```

Write `{{` and `}}` for a literal brace.

## Arrays

`T[]` is a fixed-length array. Its length is part of the value, not of the type.

```lyr
import std.io.console { println };

fn main(): int {
    let numbers = [3, 7, 1];
    let zeros = [0] * 5;
    let both = numbers + zeros;

    println(f"{numbers[0]} of {numbers.length}");
    return both.length;
}
```

Arrays do not grow. `std.collections` has `List<T>` for that.

## Tuples

A tuple groups values of different types. It has no field names; you take it apart by binding.

```lyr
fn divide(a: int, b: int): (int, int) {
    return (a / b, a % b);
}

fn main(): int {
    let (quotient, remainder) = divide(17, 5);
    return quotient + remainder;
}
```
