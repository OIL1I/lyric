// The Lyric extension for VS Code.
//
// The parts that need no code are declared in package.json: the TextMate grammar, the snippets,
// the problem matcher. The code here is the run command, the build task provider, a status item,
// and a client for `lyrls` — which is where everything language-aware happens.
//
// Nothing about the language is computed here. The client starts the server, forwards the open
// buffers to it and renders what comes back — the compiler is the only thing that decides what
// is wrong with a program, and a second opinion in JavaScript would be a second answer to the
// same question.

const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

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

/** The running language client, or null when diagnostics are off or the server did not start. */
let client = null;

/**
 * The one visible answer to "is the server running". Without it a failed start is a warning
 * toast that disappears, and the user reads the silence as "no diagnostics today" with no way to
 * tell why or to retry. Clicking it restarts.
 */
let statusItem = null;

function showStatus(state, detail) {
    if (statusItem === null) {
        return;
    }

    statusItem.busy = state === "starting";
    statusItem.severity = state === "failed"
        ? vscode.LanguageStatusSeverity.Error
        : vscode.LanguageStatusSeverity.Information;

    switch (state) {
        case "starting":
            statusItem.text = "Lyric: starting";
            statusItem.detail = "the language server is coming up";
            break;
        case "running":
            statusItem.text = detail ? `Lyric ${detail}` : "Lyric";
            statusItem.detail = "language server running";
            break;
        case "failed":
            statusItem.text = "Lyric: server failed";
            statusItem.detail = "diagnostics are off; click to retry";
            break;
        case "off":
            statusItem.text = "Lyric: diagnostics off";
            statusItem.detail = "enable lyric.diagnostics.enable to start the server";
            break;
    }
}

/**
 * Restarts run STRICTLY ONE AFTER ANOTHER.
 *
 * Without the chain a stop can overtake the start it is meant to undo: `start()` resolves only
 * after the initialize handshake, and a `stop()` issued before then rejects the request that is
 * still in flight — the client reports that as "pending response rejected since connection got
 * disposed", which names the symptom and not the cause.
 */
let restarts = Promise.resolve();

/** The restart a burst of setting changes has coming. */
let restartTimer = null;

/**
 * How long the settings must be quiet before the server is restarted.
 *
 * The settings UI fires a change PER KEYSTROKE, so typing a path is a dozen of them. Restarting on
 * each one spawns and kills a process per character, and every one of those kills lands on an
 * unanswered handshake.
 */
const RestartDelayMs = 750;

/**
 * The settings that decide what the server is and whether it runs. `lyric.executable` is among
 * them because it is where the server is looked for when its own setting is empty.
 */
const ServerSettings = [
    "lyric.languageServer",
    "lyric.executable",
    "lyric.diagnostics.enable",
];

function diagnosticsEnabled() {
    return vscode.workspace.getConfiguration("lyric").get("diagnostics.enable", true);
}

/**
 * Where `lyrls` lives.
 *
 * An explicit setting wins. Otherwise it is looked for NEXT TO the configured driver, because the
 * two ship in one directory — that way pointing `lyric.executable` at an installation configures
 * both, which is the case that would otherwise need two settings saying the same thing.
 *
 * The fallback is the bare name, which leaves the lookup to `PATH`.
 */
function serverCommand() {
    const configuration = vscode.workspace.getConfiguration("lyric");

    const configured = (configuration.get("languageServer", "") || "").trim();
    if (configured !== "") {
        return configured;
    }

    const driver = (configuration.get("executable", "lyric") || "lyric").trim();
    if (path.isAbsolute(driver)) {
        const suffix = process.platform === "win32" ? ".exe" : "";
        return path.join(path.dirname(driver), `lyrls${suffix}`);
    }

    return "lyrls";
}

/**
 * Starts the server and connects it to every `.lyr` buffer.
 *
 * `stdio` is the transport: the server owns those two streams for its whole lifetime, which is
 * also why nothing in it may print. A failure to start is reported once, through the client's own
 * output channel, rather than as a modal — an editor that cannot find a toolchain should still be
 * usable for reading code.
 */
async function startClient() {
    const command = serverCommand();

    const server = {
        run: { command, args: [], transport: TransportKind.stdio },
        debug: { command, args: [], transport: TransportKind.stdio },
    };

    const options = {
        documentSelector: [{ scheme: "file", language: "lyric" }],
        outputChannelName: "Lyric Language Server",
    };

    const starting = new LanguageClient("lyric", "Lyric Language Server", server, options);
    client = starting;
    showStatus("starting");

    // Awaited rather than left running: the caller is the restart chain, and it may only proceed to
    // the next stop once this handshake is finished.
    try {
        await starting.start();
        showStatus("running", starting.initializeResult?.serverInfo?.version);
    } catch (error) {
        if (client === starting) {
            client = null;
        }
        showStatus("failed");
        vscode.window.showWarningMessage(
            `Lyric: the language server could not be started (${command}). ` +
            `Diagnostics are off; highlighting and Ctrl+F5 still work. ${error.message}`
        );
    }
}

