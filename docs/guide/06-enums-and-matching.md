# Enums and pattern matching

An enum lists alternatives. A variant may carry a payload, either positionally or by name.

```lyr
enum Shape {
    Circle(float),
    Rectangle(float, float),
    Triangle { a: float, b: float, c: float },
    Empty;

    fn corners(): int {
        return match (this) {
            Circle(r)       => 0,
            Rectangle(w, h) => 4,
            Triangle { a, b, c } => 3,
            Empty           => 0,
        };
    }
}

fn main(): int {
    let s = Shape.Rectangle(3.0, 4.0);
    return s.corners();
}
```

The `;` separates the variants from the methods.

## Constructing

```lyr
enum Shape {
    Circle(float),
    Triangle { a: float, b: float, c: float },
    Empty,
}

fn main(): int {
    let a = Shape.Circle(2.0);
    let b = Shape.Triangle { a = 3.0, b = 4.0, c = 5.0 };
    let c = Shape.Empty;
    return 0;
}
```

## Matching

`match` is exhaustive: every variant must be covered, or the last arm must be `_`.

```lyr
enum Signal { Red, Yellow, Green, }

fn wait(s: Signal): int {
    return match (s) {
        Red    => 60,
        Yellow => 5,
        Green  => 0,
    };
}

fn main(): int { return wait(Signal.Yellow); }
```

Arms may bind payloads, test literals, cover ranges, combine alternatives with `|`, and carry a
guard:

```lyr
fn classify(n: int): int {
    return match (n) {
        0          => 0,
        1 | 2 | 3  => 1,
        4..=9      => 2,
        x if x < 0 => -1,
        _          => 3,
    };
}

fn main(): int { return classify(7); }
```

An arm whose body is an expression ends with `,`. An arm whose body is a block may omit it, but a
block arm must `return` or `throw` — it contributes no value.

## Generic enums

The type arguments belong to the enum and precede the variant:

```lyr
enum Result<T> {
    Ok(T),
    Failed { reason: string },
}

fn main(): int {
    let a = Result<int>.Ok(5);
    let b = Result<int>.Failed { reason = "empty" };

    let c: Result<int> = Result.Ok(7);      // taken from the annotation

    return match (a) {
        Ok(v) => v,
        Failed { reason } => 0,
    };
}
```

Where the expected type is known, the arguments may be omitted. In an argument position they
cannot; write them.
