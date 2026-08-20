[CmdletBinding()]
param(
    [string] $TaskName = 'ImageGen-Backend',
    [string] $TaskPath = '\'
)

$ErrorActionPreference = 'Stop'

$task = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
if ($task.Actions.Count -ne 1) {
    throw "Expected scheduled task '$TaskPath$TaskName' to have exactly one action; found $($task.Actions.Count)."
}

$action = $task.Actions[0]
$arguments = $action.Arguments
$arguments = $arguments -replace '(?:^|\s)--preview-method(?:=|\s+)\S+', ''
$arguments = $arguments -replace '(?:^|\s)--preview-size(?:=|\s+)\S+', ''
$arguments = "$($arguments.Trim()) --preview-method auto --preview-size 512".Trim()

$updatedAction = New-ScheduledTaskAction `
    -Execute $action.Execute `
    -Argument $arguments `
    -WorkingDirectory $action.WorkingDirectory

# Updating the definition does not stop or restart an already-running task instance. The new arguments take effect
# only the next time Task Scheduler launches it.
$null = Set-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Action $updatedAction -ErrorAction Stop

$saved = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
[pscustomobject]@{
    Task = "$TaskPath$TaskName"
    State = $saved.State
    Execute = $saved.Actions[0].Execute
    Arguments = $saved.Actions[0].Arguments
    WorkingDirectory = $saved.Actions[0].WorkingDirectory
}
