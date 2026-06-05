# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M0 — Setup, Architekturrahmen, Test-Infra**

Phase: Setup läuft, .NET-Solution-Aufbau in Arbeit.

## Was schon erledigt ist

- [x] Repo angelegt, `git init`, erster Commit mit Boilerplate
- [x] Doku-Files in `docs/` (Sprache, Doku, ROADMAP, IDEAS)
- [x] `CONTRIBUTING.md`, `LICENSE` (MIT), `README.md`, `.gitignore`
- [x] GitHub-Issue-Template
- [x] CLAUDE.md, STATUS.md (diese Datei) für Session-Persistenz

## Woran wir gerade arbeiten

`.NET-Solution-Setup` (Schritt A aus dem M0-Walkthrough). Sub-Steps:

- [ ] `dotnet new sln`, `Lyric.Core` (classlib), `Lyric.Cli` (console),
      `Lyric.Tests.Core` (xunit) erzeugen
- [ ] Projekte zur Solution hinzufügen, Referenzen einrichten
- [ ] Template-Müll (`Class1.cs`, `UnitTest1.cs`, Template-`Program.cs`) raus
- [ ] Eigene Files: `FileId.cs`, `Span.cs`, `Program.cs` (CLI-Stub),
      `SpanTests.cs`, `FileIdTests.cs`
- [ ] `dotnet build` + `dotnet test` lokal grün
- [ ] CI-Workflow committen, GitHub-Repo erzeugen, ersten Push
- [ ] CI-Badge in README aktiviert (✓ schon drin, OIL1I/lyric)

## Was als nächstes ansteht (im aktuellen Meilenstein)

Nach Abschluss des Setup-Sub-Steps oben:

1. **`SourceManager`-Implementierung** (~80 Zeilen + Tests)
   - File-Loading aus Pfad → `FileId`
   - Offset → (Zeile, Spalte) Lookup mit gecachten Line-Starts
   - Source-Text-Zugriff über `FileId`
2. **`DiagnosticEngine`-Implementierung** (~100 Zeilen + Tests)
   - `Diagnostic`-Record (Code, Severity, Span, Message)
   - Sammeln, deterministisch sortieren (File → Start → End → Code)
   - Text-Renderer + JSON-Renderer

Beides Lieferposten von M0 laut `docs/ROADMAP.md`.

## Offene Fragen / Diskussions-Punkte

_(Nichts offen aktuell.)_

## Letzter relevanter Commit

`(noch nicht vorhanden — Setup läuft)`

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
