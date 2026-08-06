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
  - **`static let` parste und typprüfte, lowerte aber nicht.** Es hing an derselben Lücke wie ein
    Modul-`let`: Konstanten wurden nirgends gelowert. Die Meldung sagte das dann auch, statt über
    einen Member-Zugriff auf `<?>` zu klagen. *(Dieser Satz wurde am 2026-08-06 kurzzeitig als
    falsch markiert — die Gegenprobe war es: sie ließ das `;` weg, das `BindingStmt` verlangt.
    **Seit P5c ist die Lücke geschlossen**, beide lowern in die Globals-Sektion.)*
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

- [x] **M7 — P2 — Arrays** (ADR-016, Format **1.3**): `lyric run examples/arrays.lyr` liefert 144.
  - **`T[]` ist ein echtes Array**, kein Zucker für `List<T>`. Die Länge steht bei der Erzeugung
    fest. Das war eine Kurskorrektur mittendrin: `Doku.md` §5.2 und `Sprache.md` §4 behaupteten das
    Gegenteil (Python-Modell), und die erste P2-Fassung hatte `push`/`pop` schon als Opcodes. Beide
    sind wieder raus — sie gehören `List<T>`.
  - **Das Bootstrapping-Argument** entschied es: dispatchte `[i]` immer über ein Interface, bräuchte
    dessen Implementierung selbst indizierten Speicher. Also ist `T[]` primitiv und `[i]` darauf
    direkt `ldelem`/`stelem`; alles andere bindet später an `Indexable<T>` — dieselbe Regel, die
    `for-in` schon für `Iterator<T>` benutzt, kein neuer Mechanismus.
  - **`T[N]` ist aus v1 gestrichen.** Sein einziger Zweck wäre die Länge im Typ gewesen — und die
    Ergonomie-Lücke („Array der Länge n mit Defaults") füllt `[0] * n` bereits, mit `n` als
    Laufzeitwert. Das steht seit jeher in `Sprache.md` §6.5 und war nur nie gelowert.
  - **`arrcat`/`arrrep` statt `push`/`pop`**: `xs + ys` und `xs * n` sind spezifizierte
    Sprachsemantik (§6.5), beide liefern ein neues Array.
  - **Der Elementtyp steht inline**, nicht als Tabellen-Index wie bei einer Klasse — ein Array-Typ
    kann nicht rekursiv sein, also ist die Indirektion nur Kosten. `int[][]` ist `0x41 0x41 0x04`.
  - **Bounds-Verletzung ist ein `panic`** (`LYR-VM0006`). Ein Element-Index ist ein Laufzeitwert und
    beim Laden nicht prüfbar — anders als Typ- und Feldindizes. Der Verifier prüft deshalb nur die
    Form des Index (`i64`), nicht seinen Wert.
  - 22 neue Tests: Golden-Fixture `arrays`, 10 E2E-Fälle, Referenz-Semantik, „Konkatenation lässt
    ihre Operanden in Ruhe", vier Bounds-Panics und die negative Wiederholung.

- [x] **M7 — P2b — Optionals** (Format **1.4**): `lyric run examples/optionals.lyr` liefert 200.
  `?T`, `null`, `??`, `!` und Flow-Narrowing laufen bis in die VM.
  - **„Kein Wert" ist eine leere Referenz — einheitlich.** `LyrValue` hat `Bits` *und* `Ref`; für
    `?string`, `?T[]` und `?Klasse` fällt das mit der natürlichen Darstellung zusammen. Nur Skalare
    brauchen einen Marker, weil es bei `?int` kein freies Bitmuster gibt: ein global geteiltes
    Sentinel-Objekt sagt „hat einen Wert", die Zahl bleibt in `Bits`. Kein Boxing, keine
    Allokation, keine Änderung an `LyrValue`.
  - **Die Spec schreibt das ausnahmsweise vor**, obwohl sie Runtimes sonst keine Datenstrukturen
    diktiert: `optissome` muss überall dasselbe liefern. Verboten ist ausdrücklich, ein Bitmuster
    als null zu reservieren — `?int` muss alle 2⁶⁴ Werte tragen, sonst wäre `-1` je nach Runtime
    mal ein Wert und mal keiner. Drei Tests halten genau das fest.
  - **`??` bekommt keinen Opcode.** *(Richtigstellung 2026-08-06: hier standen auch `??=` und
    `?.`, aber die lowern bis heute nicht — `LYR-IR0001`. Die Begründung unten gilt für alle drei,
    gebaut ist nur `??`.)* Sie werten ihre rechte Seite nur bedingt aus
    und lowern zu Verzweigungen über `optissome` — wie `&&` und `||`. Ein Opcode müsste einen
    unausgewerteten Ausdruck transportieren, und das kann eine Stack-Maschine nicht.
  - **`x != null` ist kein Vergleich**, sondern `optissome`. Ein echter Vergleich bräuchte einen
    `null`-Wert auf dem Stack, und den gibt es nicht — „kein Wert" ist eine leere Referenz, kein
    Operand.
  - **Flow-Narrowing wird beim Lesen eingelöst**: nach `if (x != null)` sagt die Sema für `x` den
    Typ `T`, der Slot hält aber weiter `?T` — die Einengung ist eine Aussage über den
    Kontrollfluss, nicht über den Speicher. Der Lowerer packt genau dort aus, wo die Sema `T`
    erwartet. Das `optget` kann nie panicken: es materialisiert einen schon geführten Beweis.
  - 15 neue Tests, darunter der Kurzschluss-Nachweis für `??` über eine sonst auslösende Division
    durch Null und die drei `?int`-Randwerte (0, −1, 1).

- [x] **M7 — P3b — Enums und `match`** (Format **2.0**): `lyric run examples/enums.lyr` liefert 24.
  Unit-, Tuple- und Struct-Varianten, Methoden auf Enums, `match` als Ausdruck und Statement.
  - **Jede Variante ist ein eigener Typ** mit eigenem Layout, Slot 0 ist ihr Tag. Die Alternativen
    scheitern beide an der Regel, dass jedes Feld genau einen Typ hat: ein geboxter Payload braucht
    einen Slot ohne festen Typ, ein flaches Maximal-Layout gäbe Slot 1 je nach Variante einen
    anderen. Rust schichtet aus demselben Grund so.
  - **Nur drei Instruktionen**: `newvariant`, `enumtag`, `enumas`. `match` bekommt keinen Opcode —
    es verzweigt über das Tag wie jede andere Fallunterscheidung, und der Feldzugriff nach dem
    `enumas` ist ein gewöhnliches `ldfld`. Dieselbe Arbeitsteilung wie `optissome`/`optget`.
  - **Der letzte match-Arm wird nicht geprüft**: die Sema hat Exhaustivität bewiesen
    (`LYR-SEM0050`), ein Vergleich dort erzeugte einen unerreichbaren Block — und den lehnt der
    Verifier ab.
  - **Format 2.0, nicht 1.5.** Die Types-Sektion ändert ihre *Form* (Kind-Byte je Eintrag). §2
    erlaubt einer neuen Minor nur überspringbare Ergänzungen; ADR-013 deckt den Major-Bruch vor
    v1.0 ausdrücklich. Die Alternative wäre eine Minor-Nummer gewesen, die die eigene Regel bricht.
  - **Muster-Bindungen laufen über die Sema-Symbole**, nicht über eine eigene Namensmap — sonst
    gäbe es eine zweite Wahrheit über Scoping.
  - Der Verifier bekam die Kern-Invariante: **eine Variante gehört zu genau einem Enum**. Ein
    `enumas` auf eine fremde Variante wäre ein Feldzugriff mit falschem Layout, und die
    Load-Zeit-Validierung sähe nur, dass beide Indizes für sich gültig sind.
  - 9 neue Tests, dazu die Golden-Fixture. Der Verifier hat beim Bauen zwei echte Fehler gefangen:
    fehlender Empfänger beim Enum-Methodenaufruf und ein Vergleichs-`BinOp`, dessen Type-Feld den
    Operanden- statt den Ergebnistyp trug.

- [x] **Toolchain-Split — drei Binaries** (ADR-017): `lyrc` (Compiler), `lyrvm` (Runtime),
  `lyric` (Treiber). `lyric run examples/hello.lyr` verhält sich wie vorher, `lyrc build … && lyrvm
  run …` liefert dasselbe, und `lyric run … --vm <pfad>` fährt eine fremde Runtime.
  - **Der Anlass war ein Architekturfehler, kein Wunsch nach Namen.** `Lyric.Bytecode` referenzierte
    `Lyric.Ir` → `Lyric.Sema`, also zog `Lyric.Vm` die **gesamte** Front-End-Kette mit. Ein
    Binary-Split ohne diesen Schnitt wären drei Namen auf einem Monolithen gewesen — und ADR-013s
    Ziel-Test („jemand schreibt eine zweite Runtime allein aus der Spec") eine Behauptung.
  - **Der Schnitt war mechanisch**: von der Leseseite benutzten **null** Dateien die IR, nur
    `BytecodeWriter` und `StackScheduler`. Beide sind nach `Lyric.Bytecode.Emit` umgezogen, die
    Leseseite hängt jetzt allein an `Lyric.Core`. Round-Trip- und Fuzzing-Tests liefen danach
    unverändert grün — der Beweis, dass nichts umgebaut wurde. Möglich war das nur, weil P5
    `BytecodeModule` bewusst **nicht** als `IrModule` modelliert hat.
  - **Ergebnis in Zahlen**: `lyrvm` liefert 4 DLLs aus (Core, Bytecode, Vm, lyrvm), der Treiber 12.
  - **Die Exit-Code-Regel wohnt in `Lyric.Vm.VmHost`**, nicht im CLI. Sie ist normativ (§9/§11) und
    der Runner-Vertrag verlangt sie von *jeder* Runtime — im CLI hätten `lyrvm` und `lyric` je eine
    Kopie und damit zwei Wahrheiten über „was heißt 101". Aus demselben Grund liegen `ExitCodes`
    und der `LYR-CLI####`-Katalog in `Lyric.Core`: dem einzigen Projekt, das alle drei teilen.
  - **`lyric` hat keine eigene Pipeline.** Es ruft `SourceCompiler` und `VmHost` — dieselben
    Einstiege wie `lyrc` und `lyrvm`. Genau hier saß der M6-Bug (`check` ohne `ModuleLoader`), und
    drei Binaries sind drei neue Gelegenheiten dafür.
  - **In-Process als Default, Subprozess nur für fremde Runtimes.** Immer zu starten wäre
    symmetrischer, kostet aber einen zweiten .NET-Prozessstart (~50–70 ms) auf dem häufigsten
    Kommando. Der Preis dafür sind zwei Pfade — abgesichert durch eine Test-Achse, die dieselbe
    Beispiel-Matrix einmal in-process und einmal über `--vm` fährt, mit dem mitgelieferten `lyrvm`
    als per Konstruktion vertragskonformem Testdouble.
  - **Vier-Punkte-Runner-Vertrag** in `docs/Bytecode.md` §9: Aufruf-Form, Exit-Codes,
    Strom-Trennung, `--version`. Der naheliegende fünfte Punkt — ein Capability-Probe für
    Format-Versionen — ist gestrichen: ADR-013s Load-Zeit-Validierung beantwortet die Frage schon,
    ein Probe wäre ein zweiter Kompatibilitäts-Mechanismus (Rule 2). Kein VM-Registry, keine
    Konfigurationsdatei; `--vm` schlägt `LYRIC_VM` schlägt mitgeliefert — dieselbe Staffelung wie
    beim schon existierenden `LYRIC_STDLIB`.
  - **`tests/Lyric.Tests.Cli/` tilgt die M6-Schuld** („kein CLI-Test-Projekt"). 50 Fälle (1300
    Tests gesamt, keine Regression), gefahren
    als echte Prozesse. Der wichtigste ist der Architektur-Test: er prüft das
    *Ausgabeverzeichnis* von `lyrvm`, nicht die Metadaten — die ehrliche Frage ist, was neben dem
    Binary liegt, wenn man es ausliefert. Ohne ihn wandert die Kante innerhalb eines Meilensteins
    zurück und es fällt niemandem auf.

- [x] **Auslieferung: drei Assemblies, ein Ordner, ein Kommando**. `dotnet msbuild
  build/publish.proj` legt alle drei Binaries nach `artifacts/publish/`. Der Ordner hatte 24
  Eintraege und hat jetzt **13**; 1451 Tests gruen.
  - **Elf Bibliotheks-DLLs wurden drei.** `lyrcore` (Diagnostik + Leseseite des Formats), `lyrfe`
    (alles zwischen Quelltext und Bytes), `lyrrt` (der Interpreter). Elf Dateinamen im
    Auslieferungsordner verrieten eine Projektgliederung, die den Benutzer nichts angeht.
  - **Die Schnitte liegen auf der ADR-017-Kante**, deshalb wird die Aussage schaerfer statt
    schwaecher: nicht mehr „diese acht Dateien duerfen nicht dabei sein", sondern „es sind genau
    diese drei". Die Verbotsliste haette bei jedem neuen Projekt wachsen muessen — was niemand tat.
  - **Das Format-Lesen liegt bei `lyrcore`, nicht bei der VM.** Es ist der gemeinsame *Vertrag*:
    `lyrvm info` liest, ohne auszufuehren, und der Bytecode-Writer braucht dieselben Op-Codes.
    Laege es bei der Runtime, zoege jeder Compiler-Build den Interpreter mit — die Gegenrichtung
    von ADR-017.
  - **Was es kostet, ehrlich**: die feinen Kanten *innerhalb* des Frontends sind ab jetzt
    Konvention statt Compilerfehler. Der Parser koennte die Sema rufen. Die grosse Kante bleibt
    erzwungen, und nur die behauptet ADR-017.
  - **Zwei Funde nebenbei.** Erstens: in `bin/` lagen noch die DLLs der elf alten Projektnamen,
    und der Architektur-Test haette sie durchgewinkt — er verglich gegen eine Verbotsliste. Jetzt
    ist es ein Gleichheitsvergleich ueber alles, was `lyr*` heisst. Zweitens: **die CI baute mit
    `dotnet-version: 9.0.x` bei `net10.0`-Projekten.** Das kann seit dem TFM-Wechsel nie gelaufen
    sein.
  - **`Directory.Build.props`** ersetzt vierzehnmal wortgleiches Boilerplate; `tests/` erbt es und
    ergaenzt die vier PackageReferences, die neunmal dastanden. Release baut jetzt ohne Symbole
    und ohne `.deps.json` (der Host laedt dann app-lokal — es gibt kein NuGet-Paket im
    Auslieferungspfad). Die `.runtimeconfig.json` bleibt: ohne sie startet nichts.
  - **Die Version steht zweimal** — als C#-Konstante und als MSBuild-`<Version>`, weil MSBuild
    keine C#-Konstante lesen kann. Statt die Doppelung wegzudiskutieren, vergleicht ein Test sie
    gegen das erzeugte Assembly-Attribut.
  - Das Publish-Verzeichnis wird **vorher geleert**. Genau der Fehler, der in `bin/` schon
    passiert war: ein Ordner, der ueber Umbauten hinweg waechst, liefert Leichen mit.

- [x] **Toolchain-Optionen und Fortschrittsausgabe**: `--json`, `--quiet`, `--verbose`,
  `--progress`, `lyrc --stdlib`, `lyrvm info`, `disasm --function`. 1329 Tests grün.
  - **Eine Options-Schicht in `Lyric.Core`, kein Parser je Binary.** `--json` dreimal zu parsen
    hieße, dass `lyrc check --json` JSON liefert und `lyric check --json` still Klartext — derselbe
    Fehler wie beim dreifach kopierten Compiler-Vorspann in M6, nur eine Ebene tiefer.
  - **`TerminalOutput` ist der einzige Schreiber auf stderr.** Fortschrittszeile *und* Diagnosen
    laufen durch ihn, weil eine Diagnose sonst mitten in eine stehende Zeile schreibt — und zwar
    nur manchmal. Deshalb fällt auch die Entscheidung Text-oder-JSON genau dort und nirgends sonst.
  - **`RenderJson` hatte 13 Tests und null Aufrufer.** Vollständig implementiert, vollständig
    getestet, aus keinem Binary erreichbar — seit M0, das „Text- und JSON-Output" als Lieferposten
    listete. Jetzt angeschlossen.
  - **Zig ist das Vorbild für die Haltung, nicht für die Mechanik.** Übernommen: stderr,
    TTY-Erkennung, rückstandsloses Löschen. Verworfen: der Baum (bildet parallele Arbeit ab, die
    Lyric nach ADR-010 nicht hat) und der Render-Thread (sechs Phasenwechsel brauchen keine
    Synchronisation). Die **Verzögerungsschwelle** von 120 ms kommt von Cargo: bei einem
    40-Zeilen-Programm blitzte die Zeile sonst auf und wäre wieder weg.
  - **`lower` und `verify` sind getrennte Phasen**, über `Lower(verify: false)` plus eigenem
    `VerifyOrThrow`. Keine Verhaltensänderung — `VerifyByDefault` entscheidet weiter. Grund:
    STATUS behauptete seit M5, der Verifier sei ~90 % der Lowering-Zeit, und dafür gab es keine
    Quelle. **Die Messung sagt etwas anderes**: in Debug sind es ~50 % (49 ms lower / 52 ms
    verify bei `hello.lyr`), und ein guter Teil beider Zahlen ist JIT-Aufwärmen. Die alte
    Behauptung ist damit widerlegt, aber nicht sauber ersetzt — ein Release-Profil steht aus.
  - **Der Modul-Lader misst sich selbst.** `Compilation.Resolve` lädt intern, die Grenze
    Load/Resolve ist von außen nicht beobachtbar; statt `Lyric.Resolver` dafür aufzubohren, zieht
    die Delegat-Hülle ihre Dauer von der Resolve-Zeit ab. Dieselbe Naht trägt später ADR-012s
    Source-Root.
  - **`lyrvm info` ist das Werkzeug für den *zweiten* Implementierer**, nicht für den Alltag:
    `--json` macht „stimmt mein Reader mit deinem über die Tabellen überein" zu einem Diff.
    Vorbild sind `objdump -h` / `wasm-objdump -h`, nicht der Disassembler.

- [x] **Spec-Bug: Start-Index stand im falschen Indexraum** (gefunden durch `lyrvm info`).
  `docs/Bytecode.md` §Start (Id 7) legt den Einstiegs-Index in den **gemeinsamen** Raum (erst
  Importe, dann Funktionen) — denselben, den `call` benutzt. Der Writer schrieb die nackte
  `FunctionId`.
  - **Reader-Validierung und Disassembler folgten der Spec, Writer und Interpreter einander.**
    `lyrvm disasm examples/hello.lyrbc` zeigte deshalb `start: std.string.fromInt` statt `main`.
  - **Warum es 1300 Tests überstanden hat**: ohne Importe fallen beide Lesarten zusammen, und
    `arith.lyr` — das Gate-Programm der Bytecode-Tests — hat keine. Der Round-Trip schrieb und las
    mit derselben falschen Lesart, blieb also in sich konsistent. Sichtbar war es nur an einer
    Disassembler-Zeile, die niemand gelesen hat.
  - **Genau der Schaden, gegen den ADR-013 geschrieben ist**: eine spec-treue Fremd-Runtime wäre
    bei `hello.lyr` in einen Import gesprungen. Kein Format-Bump — die Spec war immer richtig, der
    Writer war falsch.
  - Regressionstest fährt bewusst ein Programm **mit** Importen und sagt das auch im Assert; ohne
    sie ist er wertlos.

- [x] **M7 — P3 — Interfaces + vtable-Dispatch** (Format **2.1**): `lyric run
  examples/interfaces.lyr` liefert 140. Dieselbe Aufrufstelle erreicht zwei Implementierungen —
  der erste und einzige dynamische Dispatch der Sprache.
  - **Ein Interface-Wert ist ein Fat Pointer**, kein Zeiger: Objekt plus konkreter Typindex. Das
    war erzwungen, nicht gewählt — ein Objekt trägt seit M6/P1 **kein Typ-Tag**, also kann
    `callvirt` die konkrete Klasse nicht aus dem Objekt zurückgewinnen. `LyrValue` hat `Bits` und
    `Ref`, und bei einer Referenz ist `Bits` ungenutzt; der Typ passt also kostenlos hinein.
    Dieselbe Trickkiste wie bei P2b, wo `Ref` als Anwesenheits-Marker diente. Die Alternative — ein
    Tag in Slot 0 jedes Objekts — hätte **jeden Feldindex verschoben** und jedes Objekt ein Wort
    gekostet, auch die Mehrzahl ohne Interface. Rusts `dyn Trait` schichtet genauso.
  - **Zwei Instruktionen**, dieselbe Arbeitsteilung wie `optissome`/`optget` und
    `enumtag`/`enumas`: `mkiface` materialisiert die Darstellung dort, wo der konkrete Typ noch
    statisch bekannt ist, `callvirt` konsumiert sie. `mkiface` trägt **beide** Indizes, obwohl die
    Runtime nur den konkreten braucht — so prüft der Loader die Implementierungs-Beziehung ohne
    Datenflussanalyse (ADR-013, wie Typ- und Feldindex am `ldfld`).
  - **Die Auflösungsreihenfolge fällt im Lowering, nicht zur Laufzeit.** Sprache.md §3.5 sagt
    „eigenes Member vor Interface-Default"; der Compiler löst das auf und schreibt die gewonnene
    Funktion in die vtable-Zeile. Die Runtime sucht nichts, sie liest einen Index.
  - **Interface-Default-Methoden sind gewöhnliche Funktionen mit dem Interface als Empfängertyp.**
    Damit wird `this.foo()` in einem Default selbst zu einem `callvirt` — und das ist richtig:
    welche Implementierung läuft, weiß erst die Laufzeit. Der Golden-Snapshot zeigt es direkt.
  - **Format 2.1, nicht 3.0.** Interfaces ergänzen nur: ein dritter Wert für das schon vorhandene
    Kind-Byte, eine neue Sektion (Impls, Id 8), ein neues Typ-Tag, zwei Opcodes. Keine
    Sektions-*Form* ändert sich — genau die Grenze, an der 2.0 nötig war.
  - **Eine Sema-Lücke geschlossen**: `IsAssignable` kannte kein nominales Subtyping, obwohl
    `Doku.md` §13 es ausdrücklich beschreibt. `hit(player, 40)` war schlicht ein Typfehler. Die
    Frage beantwortet jetzt `Conformance` — dieselbe Stelle wie Konformanz-Check und Lowering;
    drei Antworten auf „erfüllt T das Interface I" wären drei Gelegenheiten, dass die Runtime auf
    etwas dispatcht, das nie geprüft wurde.
  - 10 E2E-Tests plus Golden-Fixture. Jeder Test führt **zwei** Implementierungen — mit nur einer
    bliebe er auch dann grün, wenn der Dispatch statisch an die erstbeste Funktion bände.

- [x] **Bug aus P3b: `?Enum` desynchronisierte den Instruktionsstrom.** Gefunden beim Bau der
  Interfaces, vorhanden seit Format 2.0.
  - `CodeDecoder.SkipType` war eine `else if`-Kette, die jedes **nicht genannte** Tag stillschweigend
    als Skalar behandelte — also den `uleb128`-Index nicht las, den `Enum` (und jetzt `Interface`)
    hinter sich trägt. Der Strom verschob sich, und der Fehler meldete sich viele Bytes später als
    „unknown opcode 0x00": eine Meldung, die nichts mehr über ihre Ursache sagt.
  - **Kein Test hat es berührt**, weil kein Beispiel und kein Testfall `?Enum` benutzte. Die
    Lehre ist dieselbe wie bei den totalen Funktionen in der IR: ein `default`, der nichts tut,
    ist stiller falscher Code. `SkipType` ist jetzt total mit `default`-Wurf.

- [x] **M7 — P5 — Exceptions und `defer`** (Format **2.3**): `lyric run examples/bank.lyr` fängt
  `InsufficientFunds` mit typed catch und lässt den `defer` beim Scope-Exit laufen.
  - **Handler-Tabelle statt expliziter Verzweigungen.** Der glückliche Pfad kostet damit nichts —
    das ist der Grund, warum jede ernsthafte VM es so macht. Die Regionen sind **Block-Bereiche**,
    keine Byte-Bereiche: dieselbe Entscheidung wie bei den Sprungzielen, und aus demselben Grund
    (zwei Vergleiche gegen die Blockzahl statt Byte-Offsets gegen Instruktionsgrenzen).
  - **Der gefangene Wert geht in einen Slot, nicht auf den Stack.** CIL schiebt ihn beim Betreten
    des Handlers auf den Operanden-Stack; das ginge hier nicht, weil der Stack an jeder
    Blockgrenze leer ist und ein Handler-Block eine Blockgrenze ist. Über einen Slot bleibt die
    Invariante intakt — dieselbe Rücksicht wie bei `mkiface`/`callvirt`.
  - **Der Typvergleich ist Gleichheit, kein Untertyp-Test** — und das ist eine Eigenschaft dieser
    Sprache: ADR-003 verbietet Inheritance, eine Klasse ist genau ihr Typ. Deshalb reicht der
    **statische** Typ an der Wurfstelle, und `throw` trägt ihn als Immediate. Wäre der Wert
    interface-typisiert, trägt der Fat Pointer ihn (P3). In C# oder Java bräuchte man dafür ein
    Typ-Tag im Objekt.
  - **`defer` registriert nichts zur Laufzeit.** Welche Rümpfe fällig sind, steht zur Compile-Zeit
    fest, also setzt das Lowering sie direkt an jeden Ausgang — Fall-through, `return`, `throw`.
    Gos Laufzeit-Stack bräuchte Closures (P6) und kostete auf jedem Pfad etwas. Der Preis ist
    Code-Duplikation je Ausgang.
  - **Der Rückgabewert wird vor den defer-Rümpfen ausgewertet.** Go hält es genauso: ein `defer`
    darf nicht mehr ändern, was `return` schon bestimmt hat. Ein Test hält das fest.
  - **Feld-Defaults nachgeliefert** (offen seit P1b): der Default ist ein *Ausdruck* und wird an
    der Konstruktionsstelle ausgewertet, nicht im Layout abgelegt. Ohne ihn lief das Gate nicht.
  - **`defer` läuft auch beim Abwickeln** (`Sprache.md` §5). Ein Scope mit `defer` bekommt eine
    `finally`-Region über seinen Blockbereich; ihr Rumpf sind dieselben Statements noch einmal.
    Der Unwinder betritt sie, räumt auf und setzt die Suche danach fort — `endfinally` ist genau
    dieses „weiter, wo ich unterbrochen wurde". Auf dem normalen Pfad wird die Region **nie**
    betreten, dort stehen die Rümpfe inline und kosten nichts.
  - **`throw` emittiert die Rümpfe deshalb NICHT inline, `return` schon.** Ein `throw` wickelt ab
    und wird von der Region bedient; ein `return` verlässt den Scope normal, da greift keine.
    Beides zu tun ließ jeden Rumpf zweimal laufen — die Regression steht als eigener Test da.
  - **Lyric hat weiterhin kein `finally`** (ADR-009). Die Region ist ein Bytecode-Träger, den
    ausschließlich `defer` erzeugt; die Sprache bleibt bei einem Schlüsselwort.
  - 17 E2E-Tests. Der wichtigste ist die Gegenprobe „ohne Wurf wird nicht gefangen" — ohne sie
    bestünde die ganze Reihe auch, wenn immer gefangen würde. Dazu die beiden Zähl-Tests: ein
    `defer` läuft **genau einmal**, auf jedem Pfad.

- [x] **M7 — P4 — Structs mit Wert-Semantik** (Format **2.2**): `lyric run examples/vectors.lyr`
  liefert 115. Zuweisung kopiert, Parameter kopiert, `struct` im `struct` kopiert mit.
  - **Ein struct-Wert ist zur Laufzeit dasselbe Slot-Array wie ein Klassenobjekt.** `newobj`,
    `ldfld` und `stfld` bleiben unverändert; es gibt kein `newstruct`. Die gesamte Wert-Semantik
    steckt in **einer** Instruktion — `structcopy` — und darin, **wo** das Lowering sie setzt.
  - **Explizit statt implizit.** Ein Kopieren im `stloc` hätte dessen Bedeutung an den Typ des
    Ziel-Slots gehängt und den Opcode polymorph gemacht — gegen die Regel aus P5/ADR-013. Explizit
    ist es in jeder Disassembly sichtbar; dieselbe Entscheidung wie bei `mkiface`.
  - **Kopiert wird an Bindepunkten, nicht bei Reads.** Ein frisch gebauter Wert (`newobj`,
    Call-Ergebnis) braucht keine Kopie — er gehört noch niemandem. Ein `HashSet<TempId>` im
    Lowerer hält das fest; ohne die Unterscheidung bekäme jedes `let p = P { … };` ein
    `structcopy` direkt hinter sein `newobj`.
  - **Die Kopie ist rekursiv über Structs, flach über alles andere.** Ein Feld vom Typ `class`
    oder `T[]` trägt eine Referenz, und die wird geteilt: kopiert wird der Wert, nicht die Welt
    dahinter. Die Rekursion braucht **keine** Zyklen-Erkennung — siehe der Sema-Befund unten.
  - **Boxed statt eingebettet.** C# und Rust legen Struct-Felder inline in den umgebenden Speicher.
    Das bräuchte Feldzugriffe über Teilbereiche und damit ein anderes Layout-Modell — Scalar
    Replacement ist eine spätere, **formatneutrale** Optimierung, keine Voraussetzung für
    Korrektheit. Dieselbe Trennung wie beim Stack-Scheduler in P5.
  - **Format 2.2**: ein Kind-Wert (`3 = Struct`), ein Typ-Tag (`0x45`), ein Opcode. Der Tag war
    seit der Einführung von `0x40` vorgesehen — „am Bytecode muss ablesbar bleiben, ob eine
    Zuweisung kopiert".
  - 13 E2E-Tests plus Golden-Fixture. Jeder prüft, **was am Original nicht passiert ist** — ein
    Test, der nur liest, bliebe auch grün, wenn ein struct sich wie eine class verhielte.

- [x] **Sema-Lücke: `struct Node { next: Node }` ging durch.** Ein Wert-Typ, der sich selbst
  enthält, ist unendlich groß; Rust meldet „recursive type has infinite size", C# CS0523.
  - Bis P4 fiel es nicht auf, weil Structs gar nicht gelowert wurden — der Compiler kam nie an den
    Punkt, an dem er ein Layout hätte bauen müssen. **Ohne die Prüfung liefe `TypeTable` in eine
    Endlosschleife**: bei einer Klasse terminiert sie über die vorab vergebene Id, ein Wert-Typ
    braucht sein Layout dagegen vollständig.
  - `LYR-SEM0056` benennt den **Zyklus**, nicht nur seine Existenz (`A -> B -> A`). Die Kette
    bricht an jeder Referenz: `class`, `T[]` und Interface sind erlaubt, mit Gegenprobe im Test.

- [x] **Ein eigener Fehler beim Bauen, wert notiert zu werden**: `LowerArgument` hatte die
  Coercion mit im `try`, dessen `catch` eigentlich nur einen unbekannten *Parametertyp*
  abschirmen sollte. Damit verschluckte es ein fehlendes `mkiface` und machte aus einer Diagnose
  **malformed IR** — der Fehler tauchte als Verifier-Befund tief im Aufrufer auf. Ein `catch`, der
  mehr umschließt als er soll, ist genau die Sorte stiller Fehler, gegen die dieses Projekt sonst
  totale Funktionen mit `default`-Wurf einsetzt.

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

**P5 steht** — als nächstes **P6 — Closures (Lifting + Environment-Objekt)**, Gate
`examples/inventory.lyr`.

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

- **Feld-Defaults** (`balance: int = 0`) werden abgelehnt statt ignoriert. Ein weggelassenes Feld im
  Initialisierer hätte seinen Nullwert; ob das erlaubt ist, sagt die Sema heute nicht. `bank.lyr`
  hängt daran.
- **Generische Klassen** bleiben `LYR-IR0001` — P8. Enums (P3b), Interfaces (P3) und Structs (P4)
  lowern inzwischen.

**Aus P2 — eine offene Ungleichbehandlung:**

- `let p = P { hp = 1 }; p.hp = 9;` ist **erlaubt** (Klassenfeld durch eine `let`-Bindung), aber
  `let xs = [1,2]; xs[0] = 9;` ist **`LYR-SEM0019`**. Beides sind Referenztypen, beide mutieren
  durch einen geteilten Verweis. `Sprache.md` §6.4 unterscheidet sie ausdrücklich („Container muss
  mut sein" vs. „`class`-Feld"), die Regel ist also nicht versehentlich — aber sie ist seit ADR-016
  schwer zu begründen, weil `T[]` jetzt genauso ein Referenztyp ist wie eine Klasse. Zu klären,
  bevor `Indexable<T>` kommt: der Setter dort wäre `mut fn`, und dann hängt die Frage an derselben
  Stelle nochmal.

- [x] **P5b-1 — `match` über Nicht-Enums**: Literale, Or-Patterns, Ranges (inklusiv und exklusiv),
  Guards, Bindungen — über `int`, `bool`, `char` und `string`. P3b hatte nur Enums geliefert,
  `match (5)` war `LYR-IR0001`.
  - **Muster verzweigen, sie rechnen nicht.** Ein Range braucht zwei Vergleiche, ein Or-Pattern
    beliebig viele; sie zu einem `bool` zu verknüpfen hieße `and`/`or` auf `bool`, und beide sind
    in dieser IR ganzzahlig — der Verifier sagt das auch. Dieselbe Lösung wie bei `&&`/`||`, die
    aus demselben Grund Kontrollfluss sind und keine Opcodes.
  - **Der letzte Arm bleibt ungeprüft**, sofern er keinen Guard hat: die Sema hat Exhaustivität
    bewiesen (`LYR-SEM0050`). Der `enums`-Golden-Snapshot ist danach **byte-identisch** — der
    gemeinsame Pfad hat den Enum-Fall nicht angefasst.
  - 20 Testfälle, jede Musterform **doppelt**: einmal treffend, einmal daneben. Ein Test, der nur
    den Treffer prüft, bliebe auch grün, wenn jedes Muster auf alles passte.

- [x] **P5b-2 — `?.` und `??=`**: beide lowern jetzt, wie `??` es seit P2b tut — als Verzweigung
  über `optissome`, nicht als Opcode. Die rechte Seite eines `??=` läuft **nur**, wenn der Slot
  leer ist; `?.` greift **nicht** zu, wenn der Träger keinen Wert hat. Genau deshalb geht es nicht
  als Instruktion: eine Stack-Maschine kann keinen unausgewerteten Ausdruck transportieren. Das
  `optget` im Some-Zweig kann nie panicken — der Beweis steht im `optissome` davor, dieselbe
  Arbeitsteilung wie beim Flow-Narrowing.

- [x] **P5b-3 — `string +`/`*` und `panic`**: alle drei lowern jetzt zu Calls, keiner zu einem
  Opcode. `add` bliebe sonst polymorph und müsste zur Laufzeit Typ-Dispatch machen — genau das,
  was ADR-013 vermeidet. `std.string.repeat` ist neu, `std.core.panic` auch: **`panic` war ein
  M6-Lieferposten und wurde nie geliefert.**
  - **`panic` versiegelt seinen Block** — Rückgabetyp `never` (§9). Der Rückgabewert von
    `LowerStmt` („fällt der Kontrollfluss durch?") muss das melden, sonst versucht der Aufrufer,
    denselben Block ein zweites Mal zu versiegeln. Das ist derselbe Mechanismus, den `return` und
    `throw` schon benutzen — ein *Ausdruck*, der ihn auslöst, war neu.
  - Ein negativer Wiederholungsfaktor liefert den leeren String statt zu werfen: die Spec kennt
    dafür keinen Fehlerfall, und eine .NET-Ausnahme mitten in einem Lyric-Programm wäre die
    falsche Antwort.

- [x] **P5b-4 — `fn main(args: string[])` meldet sich**. Es fiel bisher durch die
  Entry-Bedingung, das Modul bekam keine Start-Sektion, und der Compiler sagte **nichts**: ein
  Programm, das sauber übersetzt und dann als „Bibliothek" nicht startet. Jetzt `LYR-IR0001` mit
  der Stelle. Gebaut ist es damit nicht — aber „noch nicht gebaut" ist genau, was der Code sagt.

- [x] **P5b-5 — Default-Argumente und `params`**: beide sind reine
  **Aufrufstellen-Transformationen**. Der Callee sieht einen gewöhnlichen Parameter bzw. ein
  gewöhnliches `T[]`; die IR kennt weder optionale noch variadische Signaturen und soll auch
  keine kennen. Nach dem Lowering ist ein Aufruf ein Aufruf. Ein Default wird **pro Aufruf**
  ausgewertet (wie in C#) — sonst teilten sich zwei Aufrufe ein Objekt.
  - **Ein fertiges Array darf an `params` durchgereicht werden** (`Sprache.md` §3.1 ergänzt).
    Entscheidend war nicht die Bequemlichkeit, sondern dass eine variadische Funktion sonst an
    **keine andere delegieren** kann — `fn logged(params xs: int[]) { return sum(xs); }` wäre
    unmöglich, und genau solche Hüllen bauen C#s `WriteLine`-Überladungen intern.
  - **Eindeutig ist es, weil Lyric zwei Dinge nicht hat**, die C# hat: implizite `T` ↔
    `T[]`-Konvertierung und Overloading. C# braucht dafür „normal form vs expanded form" in der
    Überladungsauflösung; hier entscheidet der Typ des Arguments. Bei `params xs: int[][]` ist ein
    Element `int[]` und das Array `int[][]` — verschiedene Typen, kein Konflikt. Ein Test führt
    beide Fälle nebeneinander.

- [x] **P5c — Konstanten: Globals-Sektion, Format 2.4**. Modul-`let` und `static let` lowern jetzt;
  beide sind **derselbe Mechanismus** (ein globaler Slot), der Unterschied ist nur, wo der Name
  sichtbar ist. Das schließt die letzte Lücke aus P1b.
  - **Eine Init-Funktion, keine Werte in der Sektion.** Ein Initialisierer ist ein *Ausdruck*, kein
    Literal — `static let ZERO: Vector3 = Vector3 { … }` legt ein Objekt an. Als Wert im Bytecode
    wäre nur der skalare Teil darstellbar, der Rest bräuchte doch wieder Code. Die synthetische
    `<globals>`-Funktion kann alles, was das Lowering ohnehin kann, und der Instruktionssatz
    bekommt keinen Sonderfall. CIL löst es mit `.cctor` genauso.
  - **Gegen Inlining entschieden**: es dupliziert den Initialisierer an jede Verwendungsstelle und
    funktioniert nur für Skalare — für alles andere bräuchte es *zusätzlich* Slots. Zwei
    Mechanismen für ein Konzept, genau das, was `CONTRIBUTING.md` verbietet.
  - **`GlobalTable` sammelt vollständig vorab**, anders als `TypeTable`/`ImportTable`, die bei
    Bedarf internieren: die Init-Funktion muss *jeden* Slot füllen, also darf keiner erst durch
    eine Verwendung entstehen.
  - **`LYR-SEM0057` — benutzt, bevor es initialisiert ist.** Reihenfolge ist
    Deklarationsreihenfolge (wie C#s Feld-Initialisierer; Go sortiert stattdessen topologisch).
    Ohne diese Meldung lieferte der Lookup eines noch nicht berechneten Globals still `ErrorType`
    — „schon gemeldet" — die Sema schwieg, und das Lowering stürzte später ab. **Zum dritten Mal
    in M7 dieselbe überladene Invariante.** Nur *innerhalb* eines Initialisierers gilt die Regel;
    aus einem Funktionsrumpf ist jede Konstante lesbar.
  - Ein Leser lehnt ab: globaler Typ `void`, Init-Index außerhalb des Aufrufraums, und Slots
    **ohne** Init-Funktion — die wären uninitialisiert, und jeder Wert in Lyric hat einen.
  - Gate: `examples/constants.lyr` → **140**. Es benutzt Modul-`let`, `static let` auf Klasse
    *und* Struct, ein objektwertiges Global, eine Konstante, die eine frühere liest, und eine
    Funktion, die eine *später* deklarierte liest — jede Regel des Slice steht drin. **Dabei
    fiel auf, dass `RunnableExamples` seit P3 nicht mitgewachsen war**: `interfaces.lyr` (140)
    und `vectors.lyr` (115) liefen längst, standen aber in keiner Kommando-Matrix. Jetzt drin.

- [x] **Attribute nach post-v1 vertagt** (`Sprache.md` §10 umgeschrieben, `LYR-PAR0038`).
  Der Lexer erkennt `@name` weiter, die Syntax bleibt reserviert; Parser und Sema lehnen mit einer
  Meldung ab, die den Grund nennt statt „expected a declaration". **M9 verliert `lyric test`** —
  es sammelt `@test`-Funktionen, und die Grammatik dafür hat nie existiert (§2.3 sieht an einer
  Deklaration kein Attribut vor). Die Lücke zu schließen hieße, hier eine Sprachentscheidung für
  ein Werkzeug-Thema zu treffen.

**Lieferposten-Inventur 2026-08-06** (Details als Blockzitat vor M8 in der ROADMAP,
Skript: `tools/inventur.py`): 38 Konstrukte aus `Sprache.md` durch Parser/Sema/Lowering gefahren,
**12 laufen durch**. Was **keinem Slice gehoert**: Attribute an Deklarationen
(`@test` hat keine Grammatik — betrifft M9), `fn main(args)` (erzeugte **stumm** ein
Bibliotheks-Modul), Default-Argumente, `params`, `extend`, Tupel, Konstanten (Modul-`let` und
`static let`). **Erledigt in P5b**: `match` ueber Nicht-Enums, `?.`, `??=`, `string +`/`*`, `panic`,
`fn main(args)`, Default-Argumente, `params`. **Erledigt in P5c**: Konstanten. **Ohne Slice bleiben**
`extend` (Vorschlag P9) und **Tupel**. Die Restliste von M7 ist deshalb P6, P7, P8 und **P9**.

**Aus P5 — bewusst offen und wichtig:**

- **`catch (e)` ohne Typ** ist `LYR-IR0001`: der Slot bräuchte den `Throwable`-Typ als Interface,
  und das hängt an der Builtin-Konformanz (M8). `catch (_)` und `catch (e: T)` gehen.

**Aus P4 — bewusst offen:**

- **Struct-Feldzuweisung über eine immutable Bindung.** `fn f(p: P) { p.x = 9; }` ist
  `LYR-SEM0019`, aber `p.shift(9)` mit `mut fn` geht durch — obwohl beides denselben Effekt auf
  dieselbe Kopie hat. `Sprache.md` §6.4 verlangt für ein struct-Feld eine `mut fn`; die Asymmetrie
  ist damit gedeckt, wirkt aber willkürlich, sobald der Empfänger ohnehin eine Kopie ist.
- **Kein Scalar Replacement.** Jede Kopie allokiert. Formatneutral nachrüstbar.

**Aus P3 — eigene Befunde, nicht behoben:**

- **Parser: `StructInit` rechts von `=` im Statement-Kontext.** `s = Small { n = 5 };` scheitert
  mit `LYR-PAR0016`, obwohl `Sprache.md` §6.2 den Ausdruck „in jeder Wert-Position" erlaubt — die
  Mehrdeutigkeits-Sperre gilt dort nur dem **Anfang** eines `ExprStmt`, greift aber auf die ganze
  Zuweisung durch. Umgehung ist eine Fabrik; ein E2E-Test hält den Fall fest.
- **Kein `Damageable[]`** — ein Array über einen Interface-Typ typt, aber die Elementkonvertierung
  läuft noch nicht durch `Coerce`. Hängt ohnehin an Generics (P8).

**Aus dem Toolchain-Split (ADR-017):**

- **`fn main(args: string[])` ist nicht verdrahtet.** `ModuleLowerer` nimmt nur ein parameterloses
  `main` als Einstieg, obwohl `Sprache.md` §11 beide Formen kennt. Der Runner-Vertrag sieht
  `-- <args>` vor; übergebene Argumente werden deshalb mit `LYR-CLI0007` **abgelehnt** statt still
  verworfen. Arrays gibt es seit P2, es fehlt also wenig — aber es ist ein eigener Posten.
- **`dotnet publish` ist nicht konfiguriert.** Drei Apphosts müssen in *ein* Ausgabeverzeichnis,
  sonst liegt die Runtime bei `--self-contained` dreifach vor (~210 MB statt ~70 MB je Plattform).
  Fällig, wenn v1.0-Binaries gebaut werden.
- **CI-Vermerk**: `.github/workflows/ci.yml` installiert `dotnet-version: '9.0.x'`, alle Projekte
  zielen aber auf `net10.0`. Läuft heute nur, weil die Runner .NET 10 vorinstalliert haben. Nicht
  im Rahmen dieses Slices angefasst.
- **Verifier-Anteil neu zu messen**, im Release-Profil. Die Debug-Zahlen aus `--verbose` sind von
  JIT-Aufwärmen durchsetzt (`read` allein 9 ms für eine 8-Zeilen-Datei) und taugen nur als
  Größenordnung.
- **Sektions-Byte-Größen fehlen in `lyrvm info`**: `BytecodeModule` behält sie nicht, der Reader
  verwirft sie nach dem Parsen. Sie nachzurüsten hieße, das Modell um Herkunftsdaten zu erweitern
  — eigene Entscheidung, kein Nebenprodukt.

**Aus M6:**

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

`M7: Exceptions und defer - Bytecode 2.3 (P5)`

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
