param([string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
$proof=Join-Path $run 'published-verified.json'
if(-not(Test-Path -LiteralPath ($proof+'.SUCCESS.txt'))){throw 'Final native verification has not completed'}
$result=Get-Content -LiteralPath $proof -Raw|ConvertFrom-Json
$before=Get-Content -LiteralPath (Join-Path $run 'source-manifest.json') -Raw|ConvertFrom-Json
$journal=Get-Content -LiteralPath (Join-Path $run 'published.jsonl')|ForEach-Object{$_|ConvertFrom-Json}
foreach($file in $before){
    $change=$journal|Where-Object Name -eq $file.Name
    $expected=if($change){$change.After}else{$file.Sha256}
    if((Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash -ne $expected){throw "Final file changed: $($file.Name)"}
}
foreach($file in ($journal|Where-Object NewFile)){
    if((Get-FileHash -LiteralPath $file.Destination -Algorithm SHA256).Hash -ne $file.After){throw "New file changed: $($file.Name)"}
}
$text=@"
1103 装配体 workstation 整理结果

总装配：$($result.After.Root)
assembly_version：8

第一层 workstation 子装配体：
  CO2培养箱工位.SLDASM
  冰箱1.SLDASM
  冰箱2.SLDASM
  水浴组件.SLDASM
  BSC工位.SLDASM

保留用户已建立的培养箱、第一台冰箱和冰箱门分组，并将内嵌装配体保存为外部文件。
第二台冰箱和水浴组件归入具名工位。
BSC工位保留原 BSC 部件、96 个枪头的原生线性阵列，以及原来已压缩的电动助吸器快换总装。
原 BSC（带上下拉门）部件作为 BSC工位 内的普通子装配体，workstation 标记为 0。
房间布局和原先已压缩的两个立式冰箱仍留在总装配第一层。

验证结果：
  原有 391 个组件的位置、配置和压缩状态均保持一致。
  新增 3 个分组节点，当前共 394 个组件实例。
  52 份零件文件逐字节未变；几何复核通过。
  18 份现有装配体更新，6 份子装配体新增或外置，当前共 76 份 CAD 文件。
  已在 SOLIDWORKS 2025 SP5.0 重新打开最终 1103 总装配验证。
  导出插件识别到恰好 5 个第一层 workstation，无嵌套 workstation。
  所有外部模型引用均指向 1103，未增加 SOLIDWORKS 原生诊断错误。
  原有接口等自定义属性保留；仅调整工位标记及相关装配版本。

修改前 70 份原文件备份：$(Join-Path $run 'original-backup')
逐文件替换备份：$(Join-Path $run 'atomic-backup')
写入清单：$(Join-Path $run 'published.jsonl')
最终验证：$proof
"@
$report=Join-Path $run '工位整理结果.txt'
$text|Set-Content -LiteralPath $report -Encoding utf8
'VERIFIED_REPORT='+$report
