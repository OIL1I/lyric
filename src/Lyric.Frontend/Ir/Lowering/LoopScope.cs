namespace Lyric.Ir.Lowering;

/// <summary>
/// Wohin <c>break</c> und <c>continue</c> der innersten Schleife springen.
///
/// <para><b>Die Ziele entstehen bedarfsgesteuert.</b> Bis 2026-08-11 wurden sie vorab angelegt,
/// und das hat <c>do { return … } while (…)</c> zum Compiler-Absturz gemacht: terminiert der Rumpf,
/// erreicht niemand Bedingung und Ausgang, und der Verifier lehnt unerreichbare Bloecke ab (es gibt
/// keinen <c>SimplifyCfg</c>-Pass in v1). Ein Block, den niemand betritt, entsteht jetzt gar nicht
/// erst.</para>
///
/// <para><b>Warum nicht am Durchfall-Flag entschieden werden kann</b>, wie STATUS es lange
/// vermutete: <c>do { if (c) { break; } return 1; }</c> faellt nicht durch und erreicht den Ausgang
/// trotzdem. „Ist der Block erreichbar" und „faellt der Rumpf durch" sind zwei verschiedene Fragen,
/// und nur die erste zaehlt hier. Deshalb merkt sich dieser Typ, ob jemand das Ziel wirklich
/// angefordert hat.</para>
///
/// <para>Dieselbe Loesung wie beim Merge-Block von <c>try</c>/<c>match</c> im Sweep vom
/// 2026-08-07 — dort war es zweimal dieselbe Ursache, und die Lehre lautete bereits: <b>ein
/// Merge-Block gehoert grundsaetzlich bedarfsgesteuert</b>. Hier ist der dritte Fall.</para>
/// </summary>
internal sealed class LoopScope(BlockBuilder blocks)
{
    private BlockId? _continue;
    private BlockId? _break;

    /// <summary>
    /// Fuer <c>while</c> und <c>for-in</c>: dort sind beide Bloecke <b>immer</b> erreichbar — die
    /// Bedingung ueber die Einstiegskante, der Ausgang ueber ihre false-Kante. Sie muessen zudem
    /// vorher existieren, weil die <c>CondBranch</c> sie nennt, bevor der Rumpf gelowert wird.
    ///
    /// <para>Nur <c>do-while</c> hat das Problem: dort steht die Bedingung <b>hinter</b> dem
    /// Rumpf, und terminiert der, erreicht sie niemand.</para>
    /// </summary>
    public LoopScope(BlockBuilder blocks, BlockId continueTarget, BlockId breakTarget)
        : this(blocks)
    {
        _continue = continueTarget;
        _break = breakTarget;
    }

    /// <summary>Ziel von <c>continue</c> — bei <c>while</c> die Bedingung, bei <c>do-while</c>
    /// ebenfalls, bei <c>for-in</c> der Schleifenkopf.</summary>
    public BlockId ContinueTarget => _continue ??= blocks.NewBlock();

    /// <summary>Ziel von <c>break</c>.</summary>
    public BlockId BreakTarget => _break ??= blocks.NewBlock();

    /// <summary>Hat jemand das Ziel angefordert? Nur dann existiert der Block.</summary>
    public bool ContinueRequested => _continue is not null;

    /// <inheritdoc cref="ContinueRequested"/>
    public bool BreakRequested => _break is not null;
}
