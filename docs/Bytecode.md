# Lyric — `.lyrbc` Bytecode-Format v2.2

> Dieses Dokument ist **normativ** (ADR-013). Der C#-Serializer in `src/Lyric.Bytecode/` ist eine
> Implementierung dieser Spec, nicht ihre Definition. Ziel-Test: jemand kann allein aus diesem
> Dokument einen Disassembler oder eine zweite Runtime schreiben, ohne den C#-Code zu lesen.
>
> **Stabilität**: Bis Lyric v1.0 darf sich das Format inkompatibel ändern — Major-Version-Bump ohne
> Migrationspfad. Ein Stabilitätsversprechen gibt es erst ab v1.0.
>
> **Stand**: Format-Version **2.2**. Deckt den Sprachumfang ab, den das IR-Lowering heute erzeugt:
> Skalare, Locals, modulinterne und native Calls, strukturierter Kontrollfluss, **Klassen**
> (Referenz-Typen mit Feldern und Methoden, Empfaenger als Parameter 0), **Arrays**, **Optionals**,
> **Enums**, **Interfaces mit vtable-Dispatch** und **Structs mit Wert-Semantik**.

---

## 1. Grundregeln der Kodierung

| Sache | Regel |
|---|---|
| Fixbreiten-Ganzzahlen | **Little-Endian**, explizit — nicht die Byte-Reihenfolge der Host-Maschine |
| Variable Ganzzahlen | **LEB128**, unsigned, höchstens 10 Gruppen (64 Bit) |
| Strings | Länge in **Bytes** als LEB128, dann UTF-8 ohne BOM und ohne Nullterminator |
| Floats | **IEEE-754-Bitmuster**, little-endian (4 bzw. 8 Byte) — nicht der Dezimalwert |
| Bool | ein Byte, `0x00` = false, alles andere = true |

**LEB128 (unsigned)**: sieben Nutzbits je Byte, beginnend beim niedrigstwertigen. Bit 7 (`0x80`)
gesetzt heißt „ein weiteres Byte folgt". Ein Leser, der mehr als zehn Bytes liest, muss die Datei
ablehnen.

**Determinismus**: Derselbe Compiler-Input erzeugt **byte-identischen** Output. Dafür gilt:

- Der String-Pool steht in **Erst-Verwendungs-Reihenfolge** (nicht Hash-Reihenfolge).
- Sektionen erscheinen **höchstens einmal** und in **aufsteigender Id-Reihenfolge**.
- Keine Zeitstempel, keine absoluten Pfade.

---

## 2. Datei-Aufbau

```
magic            4 Byte    'L' 'Y' 'R' 'B'  (0x4C 0x59 0x52 0x42)
version.major    u16       little-endian
version.minor    u16       little-endian
sections         *         beliebig viele, siehe unten
```

Eine Sektion:

```
id               u8
byteLength       uleb128   Länge des Inhalts, ohne id und ohne dieses Feld
payload          byteLength Bytes
```

Ein Leser **muss** eine Sektion mit unbekannter Id überspringen (die Länge macht das möglich) und
**muss** eine Datei ablehnen, deren Sektions-Ids nicht streng aufsteigen.

**Versionierung**: Eine unbekannte **Major**-Version wird abgelehnt. Eine unbekannte **Minor** ist
zu tolerieren — neue Minor-Versionen dürfen nur überspringbare Sektionen hinzufügen.

### Sektions-Ids

| Id | Name | Pflicht | Inhalt |
|---|---|---|---|
| 1 | Capabilities | nein | `uleb128` Bitset |
| 2 | Strings | nein | Konstantenpool, **nur Strings** |
| 3 | Types | nein | Layouts zusammengesetzter Typen |
| 4 | Imports | nein | Host-/Native-Funktionen |
| 5 | Functions | nein | definierte Funktionen samt Code |
| 6 | SourceMap | nein | optional und **strippbar**: PC → Datei/Zeile |
| 7 | Start | nein | Einstiegspunkt: `uleb128`-Index der aufzurufenden Funktion |
| 8 | Impls | nein | Interface-Implementierungen (vtables) |

Fehlt eine Sektion, gilt sie als leer.

**Warum nur Strings im Konstantenpool**: Zahlen sind als LEB128-Immediate nicht größer als ein
Pool-Index und sparen die Indirektion. Der Pool hat damit genau eine Aufgabe.

**Warum es die Types-Sektion gibt**: Skalare Typen sind ein Byte (§3) und brauchen keine Tabelle.
Zusammengesetzte brauchen eine, aus zwei Gründen. Erstens **rekursive Typen**: `class Node { next:
Node }` ist strukturell nicht endlich kodierbar, über einen Index schon. Zweitens **Größe**: das
Layout eines Typs steht einmal da statt an jeder Instruktion, die ihn erwähnt. JVM (Constant Pool)
und CIL (TypeDef-Tabelle) lösen es genauso.

### Types (Id 3)

`uleb128` Anzahl, dann je Typ:

```
nameIndex        uleb128   Index in den String-Pool
kind             u8        0 = Layout, 1 = Enum, 2 = Interface, 3 = Struct
```

**kind 0 — Layout** (class, struct, und jede einzelne Enum-Variante):

```
fieldCount       uleb128
fieldTypes       fieldCount × Typ (§3), in Deklarationsreihenfolge
```

**kind 1 — Enum**: nennt seine Varianten, jede davon ein eigener Layout-Eintrag.

```
variantCount     uleb128
variantTypes     variantCount × uleb128   Index in diese Tabelle
```

