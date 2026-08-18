# Embedding

`lyrembed.dll` lets a C# host compile and run Lyric. Reference it and create a VM:

```csharp
using Lyric.Embedding;

var vm = new LangVm(new HostOptions
{
    StdlibRoot = "stdlib",
    Capabilities = Capability.None,
});
```

`HostOptions` decides what scripts may reach: the standard library location, the granted
capabilities, and where their output goes. A module that requires a capability it was not granted
is rejected at load time, before any instruction runs.

## Compiling and running

```csharp
var module = vm.Compile(source, "game");
var exitCode = vm.Run(module);
```

`Compile` takes the source and the module name. The name is not optional; it is what the script's
own declarations are qualified with.

To call individual functions instead of running a `main`, create an instance:

```csharp
var instance = vm.Instantiate(module);

instance.CallVoid("onStart");
var next = instance.Call<long>("onUpdate", 16L);
```

An instance holds the globals. Two instances of the same module do not share state. `Instantiate`
is what a host uses for scripts that have no entry point at all — the common case for embedded
code.

## Reloading

```csharp
var reloaded = instance.Reload();
```

`Reload()` produces a fresh instance from the same module with its globals initialized again. The
old instance stays valid; nothing about it changes.

## Registering functions

```csharp
vm.RegisterFunction("playSound", (string name) => audio.Play(name));
vm.RegisterFunction("random", (long limit) => rng.NextInt64(limit));
```

A script reaches them through the `host` module:

```lyr
import host { playSound, random };

fn main(): int {
    playSound("hit");
    return random(6) as int;
}
```

There is no implicit namespace: without the import the names are unknown.

## An SDK of your own

`RegisterFunction` generates the declaration from the delegate, which is right for a handful of
functions. For an engine with a hundred of them the signature ends up in two places: in the C# call
and in whatever documents the API.

A **native root** is a directory whose modules may declare functions without a body. The declarations
are ordinary `.lyr` files you ship and version:

```text
// sdk/engine/input.lyr
module engine.input;

pub fn keyDown(key: int): bool;

pub fn anyKey(): bool { return keyDown(32) || keyDown(27); }
```

```csharp
var vm = new LangVm(new HostOptions
{
    NativeRoots = new Dictionary<string, string> { ["engine"] = "sdk" },
});

vm.RegisterNative("engine.input.keyDown", (long key) => input.IsDown(key));
```

```lyr
import engine.input { anyKey };

fn main(): int { return if (anyKey()) 1 else 0; }
```

Three things follow from how it is keyed:

- **The root decides, not the file.** The same file outside a native root is a missing body and an
  error. Whether a module may reach into the host follows where it came from, so naming a file well
  enough is not a way in.
- **The segment belongs to the root.** `engine` is taken out of the program's own directory, so which
  file answers an import is never a question of precedence.
- **A declaration needs an implementation under the same qualified name.** `RegisterNative` writes no
  declaration — that is the file's job — and a declaration nobody implements fails when the script is
  instantiated, not at the call site.

A module in a native root may hold ordinary Lyric code beside its declarations; `anyKey` above is
compiled like any other function.

## Value types across the boundary

A native signature may use a `struct` declared **in the same native module**, with scalar and
string fields only. The declaration stays fully typed on the script side; on the wire the struct
is **flattened**:

```text
// sdk/engine/geo.lyr
module engine.geo;

pub struct Vec2 { x: float, y: float }

pub fn setPosition(entity: int, at: Vec2);

pub fn positionOf(entity: int): Vec2;
```

A struct **parameter** crosses as its fields. The host registers exactly the delegate it would
have written for scalar parameters — `setPosition` above binds against `(long, double, double)`:

```csharp
vm.RegisterNative("engine.geo.setPosition",
    (long entity, double x, double y) => world.SetPosition(entity, x, y));
```

A struct **return** comes back through a buffer the runtime owns: the implementation receives
the ordinary arguments plus the buffer's slots and fills one value per field, in field order.
That is the `NativeRegistry` surface a game host uses:

