$devs = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'HID*' }
$devs | ForEach-Object {
    "{0,-8} {1,-10} {2}  [{3}]" -f $_.Status, $_.Class, $_.InstanceId, $_.FriendlyName
}
Write-Output "---- keyboard-class raw input candidates ----"
Get-PnpDevice -PresentOnly -Class Keyboard | ForEach-Object {
    "{0,-8} {1}  [{2}]" -f $_.Status, $_.InstanceId, $_.FriendlyName
}
