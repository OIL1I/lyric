# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M7 — Objektmodell + VM (full) — abgeschlossen.** Slices P1 bis P9 stehen.

Bytecode-Format **2.5**. 1606 Tests grün. Das Gate `examples/inventory.lyr` läuft: es belastet
Interface mit Default-Methode, `::`-Konformanz, `extend`, Closure als Parameter, generische
Funktion mit Constraint, `match` auf einem Enum mit Payload und Nullable-Rückgabe **gleichzeitig**
— und hat dabei drei Lücken gefunden, die kein Einzelslice bemerkt hatte (siehe unten).

Damit läuft die Sprache von der Quelle bis zur Ausführung für alle 38 Konstrukte aus
`Sprache.md`. **`extend` war das letzte ohne Slice.**

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst (Meilenstein-Kopf stand auf M6, „woran wir arbeiten" auf P5, die Verifier-Laufzeit einmal
> als widerlegt und zwanzig Zeilen später wieder als Tatsache). Sie ist auf ihre eigene Pflegeregel
> zurückgeschnitten: letzte Slices, offene Punkte, Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **M8 — S2 — `string` wird ein richtiger Typ.** Zwoelf Natives, ein `StringIterator`,
  `for (c in s)`. 1606 Tests gruen.
  - **Alles zaehlt Codepoints**, weil `Sprache.md` §4 `char` als Codepoint definiert. Eine
    Laenge, die etwas anderes zaehlt als die Iteration liefert, waere ein Widerspruch im eigenen
    Typsystem — C#, Java und JavaScript haben genau den, weil ihr `char` eine UTF-16-Einheit ist.
    Gemessen: `length("a😀b")` ist **3**; C# sagt 4.
  - **Kein `s[i]`** — und das war vorher erlaubt, fiel erst im Lowering um. Eine
    Codepoint-Position kostet O(n), also waere die naheliegende Indexschleife quadratisch, ohne
    dass man ihr das ansieht. Rust verbietet die Indizierung aus demselben Grund. Die Meldung
    nennt beide Auswege (`charAt`, `for (c in s)`), weil ein blosses „not indexable" wie ein
    fehlendes Feature klaenge statt wie eine Entscheidung.
  - **`for (c in s)` laeuft ueber `toChars`, nicht ueber `charAt`.** Ein Iterator, der pro
    Schritt `charAt` riefe, waere quadratisch — einmal O(n) plus ein Array statt n-mal O(n).
  - **Natives duerfen jetzt Arrays liefern.** Die alte Regel war „nur Skalare"; `split` und
    `toChars` brauchen mehr. Die Linie bleibt scharf: ein Array hat, anders als eine Klasse,
    **kein Layout**, das der Host kennen muesste. Der Elementtyp wird beim Binden mitgeprueft,
    sonst waeren `string[]` und `char[]` ununterscheidbar.
  - **`Sprache.md` §4 ist korrigiert.** Dort stand „UTF-8 Fat-Pointer `{ data, length }`" — das
    schrieb der Runtime ihre Datenstruktur vor, was der Spec nicht zusteht (ADR-013 regelt das
    *Format*), und war zudem **falsch**: die .NET-Runtime haelt Strings als UTF-16. Die Divergenz
    war unbeobachtbar, solange es weder `length` noch Indizierung gab. Mit S2 waere sie es
    geworden.

