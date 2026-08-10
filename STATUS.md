# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M9 ist abgeschlossen und getaggt** (`m9-complete`, `v0.9.0`). **M8b — Stdlib-Erweiterung — läuft.**
S1 bis S8 plus die Erreichbarkeitsanalyse.

2468 Tests grün **in Debug und Release**, Bytecode-Format **2.5**, **vier** Binaries, Version **0.9.0**.

**Die Vorgabe für M8b**: *so viel wie möglich in Lyric selbst.* Nativ bleibt nur, was eine echte
Host-Grenze ist — stdin, Datei-I/O, Zeit, `sqrt`/`sin`/`cos`. Alles andere ist Lyric-Code:
`Map`, Merge Sort, der FNV-Hash für Strings, sämtliche Iterator-Adapter. Dass eine Stdlib sich
selbst tragen kann, ist die eigentliche Aussage dieses Meilensteins — und der schärfste Test der
Sprache, den es bisher gab: **zehn Compiler-Lücken** sind dabei aufgefallen, die kein
Meilenstein davor berührt hat.

**Offen für v1.0**: der Rest von M8b — `std.option`, `std.error`, `std.coroutine` —, dann M10, die
Embedding-API (`LangVm`, Marshalling, Hot-Reload). `Set<T>` und die Erreichbarkeitsanalyse stehen.

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **M9/S6 — der Abschluss-Slice. M9 ist getaggt** (2026-08-10). Der Meilenstein galt seit dem
  2026-08-07 als fertig, weil sein Gate lief. Vier seiner Lieferposten liefen nicht.
  - **CI war rot — seit 60 Pushes**, also seit *vor* M9/S1. Genau ein Test:
    `Verbose_lists_every_phase_in_pipeline_order_with_a_total` trug die Phasenliste als Literal
    inklusive `verify`, und `verify` läuft nur in Debug-Builds. Lokal grün, in CI und in allem, was
    ausgeliefert wird, rot. **CONTRIBUTING sagt „`dotnet test` must pass" — das galt für die
    falsche Konfiguration.** Die Bedingung liegt jetzt einmal in `Lyric.Core.Pipeline`, wo Frontend
    und Werkzeug-Tests sie gemeinsam sehen; dasselbe Muster wie `Lyric.Core.Unicode`.
  - **`build/publish.proj` lag in keinem Clone.** `.gitignore` hatte `build/` — gemeint waren
    Ausgaben, getroffen wurde die Auslieferungs-Definition. Der CI-Job „Publish toolchain" rief sie
    auf und ist **nie gelaufen**: `needs:` übersprang ihn, solange die Tests rot waren. Zwei Fehler,
    die sich gegenseitig verdeckt haben. Der Publish ist jetzt einmal wirklich gefahren — 16
    Einträge, und die ausgelieferte Toolchain läuft.
  - **Die README behauptete, M9 sei nicht gebaut**: *„What is missing … is the REPL, editor
    integration"*, dazu `tooling/ … not built yet` und ein Projektbaum mit drei Binaries über einem
    Abschnitt namens „The four binaries". `Doku.md` §23.7 lieferte drei Programme aus.
  - **Drei neue Tests**, jeder gegen genau den Riss, durch den es gefallen ist: die Phasenliste
    kommt aus derselben Quelle wie die Ausgabe; was CI aufruft, muss git kennen; der Projektbaum
    nennt jedes Projekt aus `src/`.
  - Die `--verbose`-Tabelle in `Doku.md` ist **erzeugt statt abgeschrieben** — sie war doppelt
    veraltet (drei Module statt sieben). Und `lyric --version` rechnet seine Spaltenbreite jetzt
    aus `Tool.All`: sie stand als `-6` da und passte, bis `lyrrepl` kam.
  - **Die Lehre ist die alte, zum sechsten Mal**: ein Meilenstein wurde an seinem Gate gemessen
    statt an seinen Lieferposten. Die Regel dagegen steht seit M7 in dieser Datei.

- [x] **ADR-025 — Modul-Bindungen sind unveränderlich** (2026-08-09). Die Regel galt seit P5b und
  stand nur als Klammerkommentar in der Grammatik plus Parser-Meldung — ohne Begründung dort, wo
  jemand sie findet.
  - **Gemessen, was sie verhindert**: nur die Neubindung des *Namens*. `let xs = [1, 2]; xs[0] = 9;`
    und `z.stand = 42` auf einem Modul-`let` sind gültig — `let` bindet den Namen, nicht den
    Inhalt (ADR-020). Veränderlichen globalen Zustand gibt es also längst.
  - **Warum sie trotzdem bleibt**, anders als bei ADR-020/023: sie gilt ausnahmslos, und der
    Ausweg (ein Wrapper-Objekt) ist ein anderer Mechanismus, kein Schlupfloch. Dazu Sichtbarkeit
    am Verwendungsort und die Hot-Reload-Frage, die M10 sonst zusätzlich beantworten müsste.
  - **`StoreGlobal` existiert in IR und VM**; das Verbot sitzt in einer einzigen Parser-Zeile. Es
    später aufzuheben bricht keinen Code, die Gegenrichtung gilt nicht — deshalb ist „vorerst
    verbieten" die einzige revidierbare Entscheidung.
  - Die Tests halten **beide** Seiten fest. Ein Test nur für das Verbot ließe offen, wie weit es
    reicht, und genau diese Unklarheit hat zwei Regeln überleben lassen, die niemand mehr
    begründen konnte.

