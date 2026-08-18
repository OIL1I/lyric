# Lyric for JetBrains IDEs

The thin plugin: it recognises `.lyr`, renders the TextMate grammar the VS Code extension ships,
and starts `lyrls`. Everything language-aware — diagnostics across the project, completion, the
jump, references in both directions, rename, semantic highlighting, signature help, folding,
inlay hints — is the IDE's own LSP integration consuming the same server every other editor
talks to. There is deliberately **no PSI implementation**: that would be a second frontend in
Kotlin with a permanent lag behind the grammar, which is the kind of parallel mechanism this
project does not build.

## Requirements

- A **commercial** JetBrains IDE, **2026.1 or newer** — the platform's LSP API does not exist in
  the Community editions, and the features this plugin relies on (rename arrived in 2026.1,
  signature help and workspace symbols in 2025.3, folding and inlay hints in 2025.2) set the
  baseline. CLion, IntelliJ IDEA, Rider, GoLand, PyCharm and the rest all qualify.
- The Lyric toolchain. `lyrls` is found on `PATH`, or under the directory named in
  **Settings | Languages & Frameworks | Lyric** — point it at the folder the release archive
  unpacks to.

## Installing

Every release carries a `jetbrains-lyric-<version>.zip`. In the IDE:
**Settings | Plugins | ⚙ | Install Plugin from Disk…** and pick the zip. The plugin is not on
the Marketplace; the release asset is the distribution.

## Building

```bash
./gradlew buildPlugin
```

The zip lands in `build/distributions/`. The build takes the TextMate grammar from
`../vscode-lyric` at packaging time — the grammar has one home, and the test suite pins that
home against the lexer.

## The manual checklist

The plugin's structure is verified by the build (`verifyPluginStructure`); what an IDE does with
it is not — headless IDE tests cost more harness than this plugin has code. After a change, in a
2026.1+ IDE with the toolchain on PATH:

1. Install the zip, restart, open a folder with a `lyric.json`.
2. Open a `.lyr` file — highlighting appears (TextMate), and after a moment the squiggles of a
   deliberate error (server).
3. Break a `pub fn` another file imports — the error appears in the importing file too.
4. Hover a stdlib call — signature plus documentation.
5. `Ctrl+B` on an imported name — lands in the declaring file, on the name.
6. `Shift+F6` on a declared function — every use and the import clause follow; renaming
   `println` is refused with the reason.
7. Type `(` after a function name — the signature popup names the declared parameters.
8. Collapse a function via the gutter; the closing brace stays visible.
9. An unannotated `let` shows its inferred type inline.
