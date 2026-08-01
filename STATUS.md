# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M5 — IR + Bytecode — Exit-Kriterium erreicht, Tag `v0.1.0` ausstehend**

Slices P1–P5 stehen: IR-Datentypen, Printer + Goldens, Verifier, Lowering AST → IR, Bytecode-Format
+ Writer/Reader/Disassembler. Die Pipeline läuft durch: `lyric build examples/arith.lyr` erzeugt
`.lyrbc`, `lyric disasm` zeigt sinnvolle Instruktionen. ADR-006 und ADR-013 sind umgesetzt,
`docs/Bytecode.md` ist normativ geschrieben. 1107 Tests grün.

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

## Woran wir gerade arbeiten

M5 ist inhaltlich fertig. Offen: **`v0.1.0` taggen** (CONTRIBUTING §Releases), danach **M6 — VM**.

## Noch offen

**Aus M5:**

- `v0.1.0` taggen und Release-Seite anlegen.
- `ModuleLowerer.Lower` verifiziert per Default immer. Für Release-Builds fehlt noch der Schalter,
  der das abstellt (heute nur als Parameter, nicht an einer Build-Konfiguration).
- **Source-Map-Sektion** (Id 6) ist in der Spec beschrieben und reserviert, wird aber noch nicht
  geschrieben. Braucht M6 für Runtime-Fehler mit Zeilenangabe.
- **Copy-Propagation im Emitter**: ein Temp mit mehreren Lesern erzeugt heute ein
  `ldloc`/`stloc`-Paar, das ein Optimierer einsparen könnte. Format-neutral nachrüstbar.
- **Generics**: Richtung entschieden (Worklist im Lowering, ab den Wurzeln, eine Instanz pro
  Typargument-Tupel), Bau offen. Der Substitutions-Haken sitzt in `FunctionLowerer.LowerType`,
  `NameMangling` ist die Stelle für die Typargumente im Namen.

**Doku-Fehler (beim Schreiben von `arith.lyr` aufgefallen):**

- `Doku.md` §9.1 und §16.1 zeigen if-**Ausdrücke** mit Blöcken
  (`if (n > 0) { 1 } else { 0 }`). Der Parser lehnt das ab (`LYR-PAR0002`), und `Sprache.md` §6.2
  definiert die Zweige als **Ausdrücke** (`if (n > 0) 1 else 0`) — Blöcke haben in Lyric keinen
  Wert. `Sprache.md` gewinnt laut eigenem Header; `Doku.md` muss an beiden Stellen nachgezogen werden.

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

`M5: Bytecode-Format, Writer, Reader und Disassembler (P5)`

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
