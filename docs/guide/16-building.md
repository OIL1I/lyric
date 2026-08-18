# Building a project

## Starting one

```bash
lyric new myapp          # a program
lyric new mylib --lib    # a module someone imports
```

An app arrives ready to build:

```
myapp/
├── lyric.json      where the modules live
├── build.lyr       what to build
├── .gitignore
└── src/main.lyr
```

```bash
cd myapp && lyric build && lyric run out/myapp.lyrbc
```

A library has no `build.lyr`, because there is nothing to build: it is source another project points
its `sourceRoot` at and imports. Its module file is named after it, so `import mylib` finds
`src/mylib.lyr`.

The name becomes a module name, so it has to be one: letters, digits and `_`, not starting with a
digit. `lyric new` refuses to write into a directory that already holds something.

## Building one

For a single file there is nothing to set up:

```bash
lyric build app.lyr -o app.lyrbc
```

A project with more than one program, or one that generates part of its own source, puts a
`build.lyr` at its root and runs `lyric build` without naming a file.

## The script

```lyr
import std.build { addExecutable };

pub fn build() {
    let app = addExecutable("src/main.lyr", "out/app.lyrbc");
    app.sourceMap(false);

    addExecutable("tools/mktex.lyr", "out/mktex.lyrbc");
}
```

```bash
lyric build          # the working directory
lyric build ../game  # somewhere else
```

Every artifact is compiled on its own, whole, from its entry file. There is no link step and nothing
is shared between two of them but the source on disk.

`lyric build` with a **file** still means "compile this file" and goes to the compiler, as it always
did. Only a directory, or no argument at all, looks for a `build.lyr`.

## Nothing is compiled while the script runs

`addExecutable` collects; the compiles happen once `build` has returned. That is why `sourceMap` on
the next line still applies — and why a file the script writes is finished before anything reads it.

## It is a program, not a list

A build script runs with every capability and has the whole standard library. It may write files,
read them, and start processes:

```lyr
import std.build { addExecutable };
import std.io.file { writeText };

pub fn build() {
    writeText("src/version.lyr", "module version; pub fn text(): string { return \"1.2.0\"; }");

    addExecutable("src/main.lyr", "out/app.lyrbc");
}
```

Relative paths mean the same everywhere in the script: they are resolved against the directory
holding `build.lyr`, not against the directory you started the build from.

**This is code you are running.** `lyric build` in a repository you did not write executes a program
you did not write, exactly as `make` and `cmake` do.

## What the script does not say

Where modules live is a property of the project, not of a build, so it stays in
[`lyric.json`](12-modules.md#saying-where-the-modules-are):

```json
{
  "sourceRoot": "src",
  "nativeRoots": { "engine": "sdk" },
}
```

Both files are read for every artifact. The script never repeats a root, and an editor learns the
layout from `lyric.json` without running anything.

## When something goes wrong

| | |
|---|---|
| No `build.lyr` in the directory | `LYR-CLI0011` |
| The script does not compile | its own diagnostics, with file, line and column |
| No `build` function, or it panics | `LYR-CLI0012` |
| `build` declared nothing to compile | `LYR-CLI0012` — silence would look like success |
| An entry file is not there | named before anything is written |