- [x] **M8 — S1 — Builtin-Konformanz.** `console.writeln(42)`, `writeln(true)`, `writeln("hi")`,
  `writeln('x')`, `writeln(2.5)` laufen. 1590 Tests grün.
  - **Es braucht kein Boxing, und das war die zentrale Messung.** Ich hatte in P9b das Gegenteil
    vermutet. `extend int :: [Display]` kam schon immer durch die Sema; die Meldung kam aus dem
    Lowering, weil `LowerConstraintCall` aufgab, sobald `TypeFacts.SymbolOf` kein Symbol lieferte.
    Die Monomorphisierung macht aus `writeln(42)` einen **direkten** Aufruf — es entsteht nie ein
    Fat Pointer, also nie ein Boxing-Bedarf. Kein Format-Bump, keine `LyrValue`-Änderung.
  - **Damit ist ADR-015 belegt statt behauptet.** Overloading wurde vertagt mit dem Argument,
    `println` wolle `println<T :: [Display]>` und keine drei Überladungen. Jetzt ist es gebaut.
  - **Die Sema lässt Builtins bei Constraints nicht mehr blind durch.** Der Kommentar in
    `Satisfies` sagte wörtlich „Builtins/extern: lenient (Conformance erst M8)" — `render(42)`
    wurde auch dann angenommen, wenn niemand `int` erweitert hatte, und der Fehler kam als
    `LYR-IR0001` aus dem Lowering, weit weg von der Ursache.
  - **`std.core` ist ohne Import sichtbar** — dieselbe Begründung wie bei `panic` (§9) und
    `coroutineEnded` (§8), die der Compiler anbindet, ohne dass jemand sie importiert haben
    könnte. Ohne die Regel müsste jedes Programm `std.core` importieren, nur damit
    `console.writeln(42)` den Constraint erfüllt — obwohl es `std.core` nirgends nennt.

- [x] **M8 — S1a — Extension-Methoden nur lowern, wenn benutzt.**
  - **S1 hatte einen Preis, und drei Tests haben ihn sofort angezeigt.** `std.core` wird immer
    geladen, also trug plötzlich *jedes* Programm die fünf `Display`-Extensions und vier
    `std.string`-Importe — auch ein `hello.lyr`, das keine davon anfasst. Die Tests messen genau
    das Richtige; sie anzupassen wäre Schönfärberei gewesen.
  - **`ExtensionTable` in derselben Worklist-Form wie `LambdaTable`/`InstanceTable`**: die Id
    entsteht bei der *Anforderung*, gelowert wird danach. Damit gilt für Extensions dieselbe
    Regel wie für Typen und Importe seit jeher — **im Bytecode steht nur, was benutzt wurde**.
  - **Die vtable braucht eine Vorrunde**: `extend A :: [I]` wird gebraucht, sobald ein `A` in
    einem `I`-Slot landet, auch wenn die Methode nirgends direkt gerufen wird. `BuildImpls` läuft
    deshalb vor dem Anhängen der letzten Funktionen, danach dreht die Worklist noch eine Runde.
  - **Vier Durchreichungen sind vier Gelegenheiten zu vergessen.** Der erste Versuch fädelte die
    Tabelle als Parameter durch den `FunctionLowerer` — und bekam sie weder in
    `InstanceTable.LowerAll` (also brach `writeln`, eine generische Funktion) noch in
    `ExtensionTable.LowerAll` selbst (also brach eine Extension, die eine andere ruft). Sie hängt
    jetzt am `TypeTable`, den ohnehin jeder Lowerer hat.

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

**M8 — Stdlib.** S1 (Builtin-Konformanz) und S2 (`string`) stehen. Als naechstes **S3 —
`std.fmt` und Format-Specs**: `f"{x:N2}"` ruft dann `std.fmt.format(value, spec)` statt der
`fromXxx`-Wandler, und `Display` liefert den Default. Damit laufen `shapes.lyr` und `stats.lyr`,
die seit M6 warten.

Danach **S4** (`Throwable` als Interface → `catch (e)` ohne Typ), **S5** (`std.collections`),
**S6** (Capabilities, ADR-007), **S7** (`std.io.file`, `std.os`, `std.math`), **S8** (Gate:
`wc`-Klon).

**Vor S5 gehoert eine Sprachentscheidung getroffen** — sie steht unten unter „Noch offen" und
wird mit `Indexable<T>` zum zweiten Mal faellig.

