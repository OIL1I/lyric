# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M9 — REPL + Tooling — abgeschlossen.** Slices S1 bis S5, danach ein Aufräum-Slice.

1747 Tests grün, Bytecode-Format **2.5**, **vier** Binaries, Version **0.9.0**. `lyric repl`
läuft, die README ist maschinell geprüft, die TextMate-Grammar an den Lexer gebunden, die
VS-Code-Extension liefert Highlighting und einen Run-Command.

**Offen für v1.0: nur noch M10** — die Embedding-API (`LangVm`, Marshalling, Hot-Reload).

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **Aufräum-Slice vor M10** (2026-08-07). Drei Punkte, die den `v0.9`-Tag blockiert haben.
  - **Die Version stand an vier Stellen und widersprach sich.** README und `Doku.md` druckten
    `Lyric 0.9.0` als REPL-Ausgabe ab, während die REPL `0.0.1-dev` sagte — eine abgeschriebene
    statt einer erzeugten Ausgabe, hingeschrieben in S5, unmittelbar nachdem S1 Tests **gegen
    genau diese Sorte Drift** gebaut hatte. Alles auf `0.9.0`; drei neue Tests binden
    `Directory.Build.props` an die Konstante, alle **vier** Binaries aneinander (`lyrrepl` fehlte,
    seit es sie gibt) und jede in der Doku abgedruckte Werkzeug-Ausgabe an die echte.
  - **Ein Modul-`let` mit `T[]` oder `?T` brach das Lowering ab** — mit Stack-Trace, nicht mit
    Diagnose. Ursache war **zum vierten Mal dieselbe**: `FunctionLowerer.LowerType` war eine
    zweite, vollständige Kopie der Abbildung Sema-Typ → IR-Typ, und die Kopien waren
    auseinandergelaufen (`T[]` und `?T` hier, nicht dort). Die Kopie ist **gelöscht**: die Methode
    substituiert nur noch und delegiert an `TypeTable.Lower`. `SubstituteType` ist im selben Zug
    vollständig geworden (`FnType`, `CoroutineOf`, `TupleOf` fehlten) — sonst hätte die
    Delegation `fn(T) -> T` verschlechtert.
  - **`stack.lyr` lag drei Meilensteine kaputt im Verzeichnis.** Es hat ADR-016 nicht überlebt
    (`T[]` wurde ein echtes Array ohne `push`) und stand in keiner Test-Matrix — deshalb hat es
    niemand bemerkt. Jetzt auf `List<T>` umgeschrieben. `bank.lyr` und `fibonacci.lyr` fehlten
    ebenfalls; sie liefen, aber aus Glück. Ein neuer Test verlangt, dass **jedes** `examples/*.lyr`
    in der Matrix steht oder auf einer Ausnahmeliste **mit Grund** — die handgepflegte Liste
    vergisst sonst weiter.
  - Die Matrix hat eine dritte Spalte bekommen: schreibt das Programm **selbst** auf stderr?
    `bank.lyr` tut das als Pointe des Beispiels. Vorher hätte es aus der Matrix fallen müssen, und
    ein Beispiel zu entfernen, damit die Matrix grün bleibt, ist die falsche Richtung.

