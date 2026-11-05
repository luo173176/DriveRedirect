$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$out = Join-Path $env:TEMP 'dr-uia-probe.txt'
"start" | Set-Content $out
$proc = Get-Process DriveRedirect.Desktop -ErrorAction Stop | Select-Object -First 1
"pid=$($proc.Id) hwnd=$($proc.MainWindowHandle)" | Add-Content $out
$root=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
"root=$($root.Current.Name) class=$($root.Current.ClassName)" | Add-Content $out
$walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
$child=$walker.GetFirstChild($root)
$count=0
while($child -and $count -lt 80){
  "child type=$($child.Current.ControlType.ProgrammaticName) name=$($child.Current.Name) class=$($child.Current.ClassName)" | Add-Content $out
  $child=$walker.GetNextSibling($child)
  $count++
}
"descendants:" | Add-Content $out
$all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
"desc count=$($all.Count)" | Add-Content $out
for($i=0;$i -lt [Math]::Min($all.Count,100);$i++){ $e=$all.Item($i); "desc $i type=$($e.Current.ControlType.ProgrammaticName) name=$($e.Current.Name) class=$($e.Current.ClassName)" | Add-Content $out }
