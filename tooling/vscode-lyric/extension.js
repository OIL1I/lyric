// The Lyric extension for VS Code.
//
// It does two things: syntax highlighting, declared through package.json and the TextMate grammar
// and needing no code, and a run command. The code here exists for the second one only.
//
// It carries no editor diagnostics, no completion and no go-to-definition; those need a language
// server that runs the compiler incrementally and keeps results between keystrokes.

const vscode = require("vscode");

/** The terminal that runs are sent to: one, not one per invocation. */
let terminal = null;

/**
 * A terminal that is reused for as long as it lives; without the reuse VS Code collects another one
 * on every Ctrl+F5. `exitStatus` detects a terminal the user has closed.
 */
function lyricTerminal() {
    if (terminal === null || terminal.exitStatus !== undefined) {
        terminal = vscode.window.createTerminal("Lyric");
    }
    return terminal;
}

/** Quotes a path for the terminal. Spaces are the normal case, not the exception. */
function quote(path) {
    return `"${path}"`;
}

/**
 * What has to precede the command so the shell reads it as a command rather than as text.
 *
 * PowerShell needs `&`: there `"lyric" run x` is a string literal that gets printed instead of
 * executed. That already hits the default `executable: "lyric"`.
 *
 * Dropping the quoting instead is not an option: `C:\Program Files\lyric\lyric.exe` would then fail
 * on `C:\Program`.
 *
 * `vscode.env.shell` is the default shell, and `createTerminal` without `shellPath` takes exactly
 * that one, so both see the same. For cmd.exe and every POSIX shell a leading `&` is wrong, hence
 * the case distinction rather than always emitting it.
 */
function callPrefix() {
    const shell = (vscode.env.shell || "").toLowerCase();
    return /(^|[\\/])(pwsh|powershell)(\.exe)?$/.test(shell) ? "& " : "";
}

function activate(context) {
    const run = vscode.commands.registerCommand("lyric.run", async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.languageId !== "lyric") {
            vscode.window.showErrorMessage("Lyric: no .lyr file is active.");
            return;
        }

        // Unsaved changes are written first: the compiler reads the file from disk, not from the
        // editor buffer. Without this the previous version runs.
        if (editor.document.isDirty) {
            const saved = await editor.document.save();
            if (!saved) {
                vscode.window.showErrorMessage("Lyric: could not save the file before running.");
                return;
            }
        }

        const executable = vscode.workspace
            .getConfiguration("lyric")
            .get("executable", "lyric");

        // The DRIVER, not lyrc or lyrvm: it is the one command that compiles and runs.
        const shell = lyricTerminal();
        shell.show(true);
        const file = quote(editor.document.fileName);
        shell.sendText(`${callPrefix()}${quote(executable)} run ${file}`);
    });

    context.subscriptions.push(run);
}

function deactivate() {
    // The terminal is ours, so it is disposed here; VS Code would otherwise leave it orphaned when
    // the extension reloads.
    if (terminal !== null) {
        terminal.dispose();
        terminal = null;
    }
}

module.exports = { activate, deactivate };