Der **Feldindex ist die Position in der Feldliste** — Feldnamen stehen nicht im Bytecode. Sie sind
Metadaten; der Zugriff ist ein Offset. Dieselbe Entscheidung wie „Sprungziele sind Block-Indizes".
Der Name des Typs steht nur für Diagnose und Disassembler darin.

Ein Leser **muss** ablehnen: einen Feldtyp `void` (§3), einen Typ-Index außerhalb der Tabelle, und
einen Enum, dessen Variante kein Layout ist. Rekursion über Referenzen ist dagegen ausdrücklich
erlaubt — ein Typ darf sich selbst als Feldtyp nennen, auch vorwärts.

**Warum jede Variante ein eigener Typ ist**: Varianten haben verschiedene Felder — `Circle(float)`
trägt eine Zahl, `Triangle { a, b, c }` drei. Ein gemeinsames Layout müsste entweder den Payload
boxen (eine Allokation mehr und ein Slot ohne festen Typ) oder auf Maximalgröße auslegen (Slot 1
wäre je nach Variante ein anderer Typ). Beides bricht die Regel, dass jedes Feld genau einen Typ
hat. Mit einem Layout pro Variante bleibt sie intakt — und `match` ist ohnehin der Ort, an dem die
Variante bekannt wird, also steht der richtige Typ genau dort zur Verfügung. Rust schichtet aus
demselben Grund so.

**Slot 0 jeder Variante ist ihr Tag**: der `i64`-Index der Variante in der `variantTypes`-Liste
ihres Enums, 0-basiert in Deklarationsreihenfolge. Die Nutzfelder beginnen bei Slot 1.

**kind 3 — Struct**: dasselbe Feld-Layout wie kind 0, aber **Wert-Semantik**.

```
fieldCount       uleb128
fieldTypes       fieldCount × Typ (§3), in Deklarationsreihenfolge
```

Ein Struct-Wert ist zur Laufzeit **dieselbe Slot-Folge** wie ein Klassenobjekt; `ldfld` und
`stfld` arbeiten unverändert darauf. Der Unterschied steckt allein in den **Bindepunkten**: wo ein
Struct-Wert eine neue Heimat bekommt, steht ein `structcopy` (§5).

Ein eigener Kind-Wert statt nur eines anderen Typ-Tags an der Verwendungsstelle: „ist dieser Typ
ein Wert-Typ" ist eine Eigenschaft der **Deklaration**, und der Loader muss `structcopy` dagegen
prüfen können.

Ein Struct darf sich **nicht** — auch nicht über Umwege — selbst als Feld enthalten: es wäre
unendlich groß. Ein Leser darf das ablehnen; der Lyric-Compiler tut es schon in der Sema
(`LYR-SEM0056`). Über eine `class`, ein `T[]` oder ein Interface ist Rekursion dagegen erlaubt —
das sind Referenzen.

**kind 2 — Interface**: nennt statt Feldern die Namen seiner Methoden-Slots.

```
slotCount        uleb128   mindestens 1
slotNames        slotCount × String
```

Der **Index** in dieser Liste ist der Slot, auf den `callvirt` zeigt. Die Namen stehen — anders als
Feldnamen — tatsächlich im Bytecode, weil ein Disassembler sonst nur `ty3#1` zeigen könnte und eine
Runtime beim Binden von Host-Implementierungen keinen Anhaltspunkt hätte.

`slotCount` muss mindestens 1 sein: auf einem Interface ohne Methoden gäbe es nichts zu
dispatchen. Ein Interface darf links in keiner Impls-Zeile stehen — Interfaces implementieren
keine Interfaces.

### Capabilities (Id 1)

Ein `uleb128`-Bitset. In Format 1.1 immer `0` = „verlangt nichts". Die Zuordnung einzelner Bits zu
den Capability-Stufen aus ADR-007 (`fileAccess`, `networkAccess`, `osAccess`, `hostAccess`) entsteht
mit der Stdlib; bis dahin darf ein Leser ein Bitset ungleich 0 ablehnen.

### Strings (Id 2)

```
count            uleb128
values           count × String
```

### Imports (Id 4)

```
count            uleb128
entries          count × {
                   name         String
                   paramCount   uleb128
                   paramTypes   paramCount × TypeTag
                   returnType   TypeTag
                 }
```

Host-Funktionen werden **symbolisch** referenziert (Name + Signatur), nicht über Adressen; die
Bindung an konkrete Implementierungen macht der Host beim Laden. In Format 1.1 ist die Tabelle
immer leer — das Lowering kennt noch keine externen Calls.

### Functions (Id 5)

```
count            uleb128
entries          count × {
                   nameIndex    uleb128   Index in den String-Pool
                   paramCount   uleb128
                   returnType   TypeTag
                   slotCount    uleb128
                   slotTypes    slotCount × TypeTag
                   maxStack     uleb128
                   blockCount   uleb128
                   blockOffsets blockCount × uleb128   Byte-Offset in 'code'
                   codeLength   uleb128
                   code         codeLength Bytes
                 }
```

- Die **ersten `paramCount` Slots sind die Parameter**, in Deklarations-Reihenfolge. Es gilt
  `paramCount ≤ slotCount`.
- `maxStack` ist die maximale Tiefe des Operanden-Stacks in dieser Funktion. Eine Runtime darf
  ihren Frame danach dimensionieren und zur Laufzeit auf Überlauf-Prüfungen verzichten.
