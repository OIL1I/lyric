# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M8b — Stdlib-Erweiterung — läuft.** S1 bis S5 plus die Erreichbarkeitsanalyse.

2343 Tests grün, Bytecode-Format **2.5**, **vier** Binaries, Version **0.9.0**.

**Die Vorgabe für M8b**: *so viel wie möglich in Lyric selbst.* Nativ bleibt nur, was eine echte
Host-Grenze ist — stdin, Datei-I/O, Zeit, `sqrt`/`sin`/`cos`. Alles andere ist Lyric-Code:
`Map`, Merge Sort, der FNV-Hash für Strings, sämtliche Iterator-Adapter. Dass eine Stdlib sich
selbst tragen kann, ist die eigentliche Aussage dieses Meilensteins — und der schärfste Test der
Sprache, den es bisher gab: **zehn Compiler-Lücken** sind dabei aufgefallen, die kein
Meilenstein davor berührt hat.

**Offen für v1.0**: `Set<T>`, der Rest von M8b (siehe `docs/IDEAS.md`-Liste), die
Erreichbarkeitsanalyse, dann M10 — die Embedding-API (`LangVm`, Marshalling, Hot-Reload).

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **Erreichbarkeitsanalyse im Lowering** (2026-08-08). Ein Lyric-Rumpf landete im Bytecode,
  sobald sein Modul geladen war — auch wenn niemand ihn rief. Gemessen, jeweils mit erweiterter
  `std.string`:

  | | vorher | nachher | | | vorher | nachher |
  |---|---|---|---|---|---|---|
  | hello | 968 B | **301 B** | | stats | 1501 B | **837 B** |
  | arith | 828 B | **437 B** | | shapes | 1888 B | **1224 B** |
  | fizzbuzz | 1092 B | **528 B** | | inventory | 2321 B | **1657 B** |

  - Ein Hello-World trägt jetzt **9 Bytes Code** — genau `main`, ohne ein Byte fremder Stdlib.
  - **Auf der IR und nicht davor**: dort stehen die Aufrufe schon als Instruktionen. Eine Analyse
    auf AST-Ebene müsste den Aufrufgraph mit Überladungsauflösung, Extensions und
    Monomorphisierung nachbauen — ein zweiter Compiler neben dem ersten.
  - **Virtuelle Aufrufe** sind der harte Teil, und die tragende Beobachtung ist: ein
    Interface-Wert entsteht *ausschliesslich* durch `mkiface`. Die erste Fassung nahm jede
    vtable-Zeile als Wurzel — sicher und wirkungslos.
  - **Der Beinahe-Unfall**: bei einem untypisierten `catch (e)` baut die **VM** den
    Throwable-Fat-Pointer selbst, im Code steht kein `mkiface`. Ohne diesen Fall suchte das
    Programm zur Laufzeit eine Implementierung, die es nicht mehr gab. Dazu ist `Throw` ein
    *Terminator* und stand nicht in den durchsuchten Instruktionen — zwei Lücken an derselben
    Stelle, gefangen von zwei Tests.
  - Bibliotheken ohne Einstiegspunkt bleiben unangetastet: dort ist jede öffentliche Funktion eine
    mögliche Wurzel.

- [x] **M8b/S5 — `std.string`** (2026-08-08). 30 Funktionen, **alle in Lyric**: Zeichen-Prädikate,
  `join`/`replace`/`splitLines`/`splitFirst`, `trimStart`/`trimEnd`/`padStart`/`padEnd`,
  `parseInt`/`parseIntRadix`/`parseFloat`/`parseBool`, `fromChars` und der `StringBuilder`, den
  `Doku.md` seit M6 erwähnt und den es nie gab.
  - Möglich nur durch **ADR-022**: seit `char` eine Zahl ist, sind `isDigit` und `parseInt`
    gewöhnliche Arithmetik. Vorher hätte jede eine native Signatur gebraucht.
  - Das Modul importiert **nichts** und darf das auch nicht — `std.core` importiert `std.string`.