- [x] **Flow-Narrowing im `if`-Ausdruck** (2026-08-09). `if (a == null) 0 else a` war ein
  Typfehler, während `if (a == null) { return 0; } return a;` daneben funktionierte — derselbe
  Beweis über denselben Wert, zwei Antworten.
  - Die Maschinerie war **vollständig da** (`NarrowingFacts`, `Apply`); sie war an dieser einen
    Stelle nicht angeschlossen. `CheckIfExpr` prüfte beide Zweige ohne Fakten.
  - Der Snapshot muss zwischen den Zweigen **zurückgesetzt** werden: was im then-Zweig gilt, gilt
    im else-Zweig gerade nicht.
  - Ein Test hält fest, dass das Narrowing den Ausdruck **nicht verlässt** — sonst wäre es keine
    Aussage über einen Zweig, sondern eine stillschweigende Umdeklaration.
  - Die Umgehung in `std.fmt.ziffernZeichen` ist zurückgebaut.

- [x] **`enumerate` und `zip` in `std.iter`** (2026-08-09). `TypeTable.Resolve` löste Arrays und
  Optionals als Typargument auf, **Tupel aber nicht** — `Iterator<(int, T)>` lief in „this type
  argument is not supported by this compiler version yet". Die Sema akzeptierte es, das Lowering
  nicht; derselbe Riss wie schon zehnmal.
  - Der Fix ist **eine Zeile**, und er hat ausgerechnet die zwei Funktionen freigeschaltet, für
    die Tupel (T1–T3) überhaupt eingeführt wurden.
  - `FunctionType` ist gleich mit aufgenommen: kein heutiger Fall braucht ihn, aber die Liste wäre
    sonst wieder eine Teilkopie — dreimal hat genau das in diesem Projekt Zeit gekostet
    (`LowerWithOwner`, `LowerSubstituted`, `SubstituteType`).
  - `zip` hat **zwei** Abbruchstellen (kurz links, kurz rechts) und deshalb zwei Tests: ein Test
    deckt nur eine davon ab.

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

**M8b läuft.** `Set<T>`, `std.string`, `std.math`, `std.fmt`, `std.io.file` und `std.os` stehen
(S1–S8). Offen ist der Rest der Stdlib-Liste — die drei bis heute inhaltsleeren Module
`std.option`/`std.error`/`std.coroutine`.

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

**Der `v0.9.0`-Tag ist gesetzt** (annotiert, die Message ist die Release-Notiz — CONTRIBUTING
§Releases, kein `CHANGELOG.md` vor v1.0), dazu `m9-complete`. Beide zeigen auf den S6-Commit und
**nicht** auf „M9: Politur" vom 2026-08-07: dort war `dotnet test` in Release rot und die
Auslieferung nicht ausführbar. Einen Tag auf diesen Stand zu setzen wäre das „done by intent
alone", das Rule 3 verbietet.

**Weiterhin ungetaggt: `m5-complete`, `m7-complete`, `m8-complete` und `v0.5.0`.** Rule 3 verlangt
sie, die Meilensteine sind fertig, die Tags fehlen — bewusst offen gelassen, weil sie auf die
damaligen Commits gehören und das eine eigene Entscheidung ist.

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

- **Ein Block-Lambda liefert seinen Rückgabetyp nicht an die Inferenz**: `(n: int) => n` bindet
  `U`, `(n: int) => { return n; }` nicht. *Keine Lücke, sondern eine dokumentierte Grenze* —
  `LYR-SEM0046` sagt es und schlägt die Annotation vor, und die funktioniert. Steht hier, weil ich
  sie am 2026-08-08 fälschlich als Bug gemeldet habe.

- **`?T[] ?? []`** und **`size`** sind erledigt (M8b/S8).

- **Interface-Vererbung gibt es nicht** (`interface A :: [B]` ist ein Parser-Fehler; die
  Grammatik sieht für `InterfaceDecl` keine Konformanzliste vor). Aufgefallen beim Bau von
  ADR-024, das sie voraussetzte. Ob v1 sie braucht, ist offen — `Hashable` bräuchte sie nur, um
  `Equatable` zu implizieren.
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

`M9: der Abschluss-Slice — CI wieder grün, Auslieferung im Repo (S6)`

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