**`std.io.net` ist aus M8 gestrichen** und steht in der v1.X-Tabelle (Begruendung in der
ROADMAP): was ein blockierender Socket in einer Single-Thread-VM bedeutet, ist eine Entscheidung
ueber das Nebenlaeufigkeitsmodell und kein Nebenprodukt eines Stdlib-Meilensteins.

## Noch offen

**Sprachlücken, vor v1 zu schließen:**

- **`b?.get()` geht nicht** — Optional-Chaining mit *Methodenaufruf*. Die Sema macht `?.get` zu
  einem `?fn() -> int` und stolpert dann über das `()`. Feldzugriff (`b?.v`) funktioniert.
- **Die Konformanz prüft die Definition statt der Typargumente**: `Ones :: [Src<int>]` würde auch
  für `Src<string>` akzeptiert.
- **`catch (e)` ohne Typ** ist `LYR-IR0001`: der Slot bräuchte `Throwable` als Interface, das hängt
  an der Builtin-Konformanz (M8). `catch (_)` und `catch (e: T)` gehen.
- **Parser: `s = Small { n = 5 };`** scheitert mit `LYR-PAR0016`, obwohl §6.2 den Ausdruck „in jeder
  Wert-Position" erlaubt — die Mehrdeutigkeits-Sperre gilt dem *Anfang* eines `ExprStmt`, greift
  aber auf die ganze Zuweisung durch. *(Bekannt seit P3 — und am 2026-08-07 beim Schreiben einer
  Messprobe erneut hineingelaufen, ohne ihn wiederzuerkennen. Er kostet real Zeit.)*
- **Generics-Rest aus M4**: Constraints mit eigenen Typ-Args über die Grenze substituieren;
  `Opt.Some(5)` typt ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath nicht ausdrückbar.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.
- **`char as int` ist kein erlaubter Cast** (`LYR-SEM0006`). Beim Schreiben der S2-Tests
  aufgefallen. Ob das gewollt ist, sagt §6.5 nicht eindeutig — ungeprüft gelassen, weil eine
  Cast-Regel eine Sprachentscheidung ist und kein Nebenprodukt.
- **`DeclaredTypes.Lower` wirft ungefangen** aus `ModuleLowerer.Lower` heraus: eine native
  Signatur mit einem unbekannten Typ gibt einen Compiler-*Absturz* statt einer Diagnose. Gefunden
  beim Bau von S2, als `split` noch nicht lowerbar war.

**Zwei Ungleichbehandlungen, die eine Entscheidung brauchen (keine Bugs):**

- `let p = P { hp = 1 }; p.hp = 9;` ist **erlaubt**, `let xs = [1,2]; xs[0] = 9;` ist
  **`LYR-SEM0019`**. §6.4 unterscheidet sie ausdrücklich, aber seit ADR-016 ist `T[]` genauso ein
  Referenztyp wie eine Klasse. **Zu klären, bevor `Indexable<T>` kommt** — dort hängt dieselbe
  Frage am `mut fn`-Setter nochmal.
- `fn f(p: P) { p.x = 9; }` ist `LYR-SEM0019`, aber `p.shift(9)` mit `mut fn` geht durch — obwohl
  beides denselben Effekt auf dieselbe Kopie hat.

**Werkzeug und Format:**

- **Source-Map-Sektion** (Id 6) ist reserviert und beschrieben, wird aber nicht geschrieben —
  Panics zeigen deshalb die Funktion, nicht die Zeile.
- **Sektions-Byte-Größen fehlen in `lyrvm info`**: der Reader verwirft sie nach dem Parsen. Sie
  nachzurüsten hieße, das Modell um Herkunftsdaten zu erweitern — eigene Entscheidung.
- **`std.fmt.format` nach M8** (Format-Specs `{x:N2}` in `shapes.lyr`/`stats.lyr`).
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

`M8: string als richtiger Typ (S2)`

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