- [x] **M8 — S4 — `catch (e)` ohne Typ.** Die letzte Luecke aus P5 ist zu. 1627 Tests gruen.
  - **`Throwable` war laengst da** — als Builtin-Interface mit synthetischem AST, und die Sema
    gab dem ungetypten `e` schon immer diesen Typ. Auch die Handler-Tabelle konnte catch-all seit
    P5 (`CatchType == null`). Gefehlt hat nur der **Slot**: er bekommt jetzt den Interface-Typ,
    den die Sema ohnehin vergibt.
  - **Im Slot liegt ein Fat Pointer, und bauen kann ihn nur die VM.** Welcher konkrete Typ
    geworfen wurde, steht erst zur Laufzeit fest — die VM fuehrt ihn im Frame ohnehin mit, weil
    der typisierte Catch dagegen vergleicht. Ohne ihn waere `e.message()` ein `callvirt` auf
    einen Wert, der seinen eigenen Typ nicht kennt (P3: ein Objekt traegt kein Typ-Tag).
  - **Ein typisierter Catch bekommt weiterhin die nackte Referenz.** Sein Slot hat den konkreten
    Typ, dort gehoert sie hin; ein Test haelt beide Faelle nebeneinander.
  - Der Dispatch-Test fuehrt **zwei** Werfer. Mit nur einem bliebe er auch gruen, wenn der Fat
    Pointer immer denselben Typindex truege — dieselbe Lehre wie bei den Interface-Tests aus P3.

- [x] **Bug: `try { return … } catch (…) { return … }` liess den Compiler abstuerzen.** Gefunden
  beim Bau von S4, aber **unabhaengig davon** — und es traf eine der haeufigsten Formen ueberhaupt.
  - Der Merge-Block wurde **unbedingt** angelegt, blieb ohne Praedecessoren und war vom Einstieg
    unerreichbar. Genau das lehnt der Verifier ab (kein `SimplifyCfg`-Pass in v1). Er entsteht
    jetzt erst, wenn ihn jemand erreicht; faellt niemand durch, meldet `LowerTry` das nach oben.
  - **Derselbe Fehler stand beim Statement-`match`** und wurde im Inventur-Sweep behoben. Hier
    ueberlebte er, weil kein Beispiel und kein Test try/catch mit zwei returnenden Zweigen
    benutzt hat. **Zweimal dieselbe Ursache heisst: der Merge-Block gehoert grundsaetzlich
    bedarfsgesteuert**, nicht an jeder Stelle einzeln nachgezogen.