- `blockOffsets[i]` ist der Byte-Offset von Block `i` in `code`. Jeder Offset **muss** auf einer
  Instruktionsgrenze liegen.
- **Block 0 ist der Einstiegsblock** der Funktion. Die Ausführung beginnt bei `blockOffsets[0]`.

### Start (Id 7)

```
functionIndex    uleb128   in den gemeinsamen Indexraum: erst Imports, dann Funktionen
```

Der Einstiegspunkt des Programms — in Lyric `main` (Sprache.md §11). Eine Runtime ruft ihn ohne
Argumente auf; sein Rückgabewert ist der Prozess-Exit-Code (die niederwertigsten 8 Bit).

Fehlt die Sektion, ist das Modul eine **Bibliothek** und hat keinen Einstieg. Ohne diese Sektion
müsste eine Runtime den Einstieg über eine Namenskonvention raten — dann wäre das Format nicht mehr
allein aus dieser Spec implementierbar, und genau das fordert ADR-013.

---

### Impls (Id 8)

Die **vtables**: welche Funktion erfüllt welchen Methoden-Slot welches Interfaces für welchen Typ.

```
implCount        uleb128
implCount mal:
  typeIndex      uleb128   der implementierende Typ (Klasse oder Enum)
  interfaceIndex uleb128   ein Interface-Eintrag
  methodCount    uleb128   muss gleich slotCount des Interfaces sein
  methods        methodCount uleb128, je ein Index in den gemeinsamen
                 Aufruf-Indexraum (erst Imports, dann Funktionen)
```

Eine Zeile je (Typ, Interface)-Paar; dasselbe Paar zweimal ist ein Fehler, weil der Dispatch sonst
mehrdeutig wäre. Ein Typ, der nicht Klasse oder Enum ist, darf links nicht stehen — Interfaces
implementieren keine Interfaces.

**Die Auflösungsreihenfolge steckt bereits in den Zeilen.** `Sprache.md` §3.5 gibt „eigenes Member
vor Interface-Default" vor; der Compiler löst das auf und trägt die gewonnene Funktion ein. Eine
Runtime sucht nichts — sie liest einen Index.

Alle Implementierungen desselben Slots haben dieselbe Signatur. Eine Runtime darf sich darauf
verlassen und die Argumentzahl aus *irgendeiner* Zeile des Interfaces ableiten; sie muss das sogar,
weil `callvirt` seinen Empfänger vom Stack holen muss, *bevor* die Zielfunktion feststeht.

Eigene Sektion und **kein** Feld im Klassen-Eintrag: §2 erlaubt einer neuen Minor nur überspringbare
Ergänzungen. Ein zusätzliches Feld im Layout-Eintrag wäre eine Formänderung wie bei den Enums (die
2.0 erzwang); eine neue Sektions-Id ist genau die Erweiterung, für die der Mechanismus da ist.

---

## 3. Typ-Tags

Ein Byte.

| Tag | Typ | | Tag | Typ |
|---|---|---|---|---|
| `0x01` | `i8` | | `0x08` | `u64` |
| `0x02` | `i16` | | `0x09` | `f32` |
| `0x03` | `i32` | | `0x0A` | `f64` |
| `0x04` | `i64` | | `0x0B` | `bool` |
| `0x05` | `u8` | | `0x0C` | `char` |
| `0x06` | `u16` | | `0x0D` | `string` |
| `0x07` | `u32` | | `0x0E` | `void` |

Zusammengesetzte Typen ab `0x40`:

| Tag | Bedeutung | Folgt |
|---|---|---|
| `0x40` | Referenz auf einen Typ der Types-Sektion | `uleb128` Typ-Index |
| `0x41` | Array | der Elementtyp, wieder als Typ (§3) |
| `0x42` | Optional (`?T`) | der innere Typ, wieder als Typ (§3) |
| `0x43` | Enum | `uleb128` Index eines Enum-Eintrags der Types-Sektion |
| `0x44` | Interface (`dyn`) | `uleb128` Index eines Interface-Eintrags der Types-Sektion |
| `0x45` | Struct (Wert-Semantik) | `uleb128` Index eines Struct-Eintrags der Types-Sektion |

Ein Wert mit Tag `0x44` traegt neben der Referenz seinen konkreten Typindex — siehe
§5 „Darstellung eines Interface-Wertes".

Der Elementtyp eines Arrays steht **inline**, nicht als Tabellen-Index: `int[][]` ist damit
`0x41 0x41 0x04`. Das geht, weil ein Array-Typ nicht rekursiv sein kann — ein Array enthält seinen
eigenen Elementtyp nie direkt, anders als ein `class Node { next: Node }`. Genau deshalb braucht er
keine Tabelle.

`void` ist ausschließlich als Rückgabetyp gültig, nie als Slot-, Feld- oder Wert-Typ.

Ein Wert mit Tag `0x40` ist eine **Referenz**: Zuweisung kopiert den Verweis, nicht das Objekt.
Ein Wert mit Tag `0x45` ist ein **Wert-Typ**: dieselbe Slot-Folge, aber jede Bindung kopiert. Zwei
Tags und nicht eines, damit am Bytecode ablesbar bleibt, ob eine Zuweisung kopiert.

Lyrics `int`/`uint`/`float` sind Aliasse für `i64`/`u64`/`f64` und erscheinen im Bytecode als solche.

---

## 4. Ausführungsmodell

Eine **Stack-Maschine** mit zwei getrennten Speichern je Aufruf:

