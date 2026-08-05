# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M6 — VM — abgeschlossen**

`lyric run examples/hello.lyr` gibt `Hello, Lyric!` aus, `examples/arith.lyr` liefert Exit-Code
**55**. Damit prüft das Projekt zum ersten Mal, ob ein Programm das Richtige *tut* — bis M5 konnte
nur geprüft werden, ob es korrekt übersetzt wird. 1156 Tests grün.

**M6-Exit ist `hello.lyr` allein**, nicht zusätzlich FizzBuzz und Fibonacci: FizzBuzz braucht
`for-in` über einen Range (also `Iterator`), Fibonacci ist eine `Coroutine<int>` — und Coroutinen
sind M7. Derselbe Fehler wie bei M5s Exit, eine Stufe später: gemessen an Programmen, deren
Sprachmittel erst der nächste Meilenstein liefert. Die ROADMAP ist entsprechend korrigiert
(Blockzitat unter M6).

**M5 — IR + Bytecode — abgeschlossen** (`v0.1.0`)

Slices P1–P5 stehen: IR-Datentypen, Printer + Goldens, Verifier, Lowering AST → IR, Bytecode-Format
+ Writer/Reader/Disassembler. Die Pipeline läuft durch: `lyric build examples/arith.lyr` erzeugt
`.lyrbc`, `lyric disasm` zeigt sinnvolle Instruktionen. ADR-006 und ADR-013 sind umgesetzt,
`docs/Bytecode.md` ist normativ geschrieben. 1151 Tests grün.

**M5-Gate-Programm ist `examples/arith.lyr`**, nicht `hello.lyr`: letzteres braucht
`console.println` und f-Strings, also eine Import-Tabelle mit Signaturen — und die entsteht erst mit
dem Stdlib-Minimum in **M6**. Hello-World ist ohnehin schon M6s Exit-Kriterium; M5 endet damit an
der Grenze, die es selbst kontrolliert. Die ROADMAP ist entsprechend korrigiert (M5-Exit + Gate-
Programm, mit Begründung als Blockzitat).

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`), **M2 — Parser** (`m2-complete`), **M3 — Resolver + Sema
  basic** (`m3-complete`), **M4 — Sema full** (`m4-complete`). Volle v1-Sprache
  typgeprüft; Entscheidungen D1–D11 in `Sprache.md`/`Doku.md` fixiert. Details in den Tags / `git log`.
- [x] **M5 — P1 — IR-Datentypen**: `IrModule`/`IrFunction`/`IrBlock`, Ids als
  `BlockId`/`TempId`/`LocalId`/`FunctionId` (die Id **ist** der Slot-/Sprung-Index im späteren
  Bytecode, daher dichte Tabellen). Instruktionen als Records, Ops und Terminatoren getrennt
  (`IrOp` vs. `IrTerminator`) — „Terminator mitten im Block" ist damit unrepräsentierbar statt
  geprüft. `IrScalarType` + `TypeLowering` von `LyrType`.
- [x] **M5 — P2 — Printer + Golden-Tests**: `IrPrinter` als deterministischer Text-Dump (Typ steht
  am Dest, nie `AppendLine`, `switch` mit default-Wurf erzwingt Vollständigkeit). 7 Golden-Fixtures
  inkl. `loop` (Back-Edge).
- [x] **M5 — P3 — Verifier**: `IrVerifier.Verify` sammelt Befunde als Klartext-Strings, `VerifyOrThrow`
  wirft. Bewusst **keine** `LYR-IR####`-Codes: jeder Befund ist ein Compiler-Bug, keine
  User-Diagnose — der Code-Bereich bleibt echten Lowering-Fehlern vorbehalten.
  - **Vier Phasen mit Bail-out** (Tabellen → CFG-Form → Reachability + Availability → Def/Use +
    Typen). Prüfungen setzen einander voraus; bei Fundamentalfehlern bricht die Funktion ab, damit
    ein Fehler keine Kaskade auslöst (dasselbe Prinzip wie `ErrorType` als Poison in der Sema).
  - **Def/Use per Availability-Dataflow** (vorwärts, Schnittmenge als Meet, optimistisches TOP für
    Loop-Header). Bei genau einer Definition pro Temp ist „auf jedem Pfad verfügbar" äquivalent zu
    „die Definition dominiert den Use" — kein Dominator-Baum nötig, bis Phi-Knoten dazukommen.
  - 74 Testfälle: jede gültige Fixture befundfrei, eine Invariante pro Negativ-Test, plus
    Bail-out-ohne-Kaskade, Isolation zwischen Funktionen, Determinismus, und dass der Verifier auf
    malformed IR nie selbst crasht.
