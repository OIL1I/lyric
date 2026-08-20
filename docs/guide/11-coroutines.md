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
uses an infinite coroutine and stops itself — or pulls with `next()`:

```lyr
import std.io.console { println };

fn three(): Coroutine<int> {
    yield 10;
    yield 20;
    yield 30;
}

fn main(): int {
    let co = three();
    var sum = 0;
    var live = true;
    while (live) {
        let v = co.next();
        if (v == null) {
            live = false;
        } else {
            sum += v;
        }
    }
    println(f"sum: {sum}");
    return sum;
}
```

`co.next()` is the safe form of the same pull: it advances the coroutine exactly like `resume`
and answers `?T` — the value, or `null` once the body has run to its end. After the end it stays
`null` on every further call; `resume` on the same coroutine still panics, because leniency
belongs to the call, not to the state. The name and shape are `Iterator<T>.next()`'s on purpose.

Two yield types change the answer's form, for the same reason: a `Coroutine<void>` has no value
to wrap, so its `next()` returns `bool` — did it advance? — and `while (p.next()) { }` drives it
to the end. A `Coroutine<?T>` refuses `next()` outright (`LYR-SEM0080`): a `null` there would
mean both "yielded null" and "done", so such a coroutine is driven with `resume` and a protocol
of its own.

A coroutine may also end itself early with a bare `return;` — the next pull is then the panic or
the `null`, exactly as if the body had run through.

A coroutine is an ordinary value: it can be a parameter, a local, a field of a class or struct,
or a type argument — a driver that steps a stored `List<Coroutine<float>>` every frame holds
them like anything else. Copying the value copies a reference to the same suspended state; two
holders drive one coroutine.

Send values (`resume c, v`) do not exist.