- [x] **M8b/S4 — `Set<T>`, Map durchlaufen, List durchsuchen** (2026-08-08).
  - `Set<T>` ist **nicht** als `Map<T, bool>` gebaut: das verschwendet ein Array und macht `add(x)`
    zu `set(x, true)`.
  - **Eine `Map` war nicht durchlaufbar** — `keys()` und `values()` fehlten, weil sie am selben
    Compiler-Fehler hingen wie `Set.iter()`.
  - `LowerWithOwner` war eine Teilkopie der Typ-Auflösung und zum **dritten** Mal zu kurz: erst der
    nackte Fall, dann `?T`, dann `T[]` — ein generischer Typ als Rückgabetyp fehlte immer noch.
    Der Kommentar darüber beschrieb den Fehler bereits, eine Generation zu früh.

- [x] **M8b/S4 — `Map<K, V>` und Merge Sort** (2026-08-08). Beide in Lyric.
  - **`Map`**: Open Addressing mit linearer Sondierung, drei flache Arrays statt einer Liste je
    Bucket. Der Schlüssel trägt zwei Constraints (`K :: [Hashable<K>, Equatable<K>]`), getrennt
    weil es keine Interface-Vererbung gibt — die Klasse ist damit der Grund, warum ADR-024 und der
    M4-Constraint-Fix gebaut wurden.
  - **Grabsteine**: ein gelöschter Slot darf die Sondierungskette nicht unterbrechen, sonst ist ein
    dahinterliegender Schlüssel unauffindbar — und zwar lautlos. Sie zählen beim Load Factor mit,
    weil sie Suchzeit kosten wie belegte Slots.
  - **Merge Sort**, bottom-up: stabil und garantiert O(n log n). Quicksort wäre schneller, ist aber
    instabil und bei sortierter Eingabe quadratisch — für eine Standardbibliothek die falsche
    Überraschung. Dieselbe Wahl wie Python/Java (Timsort), C++ `stable_sort`, Go `sort.Stable`.
  - Der Stabilitäts-Test ist der wichtigste: an einer sortierten Zahlenliste ist Stabilität **nicht
    ablesbar**. Ohne ihn wäre ein instabiler Algorithmus grün.

- [x] **M8b/S3 — `std.iter`** (2026-08-08). Sechs Adapter (`map`, `filter`, `take`, `skip`,
  `takeWhile`, `chain`) und zwölf Terminatoren, dazu `collect` in `std.collections`. Kein einziger
  nativer Aufruf.
  - **Faul, nicht eifrig** — und das ist am Ergebnis nicht ablesbar: `take(map(…), 2)` liefert
    dasselbe, ob die Closure zwei- oder fünfmal lief. Der Test zählt die Aufrufe mit einem
    Seiteneffekt; ohne ihn wäre ein eifriger Adapter grün.
  - `collect` gehört nach `std.collections`, nicht nach `std.iter`: die Abhängigkeit läuft dorthin.

- [x] **M8b/S2 — `Equatable`, `Hashable`, `Ordered`** (2026-08-07, ADR-024). Generisch, weil ein
  Interface als Parametertyp einen Interface-*Wert* verlangt und ein Skalar keiner sein kann —
  `extend int :: [Equatable]` wäre unmöglich und `Map<int, V>` gäbe es nicht.
  - `string.compare` (lexikographisch über Codepoints, ohne Locale) und `string.hash` (FNV-1a in
    acht Zeilen) **in Lyric** — möglich, weil `char` seit ADR-022 eine Zahl ist.
  - **Kein `Hashable` für `float`**, mit Begründung im Code: `NaN != NaN` hieße, dass ein Schlüssel
    sich selbst nicht wiederfindet.
  - `uint` fehlte in der ersten Fassung und fiel nur durch den **Gegentest** auf — 32 grüne Tests
    sind kein Beleg, dass geprüft wird, solange kein Fall scheitert.