- [x] **M5 — P4 — Lowering AST → IR** (`Lyric.Ir/Lowering/`, ~534 LOC): skalare Ausdrücke, Locals,
  modulinterne Calls inkl. Rekursion und Vorwärts-Call, `if`/`while`/`do-while`/`break`/`continue`/
  `return`, `if` als Ausdruck, `&&`/`||`, Casts, `++`/`--`, Compound-Assign. `lyric lower <file>`
  druckt den IR-Dump. Gate-Artefakt: `examples/arith.lyr`.
  - **Statements liefern „fällt der Kontrollfluss durch?"** — ohne diesen Rückgabewert kann man
    nicht entscheiden, ob ein Merge-Block angelegt werden darf, und einer ohne Prädecessoren wäre
    unerreichbar. Aus demselben Grund bricht die Statement-Schleife bei einem Terminator ab: Code
    nach `return` darf keinen Block erzeugen.
  - **Werte über Blockgrenzen laufen durch Locals, nicht durch Temps** (if-Ausdruck, `&&`, `||`).
    Genau deshalb braucht diese IR kein `Phi`.
  - **Zwei Pässe**: Pass 1 vergibt die `FunctionId`s, Pass 2 lowert — sonst scheitern Vorwärts-Call
    und Rekursion. Der Verifier läuft als Abnahme nach jedem Lowering (`VerifyOrThrow`).
  - 47 Testfälle: 11 Golden-Fixtures (Quelle + Snapshot als Paar), Invarianten (Blockdichte,
    Parameter-Konvention, verworfener toter Code, kein Merge bei beidseitigem return), Determinismus
    und die Scope-Grenzen mit Quellposition.
- [x] **M5 — P5 — Bytecode** (`Lyric.Bytecode/`): `docs/Bytecode.md` normativ, Writer, Reader mit
  Load-Zeit-Validierung, Disassembler, `lyric build` und `lyric disasm`.
  - **Stack-Scheduling statt Slot pro Temp.** Die IR ist temp-basiert, das Ziel eine Stack-VM.
    Naiv würde `return a + b` zehn Instruktionen erzeugen; mit Scheduling sind es vier. Möglich ist
    das, weil **der Stack an jeder Blockgrenze leer ist** — Werte über Blockgrenzen laufen durch
    Locals, was das P4-Lowering schon strukturell garantiert. Damit ist das Scheduling blocklokal
    und die Tiefe beim Laden statisch prüfbar. Korrektheit hängt nie an der Optimierung: der
    Slot-Weg ist immer verfügbar, das Scheduling entscheidet nur, wo er entfällt.
  - **Ein Opcode pro Operation + Typ-Tag-Byte**, nicht ein Opcode pro (Operation × Typ). Bei zehn
    numerischen Typen wären es sonst ~100 Arithmetik-Opcodes und die Tabelle wäre nicht mehr
    lesbar — lesbar muss sie sein, weil ADR-013 die Implementierbarkeit aus der Spec verlangt.
    Der Tag steht im Instruktionsstrom, nicht im Wert: der Dispatch bleibt statisch.
  - **Sprungziele sind Block-Indizes** mit Offset-Tabelle im Funktionskopf, keine Byte-Offsets.
    Ein Ziel prüft man mit `index < blockCount` (ADR-013s Load-Zeit-Validierung) statt Byte-Offsets
    gegen Instruktionsgrenzen zu verifizieren — das CIL-Problem.
  - 45 Testfälle: Round-Trip (schreiben → lesen → schreiben, byte-identisch) über alle
    P4-Fixtures, Determinismus, Stack-Bilanz, Reader-Robustheit inkl. 400 Fuzzing-Läufen mit
    festem Seed, und ein Test, der `docs/Bytecode.md` gegen die Opcode-/Tag-Tabellen bindet.

