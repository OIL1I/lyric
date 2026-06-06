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

Phase: Core-Schicht in Arbeit. Solution + CI stehen, SourceManager
fertig. Es fehlt noch DiagnosticEngine, dann ist M0 fertig.

## Was schon erledigt ist

- [x] Repo, Doku-Files, CONTRIBUTING, LICENSE, README, .gitignore
- [x] CLAUDE.md, STATUS.md für Session-Persistenz
- [x] .NET-Solution mit Lyric.Core, Lyric.Cli, Lyric.Tests.Core
- [x] GitHub Actions CI (Ubuntu + Windows Matrix), `dotnet build`/`test` grün
- [x] `FileId`, `Span` (mit UTF-16-Code-Unit-Offsets)
- [x] `SourceManager` + `LinePosition`: File-Laden (Disk+Virtual), Pfad-Zugriff,
  Locate(offset)→1-basierte (Line, Col), Slice(span), GetLineText,
  LineCount, FileCount — mit 50+ Tests grün

## Woran wir gerade arbeiten

**`DiagnosticEngine`** (Lieferposten 2 von 2 für M0-Core).

Ausstehend:
- `Diagnostic`-Record (Code, Severity, Span, Message)
- Sammeln, deterministisch sortieren (File → Start → End → Code)
- Text-Renderer (mit Source-Kontext, Underline-Caret)
- JSON-Renderer
- Diagnostik-Codes `LYR-CLI####` für CLI-Eingangsfehler

## Was als nächstes ansteht

Nach DiagnosticEngine:
- M0-Exit-Kriterium prüfen: `dotnet build` grün, CI grün, `lyric --version`
  funktioniert. → `m0-complete` Tag setzen.
- Wechsel auf **M1 — Lexer**.

## Offene Fragen / Diskussions-Punkte

- DiagnosticEngine: Sammler-Modell (eine Collect-Phase, dann Render) oder
  Streaming-Modell (Diagnostics während des Compiles ausgeben)?
  → Entscheidung im DiagnosticEngine-Plan.
- JSON-Renderer-Schema: eigenes Format oder am Roslyn-/LSP-Schema orientieren?
  → Klein anfangen, eigenes Format; LSP-Kompatibilität ist M9/Post-v1.

## Letzter relevanter Commit

`M0: implement SourceManager with offset→line/column lookup`

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
