# Lyric for VS Code

Syntax highlighting and a run command for [Lyric](https://github.com/OIL1I/lyric).

## What it does

- **Highlighting** for `.lyr` files — keywords, types, strings with interpolation, nested block
  comments, all numeric literal forms.
- **Run** the active file with `Ctrl+F5`, the play button in the editor title bar, or the
  command palette (`Lyric: Run File`).

## What it does not do

No diagnostics, no completion, no go-to-definition. Those need a language server that runs the
compiler incrementally and keeps results between keystrokes — a separate project.

## Setup

The extension calls `lyric` from your `PATH`. If the toolchain lives elsewhere, point at it:

```json
{
  "lyric.executable": "C:/tools/lyric/lyric.exe"
}
```

It calls the **driver**, not `lyrc` or `lyrvm` — the driver is the one command that compiles and
runs in a single step.

## Installing during development

VS Code loads extensions from `~/.vscode/extensions`. Symlink or copy this directory there and
restart:

```bash
# Linux/macOS
ln -s "$PWD/tooling/vscode-lyric" ~/.vscode/extensions/vscode-lyric

# Windows (PowerShell, as administrator)
New-Item -ItemType SymbolicLink -Path "$env:USERPROFILE\.vscode\extensions\vscode-lyric" -Target "$PWD\tooling\vscode-lyric"
```

There is no build step: the grammar is JSON and the command is a single JavaScript file, both
loaded as they are. Packaging with `vsce` comes when there is something to publish.

## The grammar is tested

`tests/Lyric.Tests.Cli/GrammarTests.cs` compares the keyword list in
`syntaxes/lyric.tmLanguage.json` against the lexer — in **both** directions. A grammar is a second
description of the same language, and two descriptions drift; if Lyric gains a keyword and the
grammar does not, the test fails rather than the editor silently treating it as an identifier.
