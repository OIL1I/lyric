# Debugging

The toolchain ships a debugger: `lyrdbg`, a Debug Adapter Protocol server an editor launches for
you. In VS Code with the Lyric extension, opening a `.lyr` file and pressing F5 is the whole
setup — breakpoints in the gutter, step over/in/out, the call stack with source lines, and the
variables panel with your locals and globals under their names.

A `launch.json` is only needed to pass arguments or to pin a file:

```json
{
    "type": "lyric",
    "request": "launch",
    "name": "Debug main.lyr",
    "program": "${file}",
    "args": [],
    "stopOnEntry": false
}
```

`program` may be a `.lyr` source or an already-built `.lyrbc`. A source file is compiled by the
debugger itself, in the **debug shape**: with the source map, with debug info, and with the
optimizations off — an inlined function has no frame to show, and a debugger that shows you the
optimizer's world instead of your program's is lying politely. The compile costs tens of
milliseconds; what runs is always the file in the editor.

## What the panels show

The **call stack** is the real frame stack, innermost first, each frame with its function and
source line. The **variables panel** shows the locals of the selected frame — parameters first,
then your bindings — and a Globals scope for module-level `let`s. Structured values expand:

```lyr
struct Vec2 { x: float, y: float }

enum Shape {
    Dot,
    Circle(float),
}

fn main(): int {
    let v = Vec2 { x = 1.0, y = 2.0 };   // expands into x and y
    let s = Shape.Circle(3.5);           // shows 'Shape.Circle', expands into the payload
    let xs = [10, 20, 30];               // shows 'int[3]', expands by index
    return 0;
}
```

Hovering a name evaluates it, and the debug console answers dotted paths: `v.x`, `player.pos.y`.
That is the whole evaluate story — **names, not expressions**. `v.x + 1` is not a thing the
debugger computes: an expression evaluator would be a second compiler running against a paused
frame, and half of one would answer wrongly.

## Breakpoints and stepping

A breakpoint stops **before** the line runs: the line's own bindings are not assigned yet, its
inputs are. A breakpoint on a blank line or a declaration slides down to the next line that
carries code and says so in the gutter; below the last executable line it stays unverified.
Stepping is line-granular — one step is one source line, however many instructions it lowers to.
Inside a loop, a breakpoint on the body hits every pass.

What the program prints while running appears in the debug console — stdout and stderr both,
labeled. A panic ends the session the way it ends a run: the message and backtrace land on
stderr, the exit code is 101.

## The machinery, briefly

Debugging works because the compiler writes two strippable sections into every module by
default: the source map (byte offsets to lines, since 1.0.1) and, since 2.3, **debug info** —
the names of local and global slots, plus field names for every named type. `lyrc build
--no-debug-info` strips the names, exactly like `--no-source-map` strips the lines; the program
is byte-for-byte the same otherwise, and a debugger attached to a stripped module falls back to
slot indices. `lyrvm info` tells you which sections a module carries.

## Debugging a script inside a host

A game has no `main`. Its entry points are the functions a host calls once per frame, so the shape
above — launch a program, watch it run — never applies to it. Since 2.7 the controller attaches to
a single call instead:

```csharp
var controller = DebugController.Create(program);
controller.SetBreakpoints("held.lyr", [129]);

// in the game loop, on the game's own thread:
program.Invoke(update, controller, LyrValue.FromF64(dt));
```

The call runs on the caller's thread, and a breakpoint parks that thread until a resume command
arrives — so the commands have to come from another one, which is what a DAP service attached to
the running game provides. The same arrangement `Start` makes, with the roles swapped: there the
program gets a thread of its own and the caller commands it; here the caller IS the program's
thread.

The controller survives across calls: breakpoints, and the stops they produce, hold for every
invocation it is passed to, and a call into a function nothing breaks on returns without stopping.
What never arrives is an `Exited` event — nothing ended, the host simply stopped calling — so the
event stream stays open, which is the honest answer while a game is still running.

There is no overload taking a debugger and a budget together: a session parked at a breakpoint
would spend a budget on standing still.

### Letting an editor in

The commands can come from an editor rather than from your own code: `DapServer` has a second
constructor that serves a controller you already hold, and the client sends `attach` where it
would otherwise send `launch`.

```csharp
var controller = DebugController.Create(program);

// a socket your host accepted, or any pair of streams
var adapter = new DapServer(input, output, controller, scriptDirectory);
_ = adapter.RunAsync();
```

Everything after `attach` is the protocol as it always was — breakpoints, stepping, stack,
variables, evaluate. What differs is the two ends of a session:

- **Nothing is compiled or started.** The program is already running; a breakpoint set here binds
  against it immediately and takes effect at the next instruction that reaches the line.
- **Disconnecting gives the program back.** `DebugController.Detach()` runs: breakpoints go, a
  parked thread is released, and the event stream ends. Without it a game whose editor crashed
  would stand at its breakpoint for good, and the breakpoints nobody reads any more would park it
  again on the next frame. A detached controller is spent — attaching again means a new one.

One server per controller, which is also the answer to a question a host with several scripts
would otherwise have to invent: which program a `setBreakpoints` is about is decided by the
connection it arrived on.

The debuggee's output does not travel as output events here. Your host owns the program's writers
and already has somewhere to put them.

What this does not do is keep the window drawing. The parked thread is the one the game runs on,
so the picture stands still until a resume arrives. Drawing through a breakpoint would need an
interpreter that can return to its host mid-instruction and be resumed later; this one keeps its
frame stack on the CLR stack, and that is a different machine.

Three limits, stated rather than discovered:

- **The global initializer runs before the debugger attaches.** A breakpoint in a module-level
  `let`'s initializer expression does not hit; the values are simply there, in the Globals
  scope, when the first line of `main` does.
- **Standard-library lines are not steppable.** Their files are recorded by bare name, not by
  path, so a step into `List.push` runs through it and stops on your next line.
- **Debugging optimized bytecode shows the optimizer's world.** A prebuilt `.lyrbc` from
  `lyrc build` has functions inlined away and structs scattered into scalars; frames and
  variables show what actually exists. Debug the source instead — the adapter's own compile is
  the honest shape.