- [x] **M8b/S1 — Eingabe in `std.io.console`** (2026-08-07). `readLine`/`readAll`/`readChar`/
  `isInteractive`/`flush`/`eprint` nativ, `prompt` und `lines()` in Lyric. Bis dahin konnte das
  Modul **ausschließlich schreiben** — es gab keine interaktiven Programme.
  - `readChar` setzt Surrogatpaare zusammen: .NETs `Read()` liefert UTF-16-Einheiten, Lyrics
    `char` ist ein Codepoint, und eine Surrogathälfte ist seit ADR-022 kein gültiger `char`.

- [x] **Zehn Compiler-Lücken, beim Bau der Stdlib gefunden** (2026-08-07/08). Fünf davon hätten
  jeweils einen ganzen Slice blockiert:
  - **Feld-Defaults wurden nie geprüft** — `K { }` mit einem Default-Feld war ein *Absturz*, weil
    das Lowering den Default auswertet und keinen Typ fand. `lyric check` sagte „ok".
  - **Coercion Klasse → Interface fehlte an generischen Aufrufen**: der Parametertyp wurde ohne
    die Substitution der Aufrufstelle gelowert, der `catch` reichte das Argument *ohne* Coercion
    durch. Hätte `std.iter` vollständig blockiert.
  - **Ein Lambda-Lowerer bekam grundsätzlich `NoSubstitution`** — `(a: T, b: T) => …` in einer
    generischen Funktion brach ab. Ein Lambda erbt den generischen Kontext seines Rumpfes.
  - **`f([])`** stürzte ab, während `let xs: int[] = []` ging: die zweiphasige Inferenz typt
    Nicht-Lambda-Argumente ohne Kontext.
  - **Die Poison-Regel griff nur an der Oberfläche** — ein `fn(int) -> <error>` erzeugte
    Folgemeldungen, die die eigentliche Ursache zudeckten.
  - Dazu **A + B**: Typargumente werden jetzt durch eine Konformanz hindurch inferiert
    (`collect(doppelt)` statt `collect<int>(…)`), und ein nicht bindbarer Typ-Parameter ist eine
    Sema-Diagnose (`LYR-SEM0060`) statt eines stillen `<error>`.

- [x] **M4-Rest: Constraints tragen ihr eigenes Typargument** (2026-08-07).
  `fn same<T :: [Eq<T>]>(a: T, b: T)` geht — explizit, inferiert, mit Nutzertypen und über zwei
  generische Funktionen hinweg weitergereicht. **ADR-024 ist damit nicht mehr blockiert**;
  `Map<K :: [Hashable<K>], V>` ist formulierbar.
  - Der Fehler war ratlos formuliert (*„cannot assign 'T' to 'T'"*) und die Ursache klein:
    `MemberOfTypeParam` gab den Methodentyp aus dem Constraint-Interface **roh** zurück.
    `InterfaceWithSubst` lag schon da, und `CheckTypeConformance` benutzte es seit jeher richtig —
    **zum siebten Mal** dasselbe Muster: eine Frage, zwei Stellen, nur eine mit der Antwort.
  - **`LYR-SEM0055` war doppelt vergeben.** Der Code gehört „Member falsch benutzt" (vier
    Stellen); die `==`-Diagnose aus dem Sweep bekommt `LYR-SEM0059`. Selbst verursacht, beim
    Sweep zwei Stunden vorher.

