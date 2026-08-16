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

## Errors

A script that fails throws on the host side:

| Exception | Cause |
|---|---|
| `ScriptException` | compilation or a runtime error inside the script |
| `ScriptPanicException` | the script panicked |
| `EmbeddingException` | the host used the API wrongly — an unknown function, a signature mismatch |

These are declared in `Lyric.Embedding`; a host does not reference the runtime assembly to catch
them.