- **Local-Slots**: indiziert, typisiert, beliebig les- und schreibbar.
- **Operanden-Stack**: die Instruktionen arbeiten darauf.

### Die tragende Invariante

> **Der Operanden-Stack ist an jeder Blockgrenze leer.**

Werte, die Blöcke überqueren, laufen durch Local-Slots. Daraus folgt:

- Die Stack-Tiefe an jedem Punkt ist **statisch** bestimmbar, ohne Datenflussanalyse.
- Ein Leser kann sie beim Laden prüfen und danach jede Laufzeitprüfung weglassen — das ist ADR-013s
  „Validierung beim Load statt beim Call".
- Sprünge brauchen keine Stack-Angleichung, weil an beiden Enden die Tiefe 0 ist.

### Sprungziele

Sprünge nennen **Block-Indizes**, keine Byte-Offsets. Der Funktionskopf trägt die Offset-Tabelle.
Ein Ziel prüft man mit `index < blockCount` — kein Abgleich gegen Instruktionsgrenzen nötig.

---

## 5. Instruktionen

Jede Instruktion beginnt mit einem Opcode-Byte. `T` steht für ein Typ-Tag-Byte (§3).

**Der Typ steht als Tag am Opcode, nicht im Opcode.** Bei zehn numerischen Typen ergäbe eine
Spezialisierung wie in der JVM (`iadd`/`ladd`/`fadd`/…) rund hundert Arithmetik-Opcodes; die
Tabelle bliebe nicht mehr lesbar. Der Tag steht im **Instruktionsstrom**, nicht im Laufzeitwert —
der Dispatch bleibt statisch, es gibt keinen polymorphen Opcode.

### Werte und Slots

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x01` | `const` | `T`, Immediate | → +1 | Konstante laden, siehe unten |
| `0x02` | `ldloc` | `uleb128` slot | → +1 | Slot lesen |
| `0x03` | `stloc` | `uleb128` slot | −1 | oberster Wert in den Slot |
| `0x04` | `pop` | — | −1 | obersten Wert verwerfen |

**`const`-Immediate** je nach Tag:

| Tag | Immediate |
|---|---|
| `i8`…`i64`, `u8`…`u64` | `uleb128` des **Zweierkomplement-Bitmusters**, nullerweitert auf 64 Bit |
| `f32` | 4 Byte IEEE-754, little-endian |
| `f64` | 8 Byte IEEE-754, little-endian |
| `bool` | 1 Byte |
| `char` | `uleb128` Unicode-Codepoint |
| `string` | `uleb128` Index in den String-Pool |

Der Wert **muss** in die Breite des Tags passen: `const i8` mit einem Immediate > `0xFF` ist
ungültig. `const void` ist ungültig.

### Arithmetik und Bitoperationen

Alle nehmen zwei Werte desselben Typs und legen einen Wert dieses Typs zurück (−2 +1).
Das Tag nennt den Operandentyp.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x10` | `add T` | | `0x15` | `shl T` |
| `0x11` | `sub T` | | `0x16` | `shr T` |
| `0x12` | `mul T` | | `0x17` | `and T` |
| `0x13` | `div T` | | `0x18` | `or T` |
| `0x14` | `rem T` | | `0x19` | `xor T` |

`add`…`rem` verlangen einen numerischen Typ, `shl`…`xor` einen ganzzahligen. Signed und unsigned
sind **verschiedene** Operationen — `div i64` und `div u64` sind nicht austauschbar. Es gibt keine
String-Konkatenation als Instruktion: `string + string` ist in Lyric eingebaute Semantik, lowert
aber zu einem Call.

### Vergleiche

Zwei Werte desselben Typs → ein `bool` (−2 +1). **Das Tag nennt den Operandentyp**, nicht den
Ergebnistyp.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x20` | `lt T` | | `0x23` | `ge T` |
| `0x21` | `le T` | | `0x24` | `eq T` |
| `0x22` | `gt T` | | `0x25` | `ne T` |

`lt`/`le`/`gt`/`ge` verlangen einen numerischen Typ. `eq`/`ne` sind zusätzlich auf `bool`, `char`
und `string` gültig.

### Unäre Operationen und Konvertierung

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x30` | `neg` | `T` | −1 +1 | Vorzeichen, numerisch |
| `0x31` | `not` | — | −1 +1 | logisches Nicht |
| `0x32` | `bitnot` | `T` | −1 +1 | Bitweises Nicht, ganzzahlig |
| `0x33` | `conv` | `T_from`, `T_to` | −1 +1 | numerische Konvertierung |

**`not` trägt als Einziger kein Typ-Tag** — nur `bool` ist gültig, ein Tag wäre reine Redundanz.
Das ist die einzige Ausnahme von der Tag-Regel.

`conv` ist nur zwischen numerischen Typen gültig, und `T_from ≠ T_to`: eine
Identitäts-Konvertierung erzeugt der Compiler nicht.