- [x] **ADR-022 — `char` ist ein Ganzzahltyp mit geprüftem Wertebereich** (2026-08-07).
  `c as int`, `n as char`, `c < 'z'`, `c + 1` und die bitweisen Operatoren gehen. Ein Ergebnis
  außerhalb des Unicode-Bereichs ist ein `panic` (`LYR-VM0012`), geprüft in `LyrValue.Normalize` —
  dem **einzigen** Weg, auf dem ein Skalar-Ergebnis entsteht. An den vier Rufstellen einzeln zu
  prüfen hieße, dieselbe Regel viermal zu schreiben. Ein Literal außerhalb des Bereichs ist schon
  ein Typfehler.
  - Die Codepoint-Grenze stand **fünfmal** im Projekt und liegt jetzt einmal in
    `Lyric.Core.Unicode`, wo Sema, Verifier und VM sie gemeinsam sehen.
  - **Mitgefixt**: `a + 1` mit `a: int8` ließ den Compiler abstürzen. `UnifyNumeric` prüfte den
    Literal-Fit, notierte den angepassten Typ aber nicht — jeder schmale Ganzzahltyp war
    betroffen. `char` war nur neu genug, dass jemand `c + 1` ausprobiert hat.

- [x] **f-Strings mit schmalen Skalaren** (2026-08-07). `f"{x}"` mit `x: int8` ließ den Compiler
  abstürzen, auf **beiden** Wegen (mit Spec über `std.fmt`, ohne über `std.string`). Die Wandler
  heißen `fromInt`/`fromFloat` — Einzahl, weil Lyric kein Overloading hat — und nehmen den
  breitesten Typ; das Lowering reichte ungewidert durch. Der Test ist eine Theory über alle
  Breiten, weil ein Beispiel denselben Zufall wiederholen würde, dem der Fund zu verdanken war.
  - *Nicht behoben, aber dokumentiert*: `f"{u}"` mit `u: uint` jenseits `int64.MaxValue` druckt
    eine negative Zahl. Der Fix wäre ein eigener `fromUint`.

- [x] **ADR-023 — Struct-Felder sind schreibbar wie class-Felder** (2026-08-07). `LYR-SEM0019`
  fällt für `let`-gebundene Structs und Struct-Parameter. `this.feld` in einer Methode ohne `mut`
  bleibt verboten — das ist die Zusage von `mut fn` und das Einzige, was die alte Regel je
  geschützt hat. Der Anlass war als Parameter-Asymmetrie notiert, betraf aber `let` genauso und
  dort schwerer: `p.shift(9)` mit einer `mut fn` änderte `p` **wirklich**, während `p.x = 9`
  daneben abgelehnt wurde.

- [x] **Sweep: Sema und Backend in Übereinstimmung** (2026-08-07). Drei Konstruktionen ließen den
  Compiler abstürzen statt zu diagnostizieren — `==` auf struct/class, `"a" < "b"`, `?int == ?int`.
  **`AgreementTests` fährt jetzt 448 Kombinationen** aus 16 Typen und 24 Operationen und verlangt
  für jede: Diagnose oder übersetztes Programm, nie ein Wurf.
  - Eine Matrix und keine Beispielliste, weil **alle vier Abstürze dieser Sitzung durch Zufall**
    gefunden wurden — beim Bauen von etwas anderem, das zufällig danebenlag. Vier Zufälle sind
    kein Zufall, sondern eine strukturelle Lücke.
  - Der dritte Fall war **selbstgemacht**: `CheckEquatable` packte Optionals aus, bevor es prüfte.
    Der Test fand ihn eine halbe Stunde nach seiner Entstehung — ohne ihn wäre er mitcommittet
    worden.

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

**M8b läuft — `Set<T>` ist der nächste Slice**: die Map ohne Werte-Spalte, plus `union`,
`intersect` und `difference`. Danach der Rest der Stdlib-Liste (`std.string`, `std.math` samt
`Random`, `std.io.file`, `std.os`, `std.fmt`, und die drei bis heute inhaltsleeren Module
`std.option`/`std.error`/`std.coroutine`).

**Die Erreichbarkeitsanalyse ist da** — vorgezogen, weil `std.string` den Effekt zum ersten Mal
schmerzhaft sichtbar gemacht hat: zwei Tests, die „ein Hello-World trägt keine String-Maschinerie"
festhielten, wurden falsch. Sie grün zu schreiben hätte geheißen, eine Zusage aufzugeben statt sie
einzulösen.

