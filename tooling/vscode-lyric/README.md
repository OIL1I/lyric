# Lyric for VS Code

Diagnostics, syntax highlighting and a run command for [Lyric](https://github.com/OIL1I/lyric).

## What it does

- **Diagnostics** while you type — for the whole project. Under a `lyric.json`, every `.lyr` file
  beneath the source root is compiled as one program: an edit that breaks a file you do not have
  open puts the error in the Problems panel all the same, and a change on disk behind the editor —
  a branch switch, another tool — is picked up through the file watcher. A file outside any
  project is compiled from itself, as before.
- **Hover** over a name to see what the compiler thinks it is: the binding form and type of a
  local, a parameter's type, a function's signature — and the `///` documentation written above
  the declaration. It keeps answering while the buffer does not parse, from the last analysis that
  got through.
- **Go to definition** (`F12`) on a name, including into the standard library — those files are
  read from disk, so the jump opens the real `.lyr` source and selects the name.
- **Find references** (`Shift+F12`) across the project, in both directions: standing on a
  declaration finds the uses in files that import this one.
- **Completion** after `.` and for names in scope, and an **outline** of the file's declarations.
- **Highlighting** for `.lyr` files — keywords, types, strings with interpolation, nested block
  comments, all numeric literal forms.
- **Run** the active file with `Ctrl+F5`, the play button in the editor title bar, or the
  command palette (`Lyric: Run File`).

## What it does not do

No rename yet. A generic call shows the **declared** signature rather than the instantiated one —
the substitution is private to the type checker, and a second one in the server would be a second
answer to what `T` became.

## Setup

The extension calls two binaries from your `PATH`. If the toolchain lives elsewhere, point at the
driver and the server is found beside it:

```json
{
  "lyric.executable": "C:/tools/lyric/lyric.exe"
}
```

`lyric.languageServer` overrides that lookup when the two do not live together, and
`lyric.diagnostics.enable` turns the server off, leaving highlighting and `Ctrl+F5` in place.

The run command calls the **driver**, not `lyrc` or `lyrvm` — the driver is the one command that
compiles and runs in a single step.

## Installing during development

There is now a build step. The extension has exactly one dependency, the language client, and it
has to be present before VS Code loads the extension:

```bash
npm install
```

VS Code loads extensions from `~/.vscode/extensions`. Symlink or copy this directory there and
restart:

```bash
# Linux/macOS
ln -s "$PWD/tooling/vscode-lyric" ~/.vscode/extensions/vscode-lyric
```

```powershell
New-Item -ItemType SymbolicLink -Path "$env:USERPROFILE\.vscode\extensions\vscode-lyric" -Target "$PWD\tooling\vscode-lyric"
```

Nothing is compiled or bundled: the grammar is JSON and the extension is a single JavaScript file,
both loaded as they are. `npm install` only fetches `vscode-languageclient`. Packaging with `vsce`
comes when there is something to publish.

## The grammar is tested

`tests/Lyric.Tests.Cli/GrammarTests.cs` compares the keyword list in
`syntaxes/lyric.tmLanguage.json` against the lexer — in **both** directions. A grammar is a second
description of the same language, and two descriptions drift; if Lyric gains a keyword and the
grammar does not, the test fails rather than the editor silently treating it as an identifier.

The server is tested in `tests/Lyric.Tests.Lsp/`, against the protocol rather than against the
editor: framing, the lifecycle state machine, the URI conversion, and the agreement between what
the server publishes and what the compiler reports for the same text.
