# 1. Принудительно ставим UTF-8 для консоли и вывода
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$output       = "project_context.txt"
$allowedExts  = @('.cs','.csproj','.razor','.json','.config','.xml','.md','.txt','.yml')
$excludeRegex = 'bin|obj|\.git|\.vs|node_modules|wwwroot|Storage|Migrations|project_context\.txt|project_dump\.txt|monitor_test\.db'
$maxSize      = 5MB

# 2. Ищем файлы
$files = Get-ChildItem -Recurse -File | Where-Object {
    $_.FullName -notmatch $excludeRegex -and
    $allowedExts -contains $_.Extension -and
    $_.Length -le $maxSize
}

$total   = $files.Count
$counter = 0

# 3. Создаём файл с явной UTF-8 кодировкой
"=== Project Context ===" | Out-File -FilePath $output -Encoding UTF8 -Force

# 4. Обрабатываем
foreach ($f in $files) {
    $counter++
    $rel = $f.FullName.Replace((Get-Location).Path, '').TrimStart('\')

    "`n`n### File: $rel ###`n" | Out-File -FilePath $output -Append -Encoding UTF8
    Get-Content $f.FullName -Raw -Encoding UTF8 | Out-File -FilePath $output -Append -Encoding UTF8

    # Безопасный прогресс-бар (встроенный в PS)
    $pct = [math]::Round(($counter / $total) * 100)
    Write-Progress -Activity "Сборка контекста" -Status "Файл: $rel" -PercentComplete $pct
}

# 5. Итог (только ASCII + кириллица, без эмодзи)
Write-Host "`n[OK] Готово! Файл сохранён: $output"
$sizeKB = [math]::Round((Get-Item $output).Length / 1KB, 1)
Write-Host "[INFO] Размер файла: $sizeKB КБ"