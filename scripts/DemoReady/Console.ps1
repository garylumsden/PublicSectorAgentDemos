# Console presentation helpers for the demo-ready orchestrator.
# Dot-source this file. Every function writes to the host and changes no state.

$script:DemoReadyGlyphSet = $null
$script:DemoReadyWidth = 0

function Get-DemoReadyGlyphSet {
    <#
    .SYNOPSIS
        Returns the glyph set that the current console can render.
    #>
    [CmdletBinding()]
    param([switch]$Force)

    if ($null -ne $script:DemoReadyGlyphSet -and -not $Force) {
        return $script:DemoReadyGlyphSet
    }
    $unicode = $false
    try {
        $unicode = [Console]::OutputEncoding.CodePage -eq 65001
    }
    catch {
        $unicode = $false
    }
    $script:DemoReadyGlyphSet = $unicode `
        ? [ordered]@{
            Ok = [string][char]0x2714
            No = [string][char]0x2718
            Warn = [string][char]0x25B2
            Info = [string][char]0x25CF
            Pending = [string][char]0x25CB
            Arrow = [string][char]0x25B8
            Bullet = [string][char]0x2022
            Horizontal = [string][char]0x2500
            Vertical = [string][char]0x2502
            TopLeft = [string][char]0x250C
            TopRight = [string][char]0x2510
            BottomLeft = [string][char]0x2514
            BottomRight = [string][char]0x2518
        } `
        : [ordered]@{
            Ok = '+'
            No = 'x'
            Warn = '!'
            Info = '*'
            Pending = '-'
            Arrow = '>'
            Bullet = '-'
            Horizontal = '-'
            Vertical = '|'
            TopLeft = '+'
            TopRight = '+'
            BottomLeft = '+'
            BottomRight = '+'
        }
    return $script:DemoReadyGlyphSet
}

function Get-DemoReadyConsoleWidth {
    <#
    .SYNOPSIS
        Returns a stable render width between 60 and 100 columns.
    #>
    [CmdletBinding()]
    param()

    if ($script:DemoReadyWidth -gt 0) {
        return $script:DemoReadyWidth
    }
    $width = 78
    try {
        $raw = [int]$Host.UI.RawUI.WindowSize.Width
        if ($raw -gt 0) {
            $width = $raw - 2
        }
    }
    catch {
        $width = 78
    }
    if ($width -lt 60) { $width = 60 }
    if ($width -gt 100) { $width = 100 }
    $script:DemoReadyWidth = $width
    return $width
}

function Get-DemoReadyStatusStyle {
    <#
    .SYNOPSIS
        Maps a status name to its glyph and colour.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('ok', 'no', 'warn', 'info', 'pending', 'step')]
        [string]$Status = 'info'
    )

    $glyphs = Get-DemoReadyGlyphSet
    switch ($Status) {
        'ok' { return [pscustomobject]@{ Glyph = $glyphs.Ok; Color = 'Green' } }
        'no' { return [pscustomobject]@{ Glyph = $glyphs.No; Color = 'Red' } }
        'warn' { return [pscustomobject]@{ Glyph = $glyphs.Warn; Color = 'Yellow' } }
        'pending' { return [pscustomobject]@{ Glyph = $glyphs.Pending; Color = 'DarkGray' } }
        'step' { return [pscustomobject]@{ Glyph = $glyphs.Arrow; Color = 'Cyan' } }
        default { return [pscustomobject]@{ Glyph = $glyphs.Info; Color = 'Cyan' } }
    }
}

function Write-DemoReadyRule {
    <#
    .SYNOPSIS
        Writes a horizontal rule.
    #>
    [CmdletBinding()]
    param([string]$Color = 'DarkGray')

    $glyphs = Get-DemoReadyGlyphSet
    Write-Host ($glyphs.Horizontal * (Get-DemoReadyConsoleWidth)) -ForegroundColor $Color
}

function Write-DemoReadyBanner {
    <#
    .SYNOPSIS
        Writes the boxed command banner.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Title,
        [string[]]$Lines = @()
    )

    $glyphs = Get-DemoReadyGlyphSet
    $width = Get-DemoReadyConsoleWidth
    $inner = $width - 2
    Write-Host ''
    Write-Host ("$($glyphs.TopLeft)$($glyphs.Horizontal * $inner)$($glyphs.TopRight)") -ForegroundColor Cyan
    Write-Host $glyphs.Vertical -ForegroundColor Cyan -NoNewline
    Write-Host (' ' + $Title.PadRight($inner - 1)) -ForegroundColor White -NoNewline
    Write-Host $glyphs.Vertical -ForegroundColor Cyan
    foreach ($line in @($Lines)) {
        Write-Host $glyphs.Vertical -ForegroundColor Cyan -NoNewline
        Write-Host (' ' + ([string]$line).PadRight($inner - 1)) -ForegroundColor DarkGray -NoNewline
        Write-Host $glyphs.Vertical -ForegroundColor Cyan
    }
    Write-Host ("$($glyphs.BottomLeft)$($glyphs.Horizontal * $inner)$($glyphs.BottomRight)") -ForegroundColor Cyan
}

