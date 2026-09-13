# TEMPORARY. The revert-check for what the tree delete leaves behind when
# something in it will not go: the one-line mutation is the framework's delete,
# which is what the branch changed away from. Removed before committing.
$ErrorActionPreference = "Continue"

$ops = "src/Vaktari.Windows/WindowsFileOperations.cs"
$test = "Vaktari.Windows.Tests.DeleteTests.A_file_that_will_not_go_leaves_the_rest_of_its_tree_standing"

$mutations = @(
  @{ n = "M1 the permanent delete walks the tree itself";
     f = $ops;
     from = "if (Directory.Exists(path)) DeleteTree(path);";
     to   = "if (Directory.Exists(path)) Directory.Delete(path, recursive: true);";
     test = $test }
)

$results = @()

foreach ($m in $mutations) {
    Write-Host ""
    Write-Host "=============================================================="
    Write-Host "$($m.n)"
    Write-Host "  from : $($m.from.Trim())"
    Write-Host "  to   : $($m.to.Trim())"
    Write-Host "  test : $($m.test)"

    $text = [IO.File]::ReadAllText($m.f)

    $count = ([regex]::Matches($text, [regex]::Escape($m.from))).Count
    if ($count -ne 1) { throw "the line to mutate occurs $count times, not once" }
    if ($m.to.Contains($m.from.Trim())) { throw "the mutation contains its original; it could not be restored" }

    [IO.File]::WriteAllText($m.f, $text.Replace($m.from, $m.to))

    $out = (& dotnet test tests/Vaktari.Windows.Tests -c Debug --filter "FullyQualifiedName=$($m.test)" 2>&1 | Out-String)

    foreach ($line in ($out -split "`n" | Select-String -Pattern "error CS|Failed!|Passed!|Error Message|Assert\.|could not find|being used" | Select-Object -First 10)) {
        Write-Host "    $($line.ToString().Trim())"
    }

    git checkout -- $m.f

    $verdict =
        if ($out -match "error CS")      { "DID NOT COMPILE" }
        elseif ($out -match "Failed!")   { "red" }
        elseif ($out -match "Passed!")   { "STILL GREEN" }
        else                             { "UNREADABLE" }

    Write-Host "  verdict: $verdict"
    $results += [pscustomobject]@{ Mutation = $m.n; Verdict = $verdict }
}

Write-Host ""
Write-Host "=== revert-check summary ==="
$results | Format-Table -AutoSize | Out-String | Write-Host

git status --porcelain
if ((git status --porcelain) -ne $null) { throw "a mutation was left behind" }

if ($results.Verdict -ne "red") { throw "a mutation did not do its job" }
