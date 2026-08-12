// Die Lyric-Extension für VS Code.
//
// Sie tut genau zwei Dinge: Syntax-Highlighting (deklarativ über package.json und die
// TextMate-Grammatik — dafür ist kein Code nötig) und ein Run-Command. Der Code hier ist
// ausschließlich für das zweite da.
//
// **Was sie bewusst nicht tut**: Diagnosen im Editor, Completion, Go-to-Definition. Das braucht
// einen Sprachserver, der den Compiler inkrementell fährt und Ergebnisse zwischen Tastendrücken
// hält — ein eigenes Projekt, das in der v1.X-Tabelle steht. Eine halbe Lösung wäre schlechter
// als keine: ein Editor, der Fehler manchmal zeigt und manchmal nicht, ist schlimmer als einer,
// der sie nie zeigt.

const vscode = require("vscode");

/** Das Terminal, in dem gelaufen wird — eines, nicht eines pro Aufruf. */
let terminal = null;

/**
 * Ein Terminal, das wiederverwendet wird, solange es lebt.
 *
 * Ohne die Wiederverwendung sammelt VS Code bei jedem Ctrl+F5 ein weiteres an, und nach zehn
 * Läufen sucht man seine Ausgabe in einer Liste. `exitStatus` erkennt ein Terminal, das der
 * Nutzer geschlossen hat.
 */
function lyricTerminal() {
    if (terminal === null || terminal.exitStatus !== undefined) {
        terminal = vscode.window.createTerminal("Lyric");
    }
    return terminal;
}

/** Quotet einen Pfad fürs Terminal. Leerzeichen sind der Normalfall, nicht die Ausnahme. */
function quote(path) {
    return `"${path}"`;
}

/**
 * Was vor den Befehl gehört, damit die Shell ihn als Befehl liest und nicht als Text.
 *
 * **PowerShell braucht `&`.** Dort ist `"lyric" run x` ein String-Literal, das ausgegeben und
 * nicht ausgeführt wird — der Lauf passierte einfach nicht. Das trifft schon den Default
 * `executable: "lyric"`, also jeden, der die Extension unverändert benutzt.
 *
 * **Das Quoting deshalb wegzulassen wäre der falsche Ausweg**: dann läuft `C:\Program
 * Files\lyric\lyric.exe` nicht mehr, und zwar mit einer Fehlermeldung über `C:\Program`. Ein
 * Pfad mit Leerzeichen ist unter Windows der Normalfall.
 *
 * `vscode.env.shell` ist die Default-Shell, und `createTerminal` ohne `shellPath` nimmt genau
 * die — beide sehen dasselbe. Für cmd.exe und jede POSIX-Shell ist ein vorangestelltes `&`
 * falsch, deshalb die Fallunterscheidung statt „immer `&`".
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

        // Ungespeicherte Änderungen zuerst schreiben: der Compiler liest die Datei von der
        // Platte, nicht aus dem Editor-Puffer. Ohne das läuft die vorige Fassung, und der
        // Nutzer sucht den Fehler in seinem Programm statt in seinem Editor.
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

        // Der DRIVER, nicht lyrc oder lyrvm: er ist das eine Kommando, das übersetzt und
        // ausführt (ADR-019). Wer die Tools einzeln will, ruft sie im Terminal selbst.
        const shell = lyricTerminal();
        shell.show(true);
        const file = quote(editor.document.fileName);
        shell.sendText(`${callPrefix()}${quote(executable)} run ${file}`);
    });

    context.subscriptions.push(run);
}

function deactivate() {
    // Das Terminal gehört uns, also räumen wir es weg. VS Code würde es sonst als Waise
    // stehenlassen, wenn die Extension neu geladen wird.
    if (terminal !== null) {
        terminal.dispose();
        terminal = null;
    }
}

module.exports = { activate, deactivate };