function Write-DemoReadySection {
    <#
    .SYNOPSIS
        Writes a numbered section heading.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Title,
        [int]$Step = 0,
        [int]$TotalSteps = 0
    )

    $glyphs = Get-DemoReadyGlyphSet
    Write-Host ''
    $prefix = ($Step -gt 0 -and $TotalSteps -gt 0) ? "$($glyphs.Arrow) Step $Step of $TotalSteps  " : "$($glyphs.Arrow) "
    Write-Host $prefix -ForegroundColor DarkCyan -NoNewline
    Write-Host $Title -ForegroundColor Cyan
    Write-DemoReadyRule
}

function Write-DemoReadyStatus {
    <#
    .SYNOPSIS
        Writes one glyph-prefixed status line.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Message,
        [ValidateSet('ok', 'no', 'warn', 'info', 'pending', 'step')]
        [string]$Status = 'info',
        [int]$Indent = 2,
        [string]$MessageColor = ''
    )

    $style = Get-DemoReadyStatusStyle -Status $Status
    Write-Host ((' ' * $Indent) + $style.Glyph + ' ') -ForegroundColor $style.Color -NoNewline
    if ([string]::IsNullOrWhiteSpace($MessageColor)) {
        Write-Host $Message
    }
    else {
        Write-Host $Message -ForegroundColor $MessageColor
    }
}

function Write-DemoReadyField {
    <#
    .SYNOPSIS
        Writes an aligned label and value pair.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [int]$LabelWidth = 22,
        [int]$Indent = 4,
        [string]$ValueColor = 'White'
    )

    Write-Host ((' ' * $Indent) + $Label.PadRight($LabelWidth)) -ForegroundColor DarkGray -NoNewline
    Write-Host $Value -ForegroundColor $ValueColor
}

function Write-DemoReadyTable {
    <#
    .SYNOPSIS
        Writes an aligned text table with a header rule.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Headers,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows,
        [string[]]$Align = @(),
        [int]$Indent = 4
    )

    $columnCount = $Headers.Count
    $widths = @(0) * $columnCount
    for ($index = 0; $index -lt $columnCount; $index++) {
        $widths[$index] = $Headers[$index].Length
    }
    $normalized = [Collections.Generic.List[string[]]]::new()
    foreach ($row in @($Rows)) {
        $cells = @($row)
        $flattened = $columnCount -gt 1 -and
            ($cells.Count -eq 1 -or @($cells | Where-Object { $_ -is [Array] }).Count -gt 0)
        if ($flattened) {
            throw (
                'Write-DemoReadyTable received a flattened or nested row. ' +
                'Emit each row with the comma operator, for example ", @($first, $second)".')
        }
        $cells = @(0..($columnCount - 1) | ForEach-Object {
            $_ -lt $cells.Count ? [string]$cells[$_] : ''
        })
        for ($index = 0; $index -lt $columnCount; $index++) {
            if ($cells[$index].Length -gt $widths[$index]) {
                $widths[$index] = $cells[$index].Length
            }
        }
        $normalized.Add($cells)
    }

    $glyphs = Get-DemoReadyGlyphSet
    $pad = {
        param([string]$Text, [int]$Width, [int]$Column)
        $alignment = $Column -lt $Align.Count ? $Align[$Column] : 'left'
        return $alignment -ceq 'right' ? $Text.PadLeft($Width) : $Text.PadRight($Width)
    }

    $headerLine = (' ' * $Indent) + (@(0..($columnCount - 1) | ForEach-Object {
        & $pad $Headers[$_] $widths[$_] $_
    }) -join '  ')
    Write-Host $headerLine -ForegroundColor DarkGray
    $ruleLine = (' ' * $Indent) + (@(0..($columnCount - 1) | ForEach-Object {
        $glyphs.Horizontal * $widths[$_]
    }) -join '  ')
    Write-Host $ruleLine -ForegroundColor DarkGray
    foreach ($cells in $normalized) {
        $line = (' ' * $Indent) + (@(0..($columnCount - 1) | ForEach-Object {
            & $pad $cells[$_] $widths[$_] $_
        }) -join '  ')
        Write-Host $line
    }
}

function Write-DemoReadyOrderedStep {
    <#
    .SYNOPSIS
        Writes one numbered action in the planned action list.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Number,
        [Parameter(Mandatory)][string]$Message,
        [int]$Indent = 4
    )

    Write-Host ((' ' * $Indent) + ('{0,2}. ' -f $Number)) -ForegroundColor DarkCyan -NoNewline
    Write-Host $Message
}
