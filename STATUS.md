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

2640 Tests grün **in Debug und Release**, Bytecode-Format **3.0**, **vier** Binaries, Version **0.9.0**.

**Die Vorgabe für M8b**: *so viel wie möglich in Lyric selbst.* Nativ bleibt nur, was eine echte
Host-Grenze ist — stdin, Datei-I/O, Zeit, `sqrt`/`sin`/`cos`. Alles andere ist Lyric-Code:
`Map`, Merge Sort, der FNV-Hash für Strings, sämtliche Iterator-Adapter. Dass eine Stdlib sich
selbst tragen kann, ist die eigentliche Aussage dieses Meilensteins — und der schärfste Test der
Sprache, den es bisher gab: **zehn Compiler-Lücken** sind dabei aufgefallen, die kein
Meilenstein davor berührt hat.

**Offen für v1.0**: **der v1.0-Rest** — `RegisterType<T>`,
siehe `## Was v1.0 noch fehlt`. M0–M10 stehen. `std.dotnet` ist gestrichen
(2026-08-11): eine Reflection-Brücke lässt das *Skript* entscheiden, was M10 dem *Host* gibt.

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **M10/E6 — Doku, Inventur, Auslieferung. M10 ist abgeschlossen** (2026-08-11). 2640 Tests grün.
  - **`Doku.md` §21 ist neu geschrieben**, gegen den Beispiel-Host, den die Testsuite ausführt.
    Vier Zusagen sind zurückgezogen: `Compile` ohne Modulnamen, `playSound("hit")` ohne Import,
    `builder.Field`, `vm.Call`. Ein Test hält sie fern **und** prüft, dass §21 die API nennt, die
    es gibt — ohne die Gegenprobe bliebe er grün, wenn der Abschnitt gelöscht würde.
  - **Jeder Lyric-Schnipsel in §21 übersetzt**, gegen einen Host mit genau den Registrierungen,
    die daneben stehen. Dieselbe Regel wie beim README-Beispiel seit M9: ein Beispiel, das nicht
    übersetzt, ist eine Lüge in der Doku.
  - **`lyrembed.dll` lag in keiner Auslieferung.** Sie stand in README und §21 als das, was ein
    Host referenziert, und kein Binary referenziert sie — also landete sie nie im Artefakt.
    **Es gab keinen Test über die Auslieferung**, nur über die `bin`-Verzeichnisse. Jetzt bindet
    einer die README-Liste an `publish.proj`, geprüft in der Richtung, die den Fehler fängt.
  - Die Lieferposten-Inventur steht in `ROADMAP.md` bei M10 — Punkt für Punkt, wie es die Regel
    seit M7 verlangt und wie es bei M9 unterblieben ist.

- [x] **M10/E5 — `Reload`** (2026-08-11). 2617 Tests grün. **Nur noch E6 bis v1.0.**
  - `instance = instance.Reload()` liest die Quelldatei erneut. **Die tragende Zusage ist nicht
    „es lädt neu", sondern dass die alte Fassung einen Fehlschlag überlebt** — sonst wäre `Reload`
    ein Alias für `Instantiate(CompileFile(…))`, das ein Host selbst schreiben könnte. Ein Mod mit
    einem Tippfehler hält das Spiel nicht an; dieselbe Eigenschaft, die die REPL seit ADR-021 hat.
  - **Was neu läuft und was bleibt, musste E5 nicht entscheiden**: Modul-Konstanten werden neu
    berechnet, weil eine neue Instanz ein neuer Zustand ist (ADR-025); Host-Objekte überleben,
    weil sie dem GC gehören und nicht der Instanz (ADR-026). Die Welt bleibt stehen, nur das
    Skript wird getauscht. Zwei ADRs, die vorher getroffen wurden, haben den Slice fast leer
    gemacht — das ist der Ertrag davon, sie vorher zu treffen.
  - **`CompileFile` meldete einen Modulnamen, den das Modul nicht trug.** Der Resolver nannte es
    `main`, `ScriptModule.Name` den Dateinamen — ein `Call` darauf fand nichts. Der Riss stammt
    aus E1 und war bis hierher unsichtbar: **kein Test hatte eine Datei übersetzt *und* daraus
    gerufen.** `RunScript` lief über `main`, `Call` über Quelltext aus dem Speicher.
  - Der explizite Tausch (`instance = instance.Reload()`) statt eines stillen hinter derselben
    Referenz: hier wird ein Zustand weggeworfen, und das soll man am Aufruf sehen.