### Aufrufe und Kontrollfluss

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x40` | `call` | `uleb128` index | −n [+1] | Aufruf, siehe unten |
| `0x41` | `ret` | — | 0 | Rückkehr ohne Wert |
| `0x42` | `retval` | — | −1 | Rückkehr mit dem obersten Wert |
| `0x43` | `br` | `uleb128` block | 0 | unbedingter Sprung |
| `0x44` | `condbr` | `uleb128` ifTrue, `uleb128` ifFalse | −1 | verzweigt nach dem obersten `bool` |
| `0x45` | `unreachable` | — | 0 | darf nie erreicht werden |

**`call`** nimmt `paramCount` Werte vom Stack (der erste Parameter zuunterst) und legt genau dann
einen Wert zurück, wenn der Rückgabetyp nicht `void` ist.

Der Index adressiert einen **gemeinsamen Indexraum**: zuerst alle Imports, dann alle definierten
Funktionen. Bei `importCount = 0` ist der Index also die Position in der Function-Tabelle.

`ret`/`retval` müssen zum Rückgabetyp der Funktion passen. Jeder Block endet mit genau einer
Instruktion aus `ret`, `retval`, `br`, `condbr`, `unreachable`.

### Objekte

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x50` | `newobj` | `uleb128` type | +1 | legt eine Instanz an, Felder auf ihren Nullwert |
| `0x51` | `ldfld` | `uleb128` type, `uleb128` field | −1 +1 | ersetzt die Referenz durch den Feldwert |
| `0x52` | `stfld` | `uleb128` type, `uleb128` field | −2 | schreibt das Feld |

**Stack-Reihenfolge bei `stfld`**: die Referenz liegt **unter** dem Wert. Also erst die Referenz
auf den Stack legen, dann den Wert; `stfld` nimmt beide. Das ist dieselbe Reihenfolge wie in CIL
und die einzige, die ohne Vertauschen auskommt, wenn man Ziel-Ausdruck vor Wert-Ausdruck auswertet
— was Sprache.md §6.4 für Zuweisungen verlangt.

**Warum der Typ-Index bei `ldfld`/`stfld` mitsteht**, obwohl die Referenz ihn schon kennt: eine
Runtime muss den Feldindex **beim Laden** gegen ein Layout prüfen können (§6), nicht erst beim
Zugriff. Ohne den Typ im Instruktionsstrom bräuchte der Validator eine Datenfluss-Analyse, um
herauszufinden, welcher Typ an dieser Stelle auf dem Stack liegt. Der Index ist redundant zur
Laufzeit und genau deshalb billig — er wird nach der Validierung nicht mehr gelesen.

### Enums

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x68` | `newvariant` | `uleb128` variantType | −n +1 | legt eine Variante an; `n` = ihre Nutzfeldzahl |
| `0x69` | `enumtag` | — | −1 +1 | Tag der Variante als `i64` |
| `0x6A` | `enumas` | `uleb128` variantType | −1 +1 | engt auf eine Variante ein; **`panic`** bei falschem Tag |

`newvariant` nimmt die Nutzfelder vom Stack (das erste zuunterst) und setzt Slot 0 selbst auf den
Tag — der Compiler muss ihn nicht mitschicken, er steht im Typ.

**`match` hat keinen eigenen Opcode.** Es liest mit `enumtag` das Tag und verzweigt darüber wie
jede andere Fallunterscheidung; ein Sprungtabellen-Opcode wäre eine Optimierung, keine Semantik.
Nach der Verzweigung ist die Variante bekannt, und `enumas` macht daraus einen Wert ihres Typs —
danach ist der Feldzugriff ein gewöhnliches `ldfld` mit dem Layout der Variante.

Das ist dieselbe Form wie beim Optional: `optissome` prüft, `optget` löst ein. Hier prüft `enumtag`
und `enumas` löst ein. Beide Einlösungen können nicht scheitern, wenn der Compiler den Beweis
geführt hat — `enumas` panickt trotzdem bei falschem Tag, weil `.lyrbc` auch aus fremder Quelle
kommen kann.

### Interfaces

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x70` | `mkiface` | `uleb128` concreteType, `uleb128` interfaceType | −1 +1 | hebt eine Objektreferenz auf ihren Interface-Typ |
| `0x71` | `callvirt` | `uleb128` interfaceType, `uleb128` slot | −n +0/1 | ruft die Implementierung des Slots am konkreten Typ des Empfängers |

`mkiface` trägt **beide** Indizes, obwohl eine Runtime nur den konkreten braucht: so prüft der
Loader die Implementierungs-Beziehung gegen die Impls-Sektion, ohne eine Datenflussanalyse zu
fahren. Dieselbe Begründung wie beim Typ- und Feldindex am `ldfld` (§6).

`callvirt` erwartet den Empfänger als **Argument 0**, also zuunterst — dieselbe Konvention wie bei
jedem Methodenaufruf (`Sprache.md` ADR-014). `n` ist die Argumentzahl des Slots einschließlich
Empfänger.

**Es gibt keinen Downcast.** `Sprache.md` §6.5 lässt `as` ausschließlich zwischen Numerik zu; ein
Interface-Wert kann nicht zurück auf seine Klasse geprüft werden. Deshalb braucht er auch keine
Laufzeit-Typprüfung und keinen Fehlerfall.