- [x] **M6 — Slice 1 — VM-Kern** (`Lyric.Vm/`): `LyrValue`, Frames, Interpreter, `lyric run`.
  - **Werte ohne Typ-Tag.** Jeder Opcode trägt sein Tag im Instruktionsstrom (P5), also weiß der
    Interpreter statisch, was auf dem Stack liegt. Ein Tag im Wert wäre eine zweite, redundante
    Wahrheitsquelle. Zahlen liegen in einem `ulong`, nur Strings brauchen eine Referenz.
  - **Ganzzahlen immer auf 64 Bit erweitert** (signed vorzeichen-, unsigned nullerweitert), nach
    jeder Rechnung auf die Zielbreite normalisiert. Ohne den Schritt liefert `add i8` mit 100+100
    die Zahl 200 statt −56.
  - **Expliziter Frame-Stack statt .NET-Rekursion**: sonst begrenzte der CLR-Stack die
    Lyric-Rekursion, und ein Überlauf wäre ein Prozessabbruch statt einer Diagnose (`LYR-VM0004`).
  - **Instruktionen einmal vordekodiert**, über denselben `CodeDecoder` wie Validator und
    Disassembler. Blocksprünge sind damit ein Array-Zugriff.
  - **Neue Start-Sektion im Format** (Id 7, Version 1.1): der Einstiegspunkt stand nirgends im
    Bytecode. Eine Runtime hätte `main` nur über eine Namenskonvention finden können — und eine
    zweite Implementierung, die nur die Spec kennt, gar nicht. Das widersprach ADR-013 direkt.
  - **Numerische Laufzeit-Semantik in `Sprache.md` §6.6 festgeschrieben**: Überlauf wickelt um,
    Schiebebetrag modulo Operandenbreite, Fließkomma→Ganzzahl sättigt, Ganzzahl-Division durch
    Null ist ein `panic` und Float-Division IEEE. „Undefiniert wie in C" ist an keiner Stelle
    zulässig — `.lyrbc` ist ein plattformneutraler Vertrag (ADR-013).
  - **Laufzeitfehler sind `panic`** (§9) mit Backtrace, kein dritter Fehlermechanismus neben
    `panic` und typisierten Exceptions. `lyric run` beendet mit **101**, damit ein Skript einen
    Absturz von einem regulären `return 1;` unterscheiden kann.
  - 40 Testfälle über die **gesamte** Pipeline (Quelle → Sema → IR → Bytecode → Ausführung):
    Arithmetik inkl. Vorzeichen- und Breiten-Kanten, Konvertierungen mit Sättigung, Kontrollfluss,
    Kurzschluss-Nachweis über eine sonst auslösende Division durch Null, Laufzeitfehler.

- [x] **M6 — Slice 2 — Stdlib + f-Strings** (`stdlib/`, Import-Sektion, `NativeRegistry`):
  `lyric run examples/hello.lyr` gibt `Hello, Lyric!` aus.
  - **Source-first: die Stdlib ist gewöhnlicher Lyric-Quelltext.** `stdlib/std/io/console.lyr` und
    `stdlib/std/string.lyr` enthalten bodylose `fn`-Deklarationen — kein neuer Mechanismus,
    `Sprache.md` §3.1 erlaubt `( Block | ';' )` schon. Sie werden geparst, aufgelöst und typgeprüft
    wie jedes andere Modul; der Compiler kennt keinen Sonderfall „println". Nur die **Herkunft**
    entscheidet, ob ein rumpfloses `fn` eine Import-Deklaration ist: außerhalb der Stdlib ist es
    `LYR-SEM0051`. Am Inhalt festzumachen hieße, jeder könnte sich Natives erschleichen, indem er
    sein Modul `std.foo` nennt.
  - **Nachladen vor dem Auflösen, nicht mittendrin** (`Compilation.LoadImportedModules`): sonst
    wüchse die Modul-Liste, während der Resolver über sie iteriert. Zyklen terminieren von allein,
    weil vor dem Betrachten der eigenen Imports registriert wird.
  - **Well-Known-Module** (`std.string`) werden geladen, ohne dass der Nutzer sie importiert — das
    f-String-Lowering ruft `concat`/`fromXxx` auf. Dasselbe Modell wie Roslyns Well-Known-Members.
  - **Natives binden über den symbolischen Namen zur Ladezeit** (`NativeRegistry.Bind`), mit Prüfung
    von Name **und** Signatur. Ein `.lyrbc` mit unbekanntem Import scheitert beim Laden, nicht beim
    ersten Aufruf — ADR-013s Load-Zeit-Validierung.
  - **Escape-Auflösung nach `Lyric.Core`** (`Escapes.Resolve`): `Lyric.Ir` darf `Lyric.Parsing`
    nicht referenzieren, braucht aber beim f-String-Lowering dieselbe Regel wie der Lexer. Eine
    zweite Kopie wäre zwei Wahrheiten über `\n`.
  - **Ein Front-End für alle CLI-Kommandos** (`Program.Frontend`): `run`, `lower` und `check` hatten
    je eine eigene Kopie des Vorspanns, und nur eine verdrahtete den `ModuleLoader`. `check` hielt
    deshalb jeden Stdlib-Import für opak und prüfte die Aufrufe **stumm gar nicht**. Zusammen mit
    dem fehlenden Auspacken von `ImportBindingSymbol` im `TypeChecker` ging `println(42)` bis in die
    VM durch — und `LyrValue` hat kein Typ-Tag, das wäre eine stille Fehlinterpretation geworden.