- [x] **M10/E4b — Methoden auf Host-Typen. E4 ist damit fertig** (2026-08-11). 2609 Tests grün.
  - `RegisterType<T>("Entity", t => t.Getter("leben", …).Method("schaden", …, mutates: true))`
    erzeugt eine Klassendeklaration, deren bodylose Methoden Natives mit dem **Empfänger als
    Parameter 0** sind (ADR-014). Im Skript: `e.schaden(30)`.
  - **Es gibt kein `Field`.** `Doku.md` §21 versprach `builder.Field("x", v => v.X)` — das braucht
    ein `ldfld`, und ein Host-Typ hat keinen Typtabellen-Eintrag. `Getter` ist die ehrliche Form:
    in Lyric `e.name()`, nicht `e.name`.
  - **`LowerImportCall` verglich Argumentzahl gegen Parameterzahl** und kannte den Empfänger
    nicht — die Meldung log dabei („with default or variadic arguments"). Er wird jetzt
    durchgereicht.
  - Die Regel „was ist ein Host-Typ" heißt seit E4b **kein Feld und kein Methodenrumpf**, vorher
    „leerer Rumpf". *Kein Feld* war immer die Aussage; es gab nur noch keine Methoden.
  - **Ein Test, den C# selbst überflüssig macht**: `Getter<TValue>(Func<T, TValue>)` erzwingt den
    Empfänger schon beim Übersetzen. Die Laufzeitprüfung deckt nur `Method(string, Delegate)` ab,
    das untypisiert sein muss, weil eine Methode beliebig viele Parameter hat.

- [x] **M10/E4a — Host-Objekte, Format 3.0** (2026-08-11). 2602 Tests grün.
  - **`TypeTag.Host = 0x47`**, Name inline. **Eigenes Tag neben `Ref`, und das ist der Kern**:
    beide sind Referenzen, aber bei `Ref` kennt das *Modul* das Layout und der Host hält sich
    heraus — bei `Host` ist es umgekehrt. Ein Host-Typ hat deshalb **keinen
    Typtabellen-Eintrag**, womit ADR-026s Zusage „gegen einen Host-Typ wird nie ein `ldfld`
    emittiert" **strukturell** wird statt geprüft: ohne Feldliste ist ein Feldzugriff nicht
    kodierbar.
  - **Format 3.0, nicht 2.6.** §2 erlaubt einer neuen Minor nur überspringbare Sektionen, und ein
    Typ-Tag ist keine. Pre-1.0 ohne Migrationspfad erlaubt (ADR-013).
  - **`RegisterType<T>`** macht einen .NET-Typ als opaken Lyric-Typ sichtbar. Ein Skript reicht
    ihn weiter — Feldzugriff und Konstruktion sind Diagnosen. **Letztere ersetzt einen
    Compiler-Absturz**: `Entity { }` war `cannot compare IrHostType with IrRefType`, jetzt
    `LYR-SEM0061`.
  - **Die Zusage ist Identität, nicht Gleichheit**: was der Host zurückbekommt, ist
    `ReferenceEquals` dasselbe Objekt. Ein Test prüft genau das — mit Wert-Gleichheit wäre ein
    Kopieren nicht zu bemerken.
  - **Die Regel „ist das ein Host-Typ" stand erst an einer Stelle und musste an zwei.** Beim
    Lowern der nativen Signatur wurde `Entity` ein Host-Typ, an der Aufrufstelle eine gewöhnliche
    Referenz; der Verifier meldete es binnen einer Minute. Sie liegt jetzt in `HostTypes.NameOf`.
  - **Der Versionstest trug `2` und `5` als Literale** und ist an `Format` gebunden — die vierte
    Stelle dieser Art in dieser Sitzung.

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

**M8b ist inhaltlich durch.** `Set<T>`, `std.string`, `std.math`, `std.fmt`, `std.io.file`,
`std.os` und zuletzt `std.option` stehen (S1–S9); `std.error` und `std.coroutine` sind mit
Begründung gestrichen statt geliefert.

**M10 läuft.** E1–E3 stehen: `LangVm`, Capabilities, `Call<T>`, Marshalling, `RegisterFunction`.
Ein Host kann heute ein Skript laden, sandboxen, Funktionen daraus rufen und eigene hineinreichen.

**M10 ist abgeschlossen**, Inventur inklusive. **v1.0 ist es nicht** — was fehlt, steht unten
unter `## Was v1.0 noch fehlt`: vier Meilenstein-Tags, ein `CHANGELOG.md`, plattformspezifische
Binaries, eine Doku-Site, und die Entscheidung, welche der offenen Sprachlücken v1 blockieren.

**Die offene Frage, die vor E4 zu beantworten ist**: Lebenszeit und Identität eines Host-Objekts
über die Grenze — hält der Host es am Leben oder die VM? Das ist die einzige Stelle in M10, an der
ich noch keine Antwort habe, und sie gehört gestellt, bevor E4 anfängt.

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
§Releases, kein `CHANGELOG.md` vor v1.0), dazu `m9-complete`. Beide zeigen auf den **ersten Stand,
auf dem alle drei CI-Jobs grün sind** — nicht auf „M9: Politur" vom 2026-08-07, wo `dotnet test`
in Release rot war und die Auslieferung nicht baute. Einen Tag dorthin zu setzen wäre das „done by
intent alone", das Rule 3 verbietet.