### Structs

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x72` | `structcopy` | `uleb128` structType | −1 +1 | legt eine unabhängige Kopie eines Struct-Wertes ab |

**Es gibt kein `newstruct`.** Ein Struct-Wert wird mit `newobj` erzeugt wie ein Klassenobjekt —
zur Laufzeit ist es dieselbe Slot-Folge. Die gesamte Wert-Semantik steckt in `structcopy` und
darin, **wo** ein Compiler es setzt.

**Die Kopie ist rekursiv über verschachtelte Structs und flach über alles andere.** Ein Feld vom
Typ `class`, `T[]` oder `dyn` trägt eine Referenz, und die wird *geteilt*: kopiert wird der Wert,
nicht die Welt dahinter (`Sprache.md` §3.2). Ein Feld vom Typ `struct` ist dagegen selbst ein Wert
und muss mitkopiert werden — sonst sähe man eine Änderung an `a.inner.x` auch bei `b`. Die
Rekursion terminiert ohne Zyklen-Erkennung, weil ein Struct sich nicht selbst enthalten kann.

**Wo ein Compiler `structcopy` setzen muss**: an jedem Punkt, an dem ein Struct-Wert aus einer
bestehenden Stelle in eine neue gebunden wird — Initialisierung, Zuweisung, Argument, Rückgabe,
Feld- und Elementzuweisung. Ein frisch erzeugter Wert (`newobj`, Rückgabe eines Aufrufs) braucht
es nicht: er hat noch keinen anderen Besitzer.

Ein `structcopy` auf einem Typ, der **kein** Struct-Eintrag ist, muss ein Leser ablehnen. Es wäre
kein Fehler, den die Laufzeit bemerkt — sie kopierte klaglos ein Slot-Array, das geteilt gehört,
und die Semantik bräche still.

### Darstellung eines Interface-Wertes

Diese Spec schreibt Runtimes sonst keine Datenstrukturen vor. Hier tut sie es, aus demselben Grund
wie bei den Optionals: das beobachtbare Verhalten muss überall gleich sein.

Ein Wert vom Tag `0x44` trägt **zwei** Dinge: die Objektreferenz und den **Index seines konkreten
Typs** in der Types-Sektion. Wie eine Runtime das ablegt, ist ihre Sache — als Paar, als zwei
Register, als Zeiger auf einen Deskriptor.

**Verboten ist**, den konkreten Typ aus dem Objekt selbst zu lesen. Ein Objekt trägt in diesem
Format kein Typ-Tag (§Objekte), und ein `mkiface` ist die einzige Stelle, an der der Typ bekannt
ist. Eine Runtime, die stattdessen ein Tag ins Objekt schriebe, verschöbe jeden Feldindex und wäre
nicht mehr formatkonform.

Die Referenzimplementierung nutzt dafür die bei einer Referenz ohnehin ungenutzten Zahl-Bits ihres
Wert-Typs — ein Fat Pointer, wie Rusts `dyn Trait`. Das ist eine Empfehlung, keine Vorschrift.

### Optionals

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x60` | `optnone` | innerer Typ (§3) | +1 | legt „kein Wert" ab |
| `0x61` | `optsome` | innerer Typ (§3) | −1 +1 | verpackt den obersten Wert |
| `0x62` | `optissome` | — | −1 +1 | `bool`: liegt ein Wert vor? |
| `0x63` | `optget` | — | −1 +1 | packt aus; **`panic`**, wenn kein Wert |

`??`, `??=` und `?.` haben **keine** eigenen Opcodes. Sie werten ihre rechte Seite bzw. den
Member-Zugriff nur bedingt aus und lowern deshalb zu Verzweigungen über `optissome` — genau wie
`&&` und `||`. Ein Opcode dafür müsste einen unausgewerteten Ausdruck transportieren, und das kann
eine Stack-Maschine nicht.

`optget` bildet den Force-Unwrap `expr!` ab (Sprache.md §7).

**Ein Optional ist nicht schachtelbar**: `??T` gibt es nicht. Ein Leser **muss** einen inneren Typ
mit Tag `0x42` ablehnen. Ohne diese Regel wäre „kein Wert" mehrdeutig — die Darstellung unten
könnte die Ebenen nicht unterscheiden.