- [x] **M7 — P1 — Classes** (`TypeTable`, Types-Sektion, Format **1.2**): `lyric run
  examples/objects.lyr` legt Objekte an, mutiert sie über Funktionsgrenzen und liefert 21.
  Der erste nicht-skalare Typ im ganzen Stack — bis hierher war das IR-Typ-Universum
  `IrScalarType` und sonst nichts.
  - **Der Typ trägt nur seine Id, nicht sein Layout** (`IrRefType(TypeId)`). Sonst müsste
    `IrType.Equal` strukturell vergleichen und liefe bei `class Node { next: Node }` in eine
    Endlosschleife. So ist Gleichheit ein `int`-Vergleich und Rekursion kostenlos.
  - **Die Id wird vor dem Layout vergeben.** Genau das macht rekursive Typen möglich: beim
    Betreten reserviert `TypeTable.Intern` den Platz und trägt die Id ein, erst danach werden die
    Feldtypen gelowert. Ein Selbstverweis findet die Id vor und terminiert. Dieselbe
    Zwei-Phasen-Form wie Pass 1/2 im `ModuleLowerer`.
  - **Feldzugriff über den Index, nicht über den Namen.** Lyric ist statisch typisiert und kennt
    kein Monkey-Patching, also steht der Index zur Compile-Zeit fest. Namens-Lookup mit
    Inline-Cache (CPython, Ruby) löst ein Problem, das diese Sprache nicht hat. Feldnamen stehen
    deshalb nicht im Bytecode; der Disassembler zeigt `Typ#index`.
  - **Feldreihenfolge kommt aus dem AST, nicht aus der Symboltabelle.** Der Index ist der Slot im
    Objekt und muss die Deklarationsreihenfolge sein — eine Symboltabelle ist eine Map, ihre
    Aufzählungsreihenfolge ist ein Implementierungsdetail.
  - **Typ- und Feldindex stehen an jeder Instruktion**, obwohl das Objekt seinen Typ kennt. Nur so
    prüft der Loader den Feldindex gegen ein Layout, ohne eine Datenfluss-Analyse zu fahren —
    ADR-013s „Validierung beim Load statt beim Call". Zur Laufzeit ist der Feldzugriff dann ein
    Array-Zugriff ohne jede Prüfung.
  - **Ein Objekt ist ein `LyrValue[]` hinter `LyrValue.Ref`**, ohne Typ-Tag im Wert — konsequent
    zur M6-Entscheidung. Kein Feld ist je uninitialisiert (§6.6). Keine Darstellungsänderung nötig:
    `Ref` war schon da.
  - **`BindingResult` läuft jetzt ins Lowering.** Ein Feld vom Typ einer anderen Klasse braucht den
    aufgelösten Namen; Namensauflösung im Lowerer nachzubauen wäre eine zweite Wahrheit über
    Sichtbarkeit und Schattierung.
  - **Classes vor Structs** (P4): eine Referenz ist ein Maschinenwort und kopiert sich selbst, ein
    Wert-Typ braucht eine Kopierentscheidung (Boxing+Kopie vs. Scalar Replacement mit
    Escape-Analyse). Die trifft man, wenn das Layout-Gerüst steht.
  - 21 neue Testfälle: 2 Golden-Fixtures (inkl. rekursivem Typ), 7 Verifier-Invarianten mit
    Gegenprobe auf Selbstreferenz, Round-Trip und Fuzzing laufen über die neuen Fixtures mit,
    6 E2E-Tests — darunter **der Test, der P1 von P4 trennt**: zwei Namen, ein Objekt.