**Danach M10**, die Embedding-API: `Lyric.Embedding.LangVm` mit
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
- **`Opt<int>.Some(5)` ist nicht ausdrückbar.** Scheitert im **Parser** (`LYR-PAR0002: expected
  an expression, got Dot`) — `Opt<int>` wird in Wert-Position nicht als Typpfad gelesen. *Stand
  bis 2026-08-07 zusammen mit dem M4-Constraint-Rest in einem Punkt; sie hängen nicht zusammen,
  der Constraint-Teil ist erledigt und dieser nicht.* Auch eine *statische Methode* auf einer
  generischen Instanz bleibt `LYR-SEM0052`; explizite Typargumente gibt es nur an
  Funktions-Aufrufen.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.
- **`do { return … } while (…)` laesst den Compiler abstuerzen.** Rumpf, Bedingung und Ausgang
  werden alle drei vorab angelegt; terminiert der Rumpf, sind Bedingung und Ausgang unerreichbar,
  und der Verifier lehnt ab. Der Fix braucht einen Umbau, weil `_loops.Push` die Sprungziele
  vorab braucht — ein bedarfsgesteuerter Ausgang muesste wissen, ob ein `break` ihn benutzt.
  *Bewusst offen gelassen*: eine Schleife, deren Rumpf immer terminiert, schleift nie; die Form
  ist toter Code. Gefunden im Merge-Block-Sweep 2026-08-07.
- **`DeclaredTypes.Lower` wirft ungefangen** aus `ModuleLowerer.Lower` heraus: eine native
  Signatur mit einem unbekannten Typ gibt einen Compiler-*Absturz* statt einer Diagnose. Gefunden
  beim Bau von S2, als `split` noch nicht lowerbar war.

- **Ein Tupel als Typargument eines generischen Interfaces ist nicht lowerbar** —
  `Iterator<(int, T)>` gibt `ir: this type argument is not supported by this compiler version
  yet`. Die Sema akzeptiert es. **Blockiert `enumerate` und `zip`** in `std.iter`, ausgerechnet
  die beiden Funktionen, für die Tupel (T1–T3) eingeführt wurden.
- **Ein Block-Lambda liefert seinen Rückgabetyp nicht an die Inferenz**: `(n: int) => n` bindet
  `U`, `(n: int) => { return n; }` nicht. *Keine Lücke, sondern eine dokumentierte Grenze* —
  `LYR-SEM0046` sagt es und schlägt die Annotation vor, und die funktioniert. Steht hier, weil ich
  sie am 2026-08-08 fälschlich als Bug gemeldet habe.
- **Modul-Bindungen sind immer unveränderlich** (`var` auf Modulebene ist `LYR-PAR0027`).
  Vermutlich Absicht, steht aber in **keinem ADR** — beim Bau eines Testzählers aufgefallen. Wenn
  es Absicht ist, gehört es in `Sprache.md`; wenn nicht, ist es eine Lücke.

- **Interface-Vererbung gibt es nicht** (`interface A :: [B]` ist ein Parser-Fehler; die
  Grammatik sieht für `InterfaceDecl` keine Konformanzliste vor). Aufgefallen beim Bau von
  ADR-024, das sie voraussetzte. Ob v1 sie braucht, ist offen — `Hashable` bräuchte sie nur, um
  `Equatable` zu implizieren.
- **`f"{u}"` mit `u: uint` jenseits `int64.MaxValue`** druckt eine negative Zahl. Keine Absturz,
  eine falsche Ausgabe; der Fix wäre ein eigener nativer `fromUint`.
- **`string < string` und `==` auf Nutzertypen sind abgelehnt** (`LYR-SEM0003` / `LYR-SEM0055`).
  Bewusst und vorübergehend: Operator-Overloading ist das erste Thema nach v1.0 (v1.4), und die
  Diagnose zeigt darauf. Bis dahin eine gewöhnliche Methode.

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

`stdlib: Merge Sort in std.collections (M8b/S4, Teil 2)`

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