async function stopClient() {
    const stopping = client;
    client = null;
    if (stopping === null) {
        return;
    }

    try {
        await stopping.stop();
    } catch {
        // A client that never reached the running state rejects instead of stopping. Disposing it
        // releases the process either way, and a failure to shut down cleanly must not block the
        // restart that follows.
        try {
            await stopping.dispose();
        } catch {
            // The process is gone. Nothing left to release.
        }
    }
}

/** Queues one stop-then-start behind whatever is already running. */
function restart() {
    restarts = restarts.then(async () => {
        await stopClient();
        if (diagnosticsEnabled()) {
            await startClient();
        } else {
            showStatus("off");
        }
    });
    return restarts;
}

function scheduleRestart() {
    if (restartTimer !== null) {
        clearTimeout(restartTimer);
    }
    restartTimer = setTimeout(() => {
        restartTimer = null;
        restart();
    }, RestartDelayMs);
}

/**
 * The build task: the project's own build.lyr, run by the driver, with the problem matcher
 * reading the compiler's diagnostics into the Problems panel.
 *
 * One task per workspace folder that carries a lyric.json — a folder without one has no project
 * to build, and offering the task there produces exactly the "no build.lyr found" the user did
 * not ask about.
 */
function buildTask(definition, scope, cwd) {
    const executable = vscode.workspace
        .getConfiguration("lyric")
        .get("executable", "lyric");

    const task = new vscode.Task(
        definition,
        scope,
        "build",
        "lyric",
        new vscode.ShellExecution(executable, ["build"], { cwd }),
        "$lyric"
    );
    task.group = vscode.TaskGroup.Build;
    task.detail = "lyric build — run the project's build.lyr";
    return task;
}

const taskProvider = {
    provideTasks() {
        const tasks = [];
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            const manifest = vscode.Uri.joinPath(folder.uri, "lyric.json").fsPath;
            if (fs.existsSync(manifest)) {
                tasks.push(buildTask({ type: "lyric" }, folder, folder.uri.fsPath));
            }
        }
        return tasks;
    },

    // A tasks.json entry of type "lyric" arrives here with its own definition; the execution is
    // rebuilt around it, honoring an explicit projectDir.
    resolveTask(task) {
        const scope = task.scope ?? vscode.TaskScope.Workspace;
        const folder = typeof scope === "object" ? scope.uri.fsPath : undefined;
        return buildTask(task.definition, scope, task.definition.projectDir ?? folder);
    },
};

function activate(context) {
    statusItem = vscode.languages.createLanguageStatusItem("lyric.server", {
        language: "lyric",
    });
    statusItem.command = { command: "lyric.restartServer", title: "Restart" };
    context.subscriptions.push(statusItem);

    // Through the same chain the setting changes use, so the first start and a restart triggered
    // seconds later cannot overlap.
    restart();

    // A changed path or a toggled switch has to reach a process that was started with the old one.
    // Restarting is the whole of it: the server holds no state that survives its own exit.
    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (!ServerSettings.some((setting) => event.affectsConfiguration(setting))) {
                return;
            }
            scheduleRestart();
        })
    );

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

    // The way out of a hung or updated server that does not cost a window reload.
    context.subscriptions.push(
        vscode.commands.registerCommand("lyric.restartServer", () => restart())
    );

    context.subscriptions.push(vscode.tasks.registerTaskProvider("lyric", taskProvider));
}

function deactivate() {
    if (restartTimer !== null) {
        clearTimeout(restartTimer);
        restartTimer = null;
    }

    // The terminal is ours, so it is disposed here; VS Code would otherwise leave it orphaned when
    // the extension reloads.
    if (terminal !== null) {
        terminal.dispose();
        terminal = null;
    }

    // Behind the chain rather than beside it: a restart may still be starting a client, and
    // stopping one mid-handshake is the very thing the chain exists to prevent.
    //
    // Returned rather than awaited: VS Code waits on the promise, and the server needs that time
    // for the shutdown handshake. Without it the process is killed and the exit code says the
    // client vanished, which is what a crashed editor is supposed to look like.
    return restarts.then(stopClient);
}

module.exports = { activate, deactivate };