```csharp
natives.RegisterStructReturning("engine.geo.positionOf",
    [TypeTag.I64], [TypeTag.F64, TypeTag.F64],
    (args, result) =>
    {
        var p = world.PositionOf(args[0].AsI64);
        result[0] = LyrValue.FromF64(p.X);
        result[1] = LyrValue.FromF64(p.Y);
    });
```

On the script side nothing special happens — `let p = positionOf(e);` binds an ordinary value
with value semantics, and mutating `p` afterwards changes nothing the host sees. The point of
the arrangement is what it costs: **nothing allocates**. A `Vec2` built fresh and passed in, or
received back in a loop of a hundred thousand iterations, measures 0 bytes per call — the fields
travel as scalars, and the result buffer exists once per program.

Registration checks the layout at load time: a host that fills three fields against a struct the
SDK declares with two is rejected with the import's name in the message, before any instruction
runs.

## Registering types

`RegisterType` exposes a C# class to scripts. Scripts receive such an object and pass it on; they
cannot construct one and cannot read its fields.

```csharp
vm.RegisterType<Player>("Player", t => t
    .Getter("name", (Player p) => p.Name)
    .Getter("health", (Player p) => p.Health)
    .Method("damage", (Player p, long amount) => p.Damage(amount), mutates: true));

vm.RegisterFunction("hero", () => world.Hero);
```

On the script side a host value looks like any other:

```lyr
import host { hero, playSound };

fn main(): int {
    let player = hero();

    if (player.health() > 0) {
        player.damage(10);
        playSound("ouch");
    }
    return player.health() as int;
}
```

A host member is read as a call — `player.name()`, not `player.name`. A host type has no field
layout the script could index into, so every access is a method.

The object travels; it is not copied. The .NET garbage collector keeps it alive as long as a Lyric
value can reach it. There is no release or revocation protocol.

## Attributes: what a script says about itself

An attribute is a struct; where it may sit is the marker interface it declares — `OnModule`,
`OnType` or `OnFunction`, all from `std.core`. An SDK declares the vocabulary, a script uses it,
and the host reads the result:

```lyr
import std.core { OnModule, OnType, OnFunction };

pub struct Plugin :: [OnModule] { name: string, api: int }
pub struct Component :: [OnType] { }
pub struct System :: [OnFunction] { order: int = 0 }

@Component
pub struct Health { value: int, max: int }

@System { order = 10 }
pub fn damageTick(dt: float): void { }
```

The arguments are literals, and a field the script does not write carries its default, so a row is
always complete. An attribute **describes; it does nothing**: a runtime that ignores the rows runs
the program unchanged, and no attribute in this vocabulary means anything to the compiler.

On the host side the rows hang off the compiled module and off an instance, joined and ready to
ask:

```csharp
var module = vm.Compile(source, "game");

foreach (var plugin in module.Attributes.OnModule)
    Console.WriteLine($"{plugin.Value("name")?.Text} wants API {plugin.Value("api")?.AsInt}");

var instance = vm.Instantiate(module);
foreach (var system in instance.Attributes.OnFunctions("System"))
    instance.CallVoid(system, 0.016);   // the use carries the function index; nothing is
                                        // resolved by name again
```

Three details carry the weight:

- **`module.Attributes` works before `Instantiate`.** For foreign bytes — mods, downloaded
  scripts — the module row is how a host decides whether to load at all.
- **A hit is a handle.** `CallVoid(use, …)` calls by the index the row carries, so the per-frame
  path stays the raw one. A typo in the script is now a compile error (`unknown type`), not a
  function nobody finds.
- **An attributed type reports its shape.** `module.Attributes.FieldsOf(use.Target)` yields the
  field names and types of `Health` — the bytecode carries field names exactly for types an
  attribute references, and for nothing else.

Attribute names are unqualified: `System`, not `engine.ecs.System`. An SDK owns its attribute
names the way it owns its native names.

## Errors

A script that fails throws on the host side:

| Exception | Cause |
|---|---|
| `ScriptException` | compilation or a runtime error inside the script |
| `ScriptPanicException` | the script panicked |
| `EmbeddingException` | the host used the API wrongly — an unknown function, a signature mismatch |

These are declared in `Lyric.Embedding`; a host does not reference the runtime assembly to catch
them.
