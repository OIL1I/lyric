# Coroutines

A function whose body contains `yield` is a coroutine. Its return type is `Coroutine<T>`, where
`T` is what it yields. Calling it does not run the body; it produces a coroutine value.

```lyr
import std.io.console { println };

fn counter(): Coroutine<int> {
    var n = 0;
    while (true) {
        yield n;
        n += 1;
    }
}

fn main(): int {
    let c = counter();

    var sum = 0;
    var i = 0;
    while (i < 5) {
        sum = sum + resume c;
        i += 1;
    }

    println(f"sum: {sum}");
    return sum;
}
```

`resume` runs the coroutine until its next `yield` and produces that value. The state between two
`yield`s — locals and the position in the loop — survives. Without that, the loop above would read
`0` five times.

A coroutine may also be finite. When its body runs to the end, it is exhausted:

```lyr
import std.io.console { println };

fn three(): Coroutine<int> {
    yield 10;
    yield 20;
    yield 30;
}

fn main(): int {
    let t = three();
    let a = resume t;
    let b = resume t;

    println(f"{a}, {b}");
    return a + b;
}
```

`resume` on an exhausted coroutine is a panic. A caller either knows how many values there are, or
uses an infinite coroutine and stops itself.

Send values (`resume c, v`) do not exist.