### Arrays

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x58` | `newarr` | Elementtyp (§3) | −n +1 | nimmt `n` Werte vom Stack, legt ein Array daraus an |
| `0x59` | `ldelem` | — | −2 +1 | Array, Index → Element |
| `0x5A` | `stelem` | — | −3 | Array, Index, Wert |
| `0x5B` | `arrlen` | — | −1 +1 | Länge als `i64` |
| `0x5C` | `arrcat` | — | −2 +1 | Konkatenation zweier Arrays → neues Array |
| `0x5D` | `arrrep` | — | −2 +1 | Array, Anzahl → neues Array, so oft wiederholt |

**`newarr` nimmt die Elementzahl als Immediate** (`uleb128`, nach dem Elementtyp) und dann so viele
Werte vom Stack, das erste Element zuunterst. Ein Literal `[3, 7, 1]` ist damit eine Instruktion und
nicht drei `stelem` — der häufige Fall bleibt kurz.

**Index-Verletzungen sind ein `panic`** (Sprache.md §9), kein undefiniertes Verhalten. Das gilt für
`ldelem`, `stelem` und `pop` auf einem leeren Array. Anders als Typ- und Feldindizes (§6) ist ein
Element-Index **nicht** beim Laden prüfbar — er ist ein Laufzeitwert. Das ist der Unterschied
zwischen „der Compiler hat Unsinn erzeugt" und „das Programm hat sich verrechnet"; nur das Erste
darf beim Laden abgefangen werden.

**`T[]` wächst nicht** (ADR-016). Die Länge steht bei der Erzeugung fest. `arrcat` und `arrrep`
liefern deshalb jeweils ein **neues** Array und ändern ihre Operanden nicht — sie bilden `xs + ys`
bzw. `xs * n` aus Sprache.md §6.5 ab, und das ist eingebaute Sprachsemantik, keine Bibliothek.
Wachsende Container (`List<T>`) sind gewöhnliche Klassen der Stdlib und brauchen im Format nichts
Eigenes: sie halten ein `T[]` und kopieren um.

`arrrep` mit Anzahl `0` liefert ein leeres Array; eine negative Anzahl ist ein `panic`.

### Darstellung eines Optionals

Diese Spec schreibt Runtimes normalerweise keine Datenstrukturen vor. Hier tut sie es, weil die
Wahl beobachtbar ist: `optissome` muss auf **jeder** Runtime dasselbe liefern.

Ein Wert ist genau dann „kein Wert", wenn seine **Referenz leer** ist. Für `?string`, `?T[]` und
`?Klasse` fällt das mit der natürlichen Darstellung zusammen — die Referenz ist der Wert. Für
`?int`, `?bool` und `?char` gibt es keine freien Bitmuster: jedes `i64` ist eine gültige Zahl. Eine
Runtime **muss** deshalb einen von Nutzdaten unterscheidbaren Marker führen, der „hat einen Wert"
bedeutet, und den Zahlenwert daneben halten.

Was sie ausdrücklich **nicht** darf: ein Bitmuster als „null" reservieren. `?int` muss alle
2⁶⁴ `int`-Werte tragen können — sonst wäre `-1` je nach Runtime mal ein Wert und mal keiner.

Ein Objekt trägt **kein** Typ-Tag zur Laufzeit. Der Instruktionsstrom weiß statisch, was vorliegt;
dieselbe Entscheidung wie bei den Werten (§4). Interface-Dispatch braucht später eine Typ-Identität
— die gehört dann an die vtable, nicht an jeden einzelnen Wert.

**Nullwert eines Feldes**: Zahlen `0`, `bool` false, `char` U+0000, `string` die leere
Zeichenkette, Referenzen die Null-Referenz. Kein Feld ist je „uninitialisiert" — `.lyrbc` ist ein
plattformneutraler Vertrag (ADR-013), und „undefiniert wie in C" ist an keiner Stelle zulässig
(Sprache.md §6.6). In Format 1.2 kann eine Null-Referenz allerdings nie beobachtet werden: das
Lowering erzeugt `newobj` und die Feld-Initialisierung immer zusammen, und Optionals (`?T`) sind
noch nicht gelowert.

---

## 6. Validierung beim Laden

Eine Runtime **muss** ein Modul vollständig prüfen, bevor sie es ausführt, und darf es danach ohne
Sicherheitsprüfungen laufen lassen. Ablehnungsgründe und ihre Diagnostik-Codes:

| Code | Grund |
|---|---|
| `LYR-BC0001` | Magic fehlt — keine `.lyrbc`-Datei |
| `LYR-BC0002` | unbekannte Major-Version |
| `LYR-BC0003` | Datei endet mitten in einer Struktur; Sektions-Länge passt nicht zum Inhalt |
| `LYR-BC0004` | Index zeigt ins Leere: String-Pool, Funktion, Block, Slot, **Typ oder Feld** |
| `LYR-BC0005` | unbekannter Opcode, unbekanntes Typ-Tag, Sektionen nicht aufsteigend |
| `LYR-BC0006` | Stack-Disziplin: Unterlauf, Tiefe ≠ 0 an einer Blockgrenze, Tiefe > `maxStack` |

Der Start-Index wird wie jeder andere Index geprüft (`LYR-BC0004`).

Für `newobj`/`ldfld`/`stfld` heißt „beim Laden geprüft" konkret: der Typ-Index liegt in der
Types-Sektion, und bei `ldfld`/`stfld` liegt der Feldindex innerhalb der Feldzahl **genau dieses**
Typs. Danach ist ein Feldzugriff zur Laufzeit ein Array-Zugriff ohne Prüfung — das ist der ganze
Zweck von ADR-013s „Validierung beim Load statt beim Call".

Der Leser bricht beim ersten Befund ab. Anders als der IR-Verifier sammelt er nicht: bei einer
kaputten Datei ist der zweite Befund meist Folge des ersten.

---

## 7. Beispiel

Quelle:

```lyr
fn add(a: int, b: int): int {
    return a + b;
}
```

Disassembly:

```
fn main.add -> i64 {
  params: 2
  maxstack: 2
  slots:
    l0: i64
    l1: i64
  bb0:
    ldloc 0
    ldloc 1
    add i64
    retval
}
```

Die vollständige Datei, 46 Bytes:

```
4C 59 52 42                  magic "LYRB"
02 00                        version.major = 2
00 00                        version.minor = 0

01                           § Sektion 1 — Capabilities
01                             byteLength = 1
00                             bitset = 0

02                           § Sektion 2 — Strings
0A                             byteLength = 10
01                             count = 1
08                             [0] Länge = 8 Byte
6D 61 69 6E 2E 61 64 64          "main.add"

04                           § Sektion 4 — Imports
01                             byteLength = 1
00                             count = 0