- [x] **Reparatur — stumm übersprungene Prüfungen** (`LYR-RES0003`, `LYR-SEM0052/0053`).
  Systematisch vermessen: **sechs** Konstrukte prüften vollständig durch, obwohl sie ungültig sind.
  - **Die Ursache ist eine überladene Invariante**: `ErrorType` trug zwei Zustände — „hierfür wurde
    schon gemeldet" (schweigen ist richtig) und „ich weiß nicht, was das ist" (schweigen ist
    falsch). Jeder Konsument tut `if (x.IsError) return Error;`, also riss der zweite Zustand jede
    Prüfung darunter mit. **Ab jetzt gilt: `Error` heißt ausschließlich „schon gemeldet".**
  - **Teuerster Fall**: `import std.io.consle { println };` — ein fehlender Buchstabe im Modulnamen
    war stumm *und* schaltete die Prüfung jeder Verwendung ab. Ein falsches *Symbol* in einem
    richtigen Modul wurde dagegen immer gemeldet. Die Asymmetrie war ein Überbleibsel aus M3
    („Module außerhalb der Compilation gelten als extern/opak") — seit es den `ModuleLoader` gibt,
    ist ein unauffindbares Modul schlicht ein Fehler.
  - **Warum man es nicht am Erzeuger melden kann**: `CheckMember` prüft sein Ziel, *bevor* es
    weiß, ob das Ziel ein Typ ist — `P` in `P.new()` läuft durch denselben Pfad wie `P` als Wert.
    Deshalb ein eigener `NonValueType`, der nur als Member-Ziel überlebt und überall sonst einmal
    gemeldet und zu `Error` degradiert wird. Nebeneffekt: bessere Meldung als vorher
    („'P' is a type, not a value — did you mean 'P { … }'?").
  - Der Fix deckte sofort zwei echte Fehler im eigenen Korpus auf: `shapes.lyr` importiert
    `std.math`, `imports.lyr` importiert `std.io` — **beide Module existieren nicht**. Sie stehen
    jetzt als „wartet auf M8-Stdlib" fest, statt still als sauber zu gelten.
  - 12 Regressionstests, jeder mit einem groben Folgefehler im Rumpf: bricht die Prüfung je wieder
    stumm ab, meldet der Compiler *gar nichts* und der Test fällt. Dazu die Gegenprobe, dass
    `P.make()` und `console.println(…)` legal bleiben — daran scheitert der naive Fix.

- [x] **M7 — P1b — `static` und Methoden-Lowering** (ADR-014): `examples/objects.lyr` konstruiert
  über `Counter.new(5)` und ruft Instanzmethoden — Exit 21 wie vorher, jetzt über Fabrik und
  Methoden statt über nackte Feldzugriffe.
  - **Der Empfänger ist Parameter 0**, dieselbe Konvention wie CIL. Damit ist der Unterschied
    zwischen Instanz- und `static`-Methode allein die Parameterliste, und P3 muss für die vtable
    nur noch entscheiden, *welche* Funktion gerufen wird — nicht, wie sie aussieht.
  - **`this` ist ein Keyword-Ausdruck, kein Symbol**, deshalb hält der Lowerer seinen Slot direkt
    (`SlotAllocator.Declare`) statt über die Symbol-Map zu gehen.
  - **Namensmangling `<modul>.<Typ>.<methode>`** — ohne den Typnamen fielen `Account.get` und
    `Player.get` zusammen, und der Verifier lehnt doppelte Funktionsnamen ab.
  - **`static let` parst und typprüft, lowert aber nicht.** Es hängt an derselben Lücke wie ein
    Modul-`let`: Konstanten werden nirgends gelowert. Die Meldung sagt das jetzt auch, statt über
    einen Member-Zugriff auf `<?>` zu klagen.
  - **Ein Compiler-Absturz gefunden und behoben**: `TypeTable.Intern` trägt den Platzhalter ein,
    *bevor* es die Feldtypen lowert. Warf es danach — bei `bank.lyr` am Feld-Default `balance:
    int = 0` —, blieb der Platzhalter stehen, und die nächste Funktion las ein Layout mit
    `FieldNames == null`. `lyric run examples/bank.lyr` endete in einer Access Violation statt in
    einer Diagnose. Fehlgeschlagene Typen werden jetzt gemerkt und werfen erneut; Scope-Grenzen
    werden pro (Position, Text) nur einmal gemeldet.
  - **Eine Regel aus ADR-014 wieder zurückgenommen**: das Verbot von `mut` an Klassen-Methoden.
    `Doku.md` §10.2 führt den Marker ausdrücklich als Lesbarkeits-Konvention, und Interfaces
    deklarieren `mut fn`, das implementierende Klassen erfüllen müssen — das Verbot hätte die
    Konformanz gebrochen. Korrektur steht im ADR.
  - 16 neue Tests: Golden-Fixture `methods`, 8 Sema-Regeln inkl. der Gegenprobe zu `mut`,
    4 E2E-Tests (Fabrik, Mutation über den Empfänger, Methode ruft Methode, zwei Instanzen
    getrennt) und der Regressionstest für den Absturz.

## Woran wir gerade arbeiten

**M7 — Objektmodell + VM (full)**, neu zugeschnitten (14–20 Wochen, acht Slices P1–P8; Tabelle in
der ROADMAP). **P1 steht** (siehe unten).

**Vorgezogen, weil beim P1-Review aufgefallen**: eine Reflexionsrunde über Konstruktion, Member und
Sichtbarkeit. Entschieden und als **ADR-014** (Member/Konstruktion) und **ADR-015** (Overloading)
festgehalten:

- **`static` und `static let`** kommen — `static fn new(…)` löst die Methoden-Frage (Empfänger
  ja/nein), `static let ZERO: Vector3 = …` gibt typgebundene Konstanten. Ein Keyword, zwei Lücken.
- **Companions: nein.** Ohne Objektidentität sind sie `static` mit Klammern; mit Identität ziehen
  sie Initialisierungsreihenfolge und Companion-Vererbung nach. Und sie wären ein zweiter
  Namensraum neben Modulen — bei ADR-012 (Datei = Modul) meist derselbe.
- **Funktions-Overloading: vertagt auf v1.X**, wo Operator-Overloading schon steht. Es war nie
  entschieden — die Behauptung „Lyric hat kein Overloading" stammt aus M6-2 von Claude und stand
  fälschlich in `Doku.md` und `ROADMAP.md`. Ausschlaggebend: Overloading ist **additiv**
  (nachrüstbar ohne Bruch), und seine Kosten landen genau auf den vier Stellen, die Lyric ohnehin
  schwerfallen — untypisierte Literale, Default-Argumente, Lambda-Inferenz, `extend`.

**P1b steht** — als nächstes **P2 — Arrays** (Gate: `examples/stats.lyr`).

Anlass des Neuschnitts: M5 und M6 haben je einen Teil ihrer eigenen Lieferposten nicht geliefert,
ohne Vermerk. M5s IR-Instruktionen `NewClass`/`LoadField`/`Throw`/`Yield`/… und das
Closure-/Coroutine-Lowering fehlen komplett — das IR-Typ-Universum ist bis heute `IrScalarType`
und sonst nichts. M6 lieferte die skalare Hälfte seiner Wert-Repräsentation. Alle vier
ursprünglichen M7-Themen brauchen aber Objekte: eine Closure **ist** ein Environment-Objekt, eine
Coroutine laut ADR-006 ein Struct mit `step`-Methode, ein Exception-Wert eine Klasseninstanz.

**Warum es nicht auffiel** — und die Lehre daraus: die Lücke tarnt sich als saubere Diagnose.
`bank.lyr` meldet `LYR-IR0001: type 'Account' is not supported by this compiler version yet`, mit
Position, ordentlich gerendert — und liest sich wie eine geplante Grenze. `LYR-IR0001` heißt aber
nur „noch nicht gebaut" und sagt nichts darüber, ob das so vorgesehen war. **Am Ende jedes
Meilensteins ist die Lieferposten-Liste Punkt für Punkt abzuhaken, nicht das Exit-Kriterium
allein.**

Zwei Dinge, die dabei gut gelaufen sind und M7 tragen: `docs/Bytecode.md` hat die Types-Sektion
(Id 3) und die Typ-Tags ab `0x40` von Anfang an reserviert, und `LyrValue` hat bereits ein
`Ref`-Feld. Das Objektmodell braucht deshalb keinen Formatbruch, nur Version 1.2.

## Scope-Check 2026-08-02 (Ergebnis)

- **Nichts gekürzt.** M0–M5 brauchten 58 Kalendertage gegen 140–210 geschätzte — die Kürzungs-
  Klauseln (>50 % / >100 % Überzug) greifen nicht. Die Zahl ist allerdings **nicht auswertbar**:
  die ROADMAP-Schätzungen sind für „~10 h/Woche solo" kalibriert, und die Slices M5-P3 bis P5 hat
  Claude implementiert. Sie messen den Wechsel des Arbeitsmodus, nicht den Fortschritt.
- **Schätzungen für M6–M10 bleiben unverändert.** Sie taugen als pessimistische Obergrenze, falls
  der Modus zurückwechselt; sie an den aktuellen anzupassen würde diese Reserve vernichten.
- **v1-Grenze bestätigt.** `docs/IDEAS.md` bleibt geparkt. Das Risiko hier ist Scope-Creep aus
  gefühltem Vorsprung, nicht aus Verzug.
- **Arbeitsmodus bleibt vorerst**: Claude plant *und* implementiert, der Maintainer reviewt.
  Bewusste Abweichung von `CLAUDE.md` §Collaboration — dort steht Plan-von-Claude/Code-vom-User.
  Zu beobachten ist, ob das Verständnis des Codes mit seinem Umfang mithält.
- **Kein `CHANGELOG.md` vor `v1.0.0`** — pre-1.0 gibt es keinen Kompatibilitäts-Anspruch, weder
  fürs `.lyrbc`-Format (ADR-013) noch für die Sprache selbst. Die annotierte Tag-Message ist bis
  dahin die Release-Notiz. `CONTRIBUTING` §Releases ist entsprechend geändert.
- **ROADMAP-Korrektur vom 30.07.** (M5-Exit auf `examples/arith.lyr`) formlos ratifiziert.

## Noch offen

**Aus M7/P1+P1b — bewusst offen:**

- **Konstanten lowern nicht** — weder `static let` noch ein Modul-`let`. Beide typprüfen sauber und
  scheitern erst im Lowering (`LYR-IR0001`). Braucht entweder eine Globals-Sektion im Bytecode oder
  Konstanten-Inlining; das ist eine eigene Entscheidung, kein Nachziehen.
- **Feld-Defaults** (`balance: int = 0`) werden abgelehnt statt ignoriert. Ein weggelassenes Feld im
  Initialisierer hätte seinen Nullwert; ob das erlaubt ist, sagt die Sema heute nicht. `bank.lyr`
  hängt daran.
- **Structs, Enums, Interfaces, generische Klassen** bleiben `LYR-IR0001` — P3/P4/P8.

**Aus M6:**

- **Kein CLI-Test-Projekt.** Dass `check` den `ModuleLoader` nicht verdrahtete, fiel nur beim
  Handprobieren auf — die Sema-Tests setzen ihn selbst. Ein Test, der die Kommandos gegen die
  Beispiele fährt, hätte das gefangen. Kandidat für M7.
- **`std.fmt.format` nach M8** (Format-Specs `{x:N2}` in `shapes.lyr`/`stats.lyr`).
- **Source-Map-Sektion** (Id 6) ist in der Spec beschrieben und reserviert, wird aber noch nicht
  geschrieben — Panics zeigen deshalb Funktion, nicht Zeile.

**Aus M5:**

- **Copy-Propagation im Emitter**: ein Temp mit mehreren Lesern erzeugt heute ein
  `ldloc`/`stloc`-Paar, das ein Optimierer einsparen könnte. Format-neutral nachrüstbar.
- **Verifier-Laufzeit**: er ist ~90 % der Lowering-Zeit, das meiste davon der Availability-Dataflow
  (HashSet pro Block, Fixpunkt-Iteration). Bitsets statt HashSets wären die naheliegende
  Optimierung — nur nötig, wenn Debug-Builds spürbar zäh werden.
- **Generics**: Richtung entschieden (Worklist im Lowering, ab den Wurzeln, eine Instanz pro
  Typargument-Tupel), Bau offen. Der Substitutions-Haken sitzt in `FunctionLowerer.LowerType`,
  `NameMangling` ist die Stelle für die Typargumente im Namen.

**Aus M4 vertagt:**

- **Generics-Rest**: Constraints mit eigenen Typ-Args (`Comparable<T>` über die Constraint-Grenze
  substituieren); Tuple-Varianten-Konstruktion generischer Enums über Call (`Opt.Some(5)`) — typt
  noch ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath nicht ausdrückbar;
  Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer).
- **Slice-4-Feinheiten**: generische Interface-Default-Substitution beim Member-Lookup nur
  best-effort; `@noCapture`-Enforcement fehlt (Lambda-Params tragen keine Attribute im AST).
- **Extern**: Stdlib-Imports opak → Modul-Universum + Builtin-Konformanz erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- **IR**: Type-Felder auf den Instruktionen sind Kopien für den Printer, die Temp-Tabelle ist die
  Autorität — dass beide übereinstimmen, ist der Kern-Job des Verifiers. Die tragenden
  IR-Invarianten (Parameter-Konvention, dichte Id-Tabellen, Entry-Regel, Single-Definition) stehen
  als Doku an `IrFunction`, durchgesetzt werden sie vom Verifier.
- **Totale Funktionen über das heutige Typ-Universum werfen im `default`**, statt einen Ersatzwert
  zu liefern: `IrType.Equal`, `IrNames.Scalar/Bin/Un`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`. Der Wurf nennt die Stelle, die beim Erweitern nachzuziehen ist. Ausnahme ist
  `IrVerifier.Show`: es baut Befund-Texte, ein Wurf würde dort den Befund verdecken.
- **`IrShape` ist die einzige Quelle für Operanden/Dest/Successors** einer Instruktion (Verifier + Stack-Scheduler); zwei Kopien dieser switch-Blöcke wären still falscher Code statt eines Fehlers.
- **`IrNames` ist die einzige Quelle für Skalar-Namen und Op-Mnemonics** (Printer + Verifier). Man
  liest Dump und Befunde nebeneinander, wenn man einen Lowering-Bug sucht — sie dürfen nicht driften.
- „Ist der Typ genau dieser Skalar?" → Pattern-Match (`IsVoid`/`IsBool`, total). „Stimmen zwei Typen
  überein?" → `IrType.Equal`. Zwei verschiedene Fragen, zwei Mechanismen.
- **Lowering**: Statements liefern „fällt der Kontrollfluss durch?"; Werte über Blockgrenzen laufen
  durch (ggf. synthetische) Locals, nie durch Temps; Blockdichte und `Entry == bb0` sind im
  `BlockBuilder` strukturell garantiert statt geprüft.
- **Zwei Fehlerklassen im Lowering, sauber getrennt**: gültiges Lyric, das der Backend-Stand noch
  nicht kann → Diagnose `LYR-IR0001` mit Datei/Zeile/Spalte, alle eines Programms in einem Durchlauf
  (`ModuleLowerer` liefert dann `null`, kein Teilergebnis — die `FunctionId`s wären verschoben).
  Interne Inkonsistenz → `InternalCompilationException` wie gehabt. **Bewusst genau ein IR-Code**:
  Codes sind stabile Bezeichner, die Lücken sind vorübergehend; ein Code, der verschwindet sobald
  Lambdas gelowert werden, war nie einer. `LYR-IR0002..0010` bleiben frei.
- **IR-Invarianten, die Arbeit ins Lowering verschieben** (alle im Verifier durchgesetzt und
  getestet): unerreichbare Blöcke sind ein Fehler (kein `SimplifyCfg`-Pass in v1); Block-Ids dicht
  und `Entry == Blocks[0]`; `string + string` lowert zu einem Call, **nicht** zu `BinOp Add` (sonst
  wäre der `add`-Opcode polymorph — gegen ADR-013); `IntConst` ist zweierkomplement-kodiert und auf
  64 Bit nullerweitert; Identitäts-`Convert` elidiert das Lowering; Ordnungsvergleiche nur auf
  Numerik, `eq`/`ne` auch auf bool/char/string.
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.
- **Zeilenenden sind Test-Vertrag, nicht Geschmack**: `.gitattributes` erzwingt `eol=lf` auch im
  Arbeitsbaum, weil die Lexer-/Parser-Goldens Span-Offsets vergleichen und CRLF jeden Offset um ein
  Byte pro Zeile verschiebt. **Nicht entfernen** — ohne sie fallen 14 Golden-Tests in jedem frischen
  Clone und der `windows-latest`-CI-Job bricht (GitHubs Windows-Runner haben `core.autocrlf=true`,
  Linux nicht; genau daran war die CI vom 24.07. bis 30.07.2026 halbseitig rot).

## Letzter relevanter Commit

`M7: static-Member und Methoden-Lowering (P1b)`

---

## Wie diese Datei zu pflegen ist

- Nach jedem Slice: `## Was schon erledigt ist` ergänzen, `## Woran wir
  gerade arbeiten` updaten, ggf. `## Was als nächstes ansteht` neu sortieren.
- Bei Meilenstein-Wechsel: oben den neuen Meilenstein eintragen, alte
  abgehakte Items aus `## Was schon erledigt ist` ausdünnen (nur die
  letzten 2–3 Slices behalten, Rest ist in `git log`).
- Bei Block-/Diskussions-Bedarf: `## Offene Fragen` füllen — Claude
  greift das in der nächsten Session auf.
- **Niemals** hier neue Features planen. Das ist `ROADMAP.md`-Territorium.
