#requires -Version 5.1
<#
.SYNOPSIS
    Canonical-spec drift gate for the docs/design/ docs.

.DESCRIPTION
    Fails (exit 1) if a known-stale token reappears anywhere in the LIVE design tree.
    `docs/design/archive/` is excluded (historical docs are allowed to contain superseded forms).
    To intentionally mention a superseded form in live prose (e.g. to explain a correction),
    put the marker `canon-allow` on that line (an HTML comment `<!-- canon-allow: reason -->`
    is the convention). See SPEC-INDEX.md for the canonical values these rules protect.

.EXAMPLE
    pwsh docs/design/check-canon.ps1     # or: powershell -File docs\design\check-canon.ps1
#>
[CmdletBinding()]
param(
    # NOT defaulted to $PSScriptRoot in the param block: under `powershell -File <relative path>` on Windows
    # PowerShell 5.1, parameter defaults bind BEFORE $PSScriptRoot is in scope, so it silently evaluated to ''.
    # Get-ChildItem -Path '' then falls back to the CWD and scanned the WHOLE repo — including stale
    # .claude/worktrees copies of docs/design — reporting CANON DRIFT that does not exist in the live tree,
    # on exactly the invocation CLAUDE.md documents.
    [string]$Root
)

if ([string]::IsNullOrWhiteSpace($Root)) { $Root = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($Root) -and $MyInvocation.MyCommand.Path) {
    $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($Root)) { throw 'check-canon: could not resolve the design-docs root.' }
$Root = (Resolve-Path -LiteralPath $Root).Path

$ErrorActionPreference = 'Stop'

# Each rule: a regex (case-insensitive) that must NOT appear in any live design doc.
# Patterns are SINGLE-quoted so backticks/backslashes are literal.
$rules = @(
    @{
        Name    = 'handle-layout'
        Pattern = '24-bit generation|index32,\s*gen24|gen24,\s*kind8'
        Why     = 'Handle is {u32 index, u32 gen} (architecture-spec 4.1). The 24-bit-gen + 8-bit-kind form is superseded.'
    },
    @{
        Name    = 'com-blanket'
        Pattern = 'All COM via hand-vtable|ComWrappers strategy|both COM directions use hand'
        Why     = 'COM is tiered (dotnet10 sec.4): hand-vtable hot path + [GeneratedComInterface] cold. The blanket "no ComWrappers anywhere" rule is superseded.'
    },
    @{
        Name    = 'depkey-union'
        Pattern = '\[FieldOffset\(\d+\)\]\s*public\s+readonly\s+(object|string)'
        Why     = 'DepKey is pure-scalar + a side GcDepTable. A [FieldOffset] GC-ref/scalar union is illegal CLR layout (TypeLoadException).'
    },
    @{
        Name    = 'spotlight-dim'
        Pattern = 'SpotlightBackgroundOpacity|SetDropSpotlightExempt|IsUnderDropSpotlightExempt'
        Why     = 'The drop-spotlight dim is an explicit SCRIM BAND with per-target cutouts (gpu-renderer.md 7.4 + input-a11y.md): DragVisualTok.ScrimColor/ScrimOpacity + SceneStore.SpotlightScrimClip + DropTargetSpec.SpotlightWhen. The 0.28 per-node opacity multiply/divide token and the presentation-only spotlight-exemption registry are deleted.'
    },
    @{
        Name    = 'bind-props'
        Pattern = '\b(Transform|Opacity|Fill|Width|Height|Text|Color|Source|Placeholder)Bind\b'
        Why     = 'The dual static+*Bind element surface is superseded by one Prop<T> per bindable channel (reconciler-hooks.md sec.0bis). The *Bind property spelling is gone.'
    },
    @{
        Name    = 'path-aa-config'
        Pattern = 'RenderConfig\.PathAaMode'
        Why     = 'the as-built flag is GpuProfile.PathAaMode (gpu-renderer.md sec.5); there is no RenderConfig type.'
    },
    @{
        Name    = 'path-earclip'
        Pattern = 'ear-?clipping'
        Why     = 'canon DELETED ear-clipping (gpu-renderer.md sec.5): one vetted O(n log n) monotone/trapezoidal sweep.'
    }
)

$docs = Get-ChildItem -Path $Root -Recurse -Filter *.md |
    Where-Object { $_.FullName -notmatch '[\\/]archive[\\/]' } |
    # Belt-and-braces: a leftover git worktree carries its own older copy of docs/design. Linting it reports
    # drift that does not exist in the live tree and cannot be fixed from here, so never scan one.
    Where-Object { $_.FullName -notmatch '[\\/]\.claude[\\/]' -and $_.FullName -notmatch '[\\/]worktrees[\\/]' }

$violations = New-Object System.Collections.Generic.List[object]
foreach ($rule in $rules) {
    $hits = $docs | Select-String -Pattern $rule.Pattern
    foreach ($hit in $hits) {
        if ($hit.Line -match 'canon-allow') { continue }   # explicit opt-out
        $violations.Add([pscustomobject]@{
            Rule = $rule.Name
            File = (Resolve-Path -Relative $hit.Path)
            Line = $hit.LineNumber
            Text = $hit.Line.Trim()
            Why  = $rule.Why
        })
    }
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "CANON DRIFT DETECTED ($($violations.Count) violation(s)) - see SPEC-INDEX.md:" -ForegroundColor Red
    Write-Host ""
    foreach ($v in $violations) {
        Write-Host ("  [{0}] {1}:{2}" -f $v.Rule, $v.File, $v.Line) -ForegroundColor Yellow
        Write-Host ("      {0}" -f $v.Text)
        Write-Host ("      -> {0}" -f $v.Why) -ForegroundColor DarkGray
        Write-Host ""
    }
    Write-Host "Fix the token, or add 'canon-allow' to the line if the mention is intentional." -ForegroundColor Red
    exit 1
}

Write-Host ("Canon OK: no stale tokens in the live design tree ({0} docs scanned, archive/ excluded)." -f $docs.Count) -ForegroundColor Green
exit 0