05                           § Sektion 5 — Functions
12                             byteLength = 18
01                             count = 1
00                             nameIndex = 0            -> "main.add"
02                             paramCount = 2
04                             returnType = i64
02                             slotCount = 2
04 04                          slotTypes = i64, i64
02                             maxStack = 2
01                             blockCount = 1
00                             blockOffsets[0] = 0
07                             codeLength = 7
02 00                            ldloc 0
02 01                            ldloc 1
10 04                            add i64
42                               retval
```

Die Slot-Tabelle hat zwei Einträge, nicht fünf: die Zwischenwerte bleiben auf dem Operanden-Stack
und brauchen keinen Slot. Ein Emitter, der jedes Zwischenergebnis in einen Slot schreibt, ist
ebenfalls konform — er erzeugt nur größeren und langsameren Code.

---

## 8. Was in Format 2.0 fehlt

Absichtlich, weil das Lowering es noch nicht erzeugt: `struct` (Wert-Semantik), `enum`, Arrays,
Tupel, Nullable, Exceptions, Coroutinen-State-Machines, Closures, generische Instanzen und
Interface-Dispatch. Jedes davon braucht neue Opcodes oder Sektionen und damit eine neue
Format-Version — mit Ausnahme von `enum` und `struct`, die die Types-Sektion mitbenutzen können.

Ebenfalls offen, aber ohne Format-Änderung nachrüstbar: die Source-Map-Sektion (Id 6 ist reserviert
und beschrieben, wird aber noch nicht geschrieben) und Copy-Propagation im Emitter — heute erzeugt
ein Temp mit mehreren Lesern ein `ldloc`/`stloc`-Paar, das ein Optimierer einsparen könnte.

---

## 9. Runner-Vertrag

Dieser Abschnitt ist **normativ** und gilt für jede Runtime, die als `lyric --vm <pfad>` eingesetzt
werden soll (ADR-017). Er ist die Antwort auf die Frage, die §1–§8 offen lassen: eine Spec sagt, was
in der Datei steht — nicht, wie man eine Runtime *aufruft*, die sie ausführt. Ohne diesen Abschnitt
wäre „austauschbare Runtime" eine Behauptung.

Er hat vier Punkte, und die Kürze ist Absicht (siehe „Was hier bewusst fehlt").

### 9.1 Aufruf

```
<vm> run <datei.lyrbc> [-- <programm-args>]
```

Der erste Parameter ist wörtlich `run`. Alles nach dem ersten `--` gehört dem Lyric-Programm, nicht
der Runtime. Eine Runtime, die weitere Kommandos anbietet (`disasm`, `verify`, …), darf das — der
Vertrag verlangt nur, dass `run` existiert und sich so verhält.

### 9.2 Exit-Codes

| Code | Bedeutung |
|---|---|
| `0`–`255` | Rückgabewert von `main`, maskiert mit `& 0xFF` (`Sprache.md` §11) |
| `101` | `panic` (§9 der Sprachspec) |
| `1` | Lade-, Validierungs- oder IO-Fehler — das Programm lief nie an |
| `2` | Aufruf-Fehler: fehlendes Argument, unbekanntes Kommando, falsche Dateiart |

`101`, `1` und `2` kollidieren mit einem Programm, das diese Werte selbst zurückgibt. Das ist
unvermeidbar, sobald beides durch einen Byte-Kanal läuft; Rust lebt mit derselben Kollision. Wer die
Unterscheidung braucht, liest stderr.

### 9.3 Ströme

- **stdout** trägt ausschließlich die Ausgabe des Lyric-Programms.
- **stderr** trägt ausschließlich Diagnosen, Panic-Meldungen und Backtraces.

Keine Vermischung in beide Richtungen. Ohne diese Trennung kann ein aufrufendes Werkzeug die
Ausgabe eines Programms nicht von der Klage der Runtime unterscheiden.

### 9.4 Versionsauskunft

```
<vm> --version
```

liefert freien Text auf stdout und Exit-Code `0`. Der Treiber reicht ihn durch und **interpretiert
ihn nicht**. Es gibt bewusst kein maschinenlesbares Format: siehe unten.

### Was hier bewusst fehlt

**Kein Capability-Probe.** Der naheliegende fünfte Punkt wäre ein `<vm> --lyrbc-versions`, damit ein
Treiber vorab sagen kann „deine Runtime spricht nur 1.4, dieses Modul ist 2.0". Er entfällt, weil
§2 bereits verlangt, dass jede Runtime eine unbekannte Major-Version **beim Laden** ablehnt, und §6
dasselbe für Import-Namen und -Signaturen tut. Die Fremd-Runtime liefert die präzise Meldung also
von allein. Ein Probe wäre ein zweiter Kompatibilitäts-Mechanismus neben der Load-Zeit-Validierung —
und der teurere, weil ihn jede Runtime nachbauen müsste.

**Kein Handshake, kein Daemon, kein IPC.** Ein Lauf ist ein Prozessstart.

**Keine Vorgabe zur Disassembly.** `lyric disasm` benutzt immer den mitgelieferten Disassembler,
auch wenn eine Fremd-Runtime gewählt ist: das *Format* ist spezifiziert, seine Textdarstellung ist
es nicht.

### Konformanz prüfen

Die mitgelieferte Runtime ist die Referenz-Implementierung dieses Abschnitts. Für ein gegebenes
Modul beantwortet

```
lyrvm verify <datei.lyrbc>
```

die Frage „würde diese Runtime das Modul annehmen" — Format-Validierung (§6) und Import-Bindung,
ohne eine Instruktion auszuführen. Eine zweite Runtime, die dasselbe Urteil fällt, ist an dieser
Stelle konform.

### Offen

`-- <programm-args>` ist hier spezifiziert, aber vom aktuellen Stand **nicht einlösbar**:
`Sprache.md` §11 kennt `fn main(args: string[])`, das IR-Lowering nimmt jedoch nur ein
parameterloses `main` als Einstieg. Die mitgelieferte Runtime lehnt übergebene Argumente deshalb ab
(`LYR-CLI0007`), statt sie still zu verwerfen — eine Runtime, die vorgibt, Argumente zugestellt zu
haben, wäre schlimmer als eine, die es zugibt.
