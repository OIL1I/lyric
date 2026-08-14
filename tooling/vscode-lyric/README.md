# Lyric for VS Code

Diagnostics, syntax highlighting and a run command for [Lyric](https://github.com/OIL1I/lyric).

## What it does

- **Diagnostics** while you type. The `lyrls` language server compiles the buffer — not the file on
  disk — and reports exactly what `lyric check` reports, at the same positions.
- **Hover** over a name to see what the compiler thinks it is: the binding form and type of a local,
  a parameter's type, a function's signature, what kind of type a type name names. It keeps
  answering while the buffer does not parse, from the last analysis that got through.
- **Highlighting** for `.lyr` files — keywords, types, strings with interpolation, nested block
  comments, all numeric literal forms.
- **Run** the active file with `Ctrl+F5`, the play button in the editor title bar, or the
  command palette (`Lyric: Run File`).

- **Go to definition** (`F12`) on a name, including into the standard library — those files are
  read from disk, so the jump opens the real `.lyr` source.

## What it does not do

No completion and no find-references yet. Hover shows no documentation either, and that is not an
omission to be filled in later: `///` is a token the lexer produces and nothing carries it into the
syntax tree, so there is nothing to show.

A jump lands on the **start** of a declaration rather than on its name. The syntax tree records no
span for a name on its own, and searching the text for it would be a second, weaker way of knowing
where it is.

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