Sie standen kurzzeitig einen Commit früher und sind verschoben worden: dort waren die Tests grün,
der Publish-Job aber rot. **Ein Tag ist das Einzige, was sich nicht stillschweigend nachbessern
lässt** — deshalb war das Verschieben richtig und wäre es eine Woche später nicht mehr gewesen.

**Weiterhin ungetaggt: `m5-complete`, `m7-complete`, `m8-complete` und `v0.5.0`.** Rule 3 verlangt
sie, die Meilensteine sind fertig, die Tags fehlen — bewusst offen gelassen, weil sie auf die
damaligen Commits gehören und das eine eigene Entscheidung ist.

## Was v1.0 noch fehlt

**M0–M10 sind inhaltlich fertig.** Das Release ist es nicht, und die Liste ist kurz genug, um sie
Punkt für Punkt abzuarbeiten — nach der Regel, an der M9 gescheitert ist.

**Prozess (CONTRIBUTING):**

- **Vier Meilenstein-Tags fehlen**: `m5-complete`, `m7-complete`, `m8-complete`, `m10-complete`.
  Rule 3 verlangt sie; die Meilensteine sind fertig, die Tags fehlen. Dazu `v0.5.0` (laut
  §Releases nach M7).
- **`CHANGELOG.md`** — §Releases: *„From `v1.0.0` on: tag, GitHub release page, and a
  `CHANGELOG.md` entry."* Vor v1.0 gab es bewusst keinen; ab v1.0 gibt es ihn.
- **GitHub-Release-Seite** zum `v1.0.0`-Tag.

**Artefakt (ROADMAP §v1.0):**

- **Binaries für Windows/Linux/macOS** via `dotnet publish -r …`. `publish.proj` liefert heute
  **framework-abhängig** und ohne RID-Matrix — es braucht eine .NET-10-Laufzeit auf der Zielmaschine.
- **Doku-Site** (statisches HTML aus den Docs). Es gibt keine.

**Sprachlücken, die als „vor v1 zu schließen" notiert sind** — sie stehen einzeln unter
`## Noch offen`. Die zwei, die ich für blockierend halte, weil sie **abstürzen** statt zu
diagnostizieren:

- `do { return … } while (…)` lässt den Compiler abstürzen (bewusst offen gelassen, aber ein
  Absturz bleibt ein Absturz).
- `DeclaredTypes.Lower` wirft ungefangen aus `ModuleLowerer.Lower` heraus — **teilweise behoben**:
  der Import-Pfad in Pass 1 fängt seit M7, der Host-Methoden-Pfad seit E4b. Ob eine Lücke bleibt,
  ist nachzumessen.

Die übrigen sind Grenzen mit Diagnose (`b?.get()`, `Opt<int>.Some(5)`, `s = Small { n = 5 }`,
`@noCapture`, Interface-Vererbung) — sie kosten Ausdrucksstärke, keinen Absturz. **Ob sie v1
blockieren, ist eine Entscheidung und keine Messung.**

## Noch offen

**Aus dem M10-Plan, beim Messen gefunden:**

- **Der Member-Trenner aus §3.2 ist für Block-Rümpfe geschrieben.** Eine bodylose Methode in einer
  Klasse braucht `int;,` — Semikolon *und* Komma hintereinander. Es geht, aber es ist eine
  Schreibweise, die niemand errät.

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

`M10/E6: Doku §21 neu, Inventur, lyrembed ausgeliefert — M10 fertig`

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