- [x] **M9 — S5 — Politur.** **M9 ist damit abgeschlossen.** 1727 Tests gruen.
  - README auf **vier** Binaries; `lyric repl` in `Doku.md` §23.1 mit den Kommandos und der
    Regel „Deklarationen bleiben, Statements laufen einmal".
  - Die Auslieferung liefert **16 Eintraege** (vorher 13) — `lyrrepl` samt DLL und
    `runtimeconfig`. Ein neuer Architektur-Test haelt fest, dass **jedes** Werkzeug aus
    `Tool.All` neben dem Treiber liegt: er sucht sie dort, und ein fehlendes meldet sich sonst
    erst zur Laufzeit beim Nutzer.
  - Die ROADMAP hat ihren M9-Vermerk („erreicht"), inklusive dessen, was M9 **nicht** bringt:
    LSP, Formatter und Attribute stehen in der v1.X-Tabelle.

- [x] **M9 — S4 — die REPL** (`lyrrepl.exe`, ADR-021). 1726 Tests gruen.
  - **Ein viertes Binary, und das war ADR-019s eigener Test.** Dort stand: „`lyrtest` fuegt sich
    als drittes Werkzeug ein, ohne dass am Dispatcher etwas zu aendern waere; das ist der Test
    dafuer, ob dieser Entwurf traegt." `lyric repl` ist **eine Zeile** im Dispatcher — der Entwurf
    traegt. Der Architektur-Test hat einen vierten Fall bekommen, der die Ausnahme ausdruecklich
    erlaubt: `lyrrepl` ist das erste Binary mit **beiden** Bibliotheken, weil eine REPL
    uebersetzt UND ausfuehrt.
  - **Deklarationen sammeln sich an, Statements laufen einmal.** Das ist die ganze Mechanik. Wer
    schlicht den Quelltext akkumuliert, laesst jedes `println` bei jeder folgenden Eingabe erneut
    laufen — ein Test misst genau das (`An_earlier_print_does_not_repeat`), und ohne ihn waere der
    Fehler unsichtbar, weil alles andere richtig aussieht.
  - **Zwei Versuche pro Eingabe**: erst als Ausdruck (gedruckt), bei Fehlschlag als Statement. Ob
    `console.println(x)` das eine oder andere ist, entscheidet der **Typ** und nicht die Syntax —
    ein Aufruf, der `void` liefert, laesst sich nicht drucken. Die Diagnosen des ersten Versuchs
    werden verworfen.
  - **Eine fehlerhafte Eingabe aendert nichts.** Wer sich vertippt, sitzt danach nicht auf einem
    Vorspann, der nicht mehr uebersetzt — ohne diese Eigenschaft waere eine Sitzung nach dem
    ersten Fehler unbrauchbar.
  - **Beim Bauen zweimal dasselbe gelernt**: der `try` sass nur um den Interpreter, aber eine
    Scope-Grenze wirft im **Lowering**. Ein `let xs = [1, 2]` riss die ganze Sitzung mit. In einem
    Programm ist ein solcher Wurf ein Absturz; interaktiv ist er eine Zeile, die nicht ging.

- [x] **M9 — S3 — VS-Code-Extension** (`tooling/vscode-lyric/`). 1715 Tests gruen.
  - Highlighting ist deklarativ (Manifest + Grammatik, kein Code); der einzige JavaScript-Teil
    ist das **Run-Command**. Es ruft den **Treiber**, nicht `lyrc` oder `lyrvm` — ADR-019: der
    Treiber ist das eine Kommando, das uebersetzt UND ausfuehrt. Mit `lyrc` bekaeme der Nutzer
    eine `.lyrbc` statt eines Laufs.
  - **Ungespeicherte Aenderungen werden vorher geschrieben.** Der Compiler liest von der Platte,
    nicht aus dem Editor-Puffer; ohne das laeuft die vorige Fassung, und der Nutzer sucht den
    Fehler in seinem Programm statt in seinem Editor.
  - **Sechs Tests binden das Manifest an das, was daneben liegt**: jeder Pfad existiert, Grammatik
    und Sprache nennen denselben `scopeName`, jedes Keybinding zeigt auf ein deklariertes
    Kommando. Nichts davon prueft VS Code beim Laden — ein falscher Pfad heisst einfach, dass die
    Faerbung fehlt.
  - **Keine Diagnosen, keine Completion.** Das braucht einen Sprachserver (v1.2). Eine halbe
    Loesung waere schlechter als keine: ein Editor, der Fehler *manchmal* zeigt, ist schlimmer
    als einer, der sie nie zeigt — man hoert in beiden Faellen auf, ihm zu glauben.

- [x] **M9 — S2 — TextMate-Grammar** (`tooling/vscode-lyric/`). 1709 Tests gruen.
  - **Die Keywords kommen aus dem Lexer, nicht aus dem Gedaechtnis** — und ein Test haelt beide
    Listen aneinander. Eine Editor-Grammatik ist eine **zweite Beschreibung derselben Sprache**,
    und zwei Beschreibungen driften: bekaeme Lyric ein Keyword, faerbte der Editor es einfach
    nicht, und das sieht aus wie ein Bezeichner. Der Test prueft **beide Richtungen** — auch, dass
    die Grammatik nichts faerbt, was die Sprache nicht kennt.
  - **Verschachtelte Block-Kommentare** (§1.1) sind der Fall, den die meisten Grammatiken falsch
    machen: ohne den Selbstbezug endet `/* /* */ */` eine Ebene zu frueh und faerbt den Rest der
    Datei als Kommentar. Ein eigener Test haelt den Selbstbezug fest.
  - **Spitze Klammern werden NICHT automatisch geschlossen.** Sie kommen als Vergleich mindestens
    so oft vor wie als Typargumente; ein Editor, der sie schliesst, produziert bei jedem `a < b`
    ein `>` zu viel. Dieselbe Mehrdeutigkeit, fuer die der Parser einen eigenen Token-Scan hat —
    hier ist die richtige Antwort, es zu lassen.
  - **Keine semantische Faerbung.** Ob ein Bezeichner ein Typ ist, weiss nur die Sema; die
    Grammatik raet es an der Grossschreibung und sagt das im Kommentar. Wer es genau will,
    braucht den LSP (v1.2).

## Messungen

Zahlen statt Meinungen. Erhoben 2026-08-07, Release, 100 000 Iterationen, bereinigt um eine
Skalar-Schleife derselben Länge.

| Was | Bytes/Operation |
|---|---|
| Struct-Bau **+** Methodenaufruf (`Vec2.add`) | **352 B** |
| nur Aufruf (`fn step(a: float): float`) | **176 B** |
| nur Struct-Bau | **112 B** |
| Skalar-Basislinie | 9 064 B *insgesamt* |

**Die VM ist im Kern allokationsfrei** — eine Schleife mit Fließkomma-Arithmetik allokiert über
100 000 Durchläufe nichts Nennenswertes. Alles darüber sind Aufrufe und Objekte.

**Die Hälfte der Bytes hat mit Structs nichts zu tun**: `Frame.For` allokiert pro Aufruf drei
Objekte (Frame, Slots, Stack). Damit ist die Reihenfolge für eine spätere Optimierung festgelegt —
**Frame-Pooling, dann Inlining, dann Scalar Replacement**, nicht umgekehrt: der in `add` gebaute
Wert **escaped** (er wird zurückgegeben), also findet Escape-Analyse ohne vorheriges Inlining
nichts. Die Ideen stehen in `docs/IDEAS.md`; **gebaut wird davon in v1 nichts.**

Im Frame-Budget: 1000 Entities × 10 Vec-Operationen × 60 fps ≈ 211 MB/s, grob eine
Gen0-Sammlung pro Frame. Gen0 ist kurz — **kein Grund, Vektor-Mathematik hinter Natives zu
verlegen.** Das war die offene Frage aus P4; sie ist beantwortet.

Weiter gemessen: `for-in` über einen Range kostet **1,28×** gegenüber einer `while`-Schleife (nicht
mehr, wie der P8-Eintrag befürchten ließ). Der Verifier ist **~50 %** der Lowering-Zeit in Debug,
nicht ~90 % — die alte Behauptung stammte aus M5 und hatte nie eine Quelle. Ein Release-Profil
steht weiter aus.

## Woran wir gerade arbeiten

**M9 ist abgeschlossen — als naechstes M10**, die Embedding-API: `Lyric.Embedding.LangVm` mit
`RegisterFunction`/`RegisterType`, die bidirektionale Marshalling-Schicht zwischen Lyric-Werten
und .NET-Objekten, `Reload` fuer Hot-Reload, und ein Beispiel-Host in C#.

Die Capabilities aus M8/S6 sind dafuer die halbe Miete: der Host konfiguriert beim Erzeugen der
VM, was ein Skript anfassen darf, und die Durchsetzung liegt bereits beim Laden. `std.dotnet`
gehoert in denselben Slice — es ist Interop und teilt sich die Marshalling-Schicht.

**Der `v0.9`-Tag** ist das Exit von M9 und noch nicht gesetzt; die annotierte Tag-Message ist die
Release-Notiz (CONTRIBUTING §Releases — kein `CHANGELOG.md` vor v1.0).

## Noch offen

**Sprachlücken, vor v1 zu schließen:**

- **`b?.get()` geht nicht** — Optional-Chaining mit *Methodenaufruf*. Die Sema macht `?.get` zu
  einem `?fn() -> int` und stolpert dann über das `()`. Feldzugriff (`b?.v`) funktioniert.
- **Die Konformanz prüft die Definition statt der Typargumente**: `Ones :: [Src<int>]` würde auch
  für `Src<string>` akzeptiert.
- **Parser: `s = Small { n = 5 };`** scheitert mit `LYR-PAR0016`, obwohl §6.2 den Ausdruck „in jeder
  Wert-Position" erlaubt — die Mehrdeutigkeits-Sperre gilt dem *Anfang* eines `ExprStmt`, greift
  aber auf die ganze Zuweisung durch. *(Bekannt seit P3 — und am 2026-08-07 beim Schreiben einer
  Messprobe erneut hineingelaufen, ohne ihn wiederzuerkennen. Er kostet real Zeit.)*
- **Generics-Rest aus M4**: Constraints mit eigenen Typ-Args über die Grenze substituieren.
  `Opt<int>.Some(5)` bleibt offen — eine *statische Methode* auf einer generischen Instanz ist
  weiterhin `LYR-SEM0052`; explizite Typargumente gibt es nur an Funktions-Aufrufen.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.
- **`char as int` ist kein erlaubter Cast** (`LYR-SEM0006`). Beim Schreiben der S2-Tests
  aufgefallen. Ob das gewollt ist, sagt §6.5 nicht eindeutig — ungeprüft gelassen, weil eine
  Cast-Regel eine Sprachentscheidung ist und kein Nebenprodukt.
- **`do { return … } while (…)` laesst den Compiler abstuerzen.** Rumpf, Bedingung und Ausgang
  werden alle drei vorab angelegt; terminiert der Rumpf, sind Bedingung und Ausgang unerreichbar,
  und der Verifier lehnt ab. Der Fix braucht einen Umbau, weil `_loops.Push` die Sprungziele
  vorab braucht — ein bedarfsgesteuerter Ausgang muesste wissen, ob ein `break` ihn benutzt.
  *Bewusst offen gelassen*: eine Schleife, deren Rumpf immer terminiert, schleift nie; die Form
  ist toter Code. Gefunden im Merge-Block-Sweep 2026-08-07.
- **`DeclaredTypes.Lower` wirft ungefangen** aus `ModuleLowerer.Lower` heraus: eine native
  Signatur mit einem unbekannten Typ gibt einen Compiler-*Absturz* statt einer Diagnose. Gefunden
  beim Bau von S2, als `split` noch nicht lowerbar war.

**Zwei Ungleichbehandlungen, die eine Entscheidung brauchen (keine Bugs):**

- `fn f(p: P) { p.x = 9; }` ist `LYR-SEM0019`, aber `p.shift(9)` mit `mut fn` geht durch — obwohl
  beides denselben Effekt auf dieselbe Kopie hat.

**Werkzeug und Format:**

- **Source-Map-Sektion** (Id 6) ist reserviert und beschrieben, wird aber nicht geschrieben —
  Panics zeigen deshalb die Funktion, nicht die Zeile.
- **Sektions-Byte-Größen fehlen in `lyrvm info`**: der Reader verwirft sie nach dem Parsen. Sie
  nachzurüsten hieße, das Modell um Herkunftsdaten zu erweitern — eigene Entscheidung.
- **Verifier-Anteil im Release-Profil messen** — die Debug-Zahlen sind von JIT-Aufwärmen
  durchsetzt und taugen nur als Größenordnung.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; **`ErrorType` heißt ausschließlich „hier wurde bereits
  gemeldet"** — nicht „unbekannt". Maschinell geprüft.
- Generics: Monomorphisierung. Die einzige Option, die zu dieser VM passt — C# reifiziert und
  braucht einen JIT, Java erased und bezahlt mit Boxing; beides setzt voraus, dass die Runtime
  Typen kennt, und ein Lyric-Wert trägt kein Typ-Tag (ADR-013).
- **Ein Wert trägt kein Typ-Tag.** Jeder Opcode trägt sein Tag im Instruktionsstrom, der Dispatch
  bleibt statisch. Daraus folgt das Fat-Pointer-Muster, das Interfaces (P3), Closures (P6) und
  Coroutinen (P7) teilen: Referenz plus Wort in `LyrValue`.
- **IR**: Type-Felder auf den Instruktionen sind Kopien für den Printer, die Temp-Tabelle ist die
  Autorität — dass beide übereinstimmen, ist der Kern-Job des Verifiers.
- **Totale Funktionen über das heutige Typ-Universum werfen im `default`**, statt einen Ersatzwert
  zu liefern (`IrType.Equal`, `IrNames.*`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`). Der Wurf nennt die Stelle, die beim Erweitern nachzuziehen ist. Ausnahme
  ist `IrVerifier.Show` — dort würde ein Wurf den Befund verdecken. *(Ein `default`, der still
  nichts tut, hat schon einmal den Instruktionsstrom desynchronisiert: `CodeDecoder.SkipType`.)*
- **`IrShape` ist die einzige Quelle für Operanden/Dest/Successors**, **`IrNames` die einzige für
  Skalar-Namen und Mnemonics.** Zwei Kopien dieser switch-Blöcke wären still falscher Code.
- **Lowering**: Statements liefern „fällt der Kontrollfluss durch?"; Werte über Blockgrenzen laufen
  durch (ggf. synthetische) Locals, nie durch Temps — **genau deshalb braucht diese IR kein Phi**.
  Blockdichte und `Entry == bb0` sind im `BlockBuilder` strukturell garantiert statt geprüft.
- **Zwei Fehlerklassen im Lowering**: gültiges Lyric, das der Backend-Stand nicht kann → `LYR-IR0001`
  mit Position; interne Inkonsistenz → `InternalCompilationException`. **Bewusst genau ein IR-Code**
  — Codes sind stabile Bezeichner, die Lücken vorübergehend. `LYR-IR0002..0010` bleiben frei.
  Ebenso gilt: eine entfallene Nummer (`LYR-CLI0007`) wird **nie** neu vergeben.
- **Zeilenenden sind Test-Vertrag, nicht Geschmack**: `.gitattributes` erzwingt `eol=lf` auch im
  Arbeitsbaum, weil die Goldens Span-Offsets vergleichen. **Nicht entfernen** — ohne sie fallen 14
  Golden-Tests in jedem frischen Clone und der `windows-latest`-Job bricht.
- **Arbeitsmodus** (Scope-Check 2026-08-02, gilt weiter): Claude plant *und* implementiert, der
  Maintainer reviewt — bewusste Abweichung von `CLAUDE.md` §Collaboration, wo
  Plan-von-Claude/Code-vom-User steht. Zu beobachten ist, ob das Verständnis des Codes mit seinem
  Umfang mithält. **Kein `CHANGELOG.md` vor `v1.0.0`**; die annotierte Tag-Message ist die
  Release-Notiz.
- **Am Ende jedes Meilensteins ist die Lieferposten-Liste Punkt für Punkt abzuhaken, nicht das
  Exit-Kriterium allein.** M5 und M6 haben je einen Teil ihrer Posten stillschweigend nicht
  geliefert; die Lücke tarnte sich als saubere Diagnose. Aus demselben Grund wurden in M7 **sechs**
  Gates neu zugeschnitten, weil sie Sprachmittel späterer Slices verlangten.

## Letzter relevanter Commit

`M8: std.math, std.os, std.io.file (S7)`

---

## Wie diese Datei zu pflegen ist

- Nach jedem Slice: `## Zuletzt fertig geworden` ergänzen, `## Woran wir gerade
  arbeiten` updaten.
- **Höchstens vier Einträge unter `## Zuletzt fertig geworden`.** Der fünfte
  fliegt raus — er steht in `git log`. Diese Regel gab es schon, sie wurde
  1088 Zeilen lang ignoriert.
- Bei Meilenstein-Wechsel: oben den neuen Meilenstein eintragen.
- Erledigte Punkte aus `## Noch offen` **löschen**, nicht durchstreichen.
- **Niemals** hier neue Features planen. Das ist `ROADMAP.md`-Territorium.
