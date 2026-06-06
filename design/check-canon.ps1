#requires -Version 5.1
<#
.SYNOPSIS
    Canonical-spec drift gate for the design/ docs.

.DESCRIPTION
    Fails (exit 1) if a known-stale token reappears anywhere in the LIVE design tree.
    `design/archive/` is excluded (historical docs are allowed to contain superseded forms).
    To intentionally mention a superseded form in live prose (e.g. to explain a correction),
    put the marker `canon-allow` on that line (an HTML comment `<!-- canon-allow: reason -->`
    is the convention). See SPEC-INDEX.md for the canonical values these rules protect.

.EXAMPLE
    pwsh design/check-canon.ps1     # or: powershell -File design\check-canon.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot
)

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
    }
)

$docs = Get-ChildItem -Path $Root -Recurse -Filter *.md |
    Where-Object { $_.FullName -notmatch '[\\/]archive[\\/]' }

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
