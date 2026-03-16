$uri = "http://localhost:5283/api/ttn/uplink"
$deviceId = "paxcounter-ljubljana-02"
$devEui = "1147DC7E94C69E36"

while ($true) {
    $wifi = Get-Random -Minimum 0 -Maximum 5
    $ble = Get-Random -Minimum 0 -Maximum 25
    $body = @"
{"data":{"end_device_ids":{"dev_eui":"$devEui","device_id":"$deviceId"},"received_at":"$(Get-Date).ToUniversalTime().ToString("o")","uplink_message":{"decoded_payload":{"wifi":$wifi,"ble":$ble}}}}
"@

    try {
        Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json" -Body $body | Out-Null
        Write-Host "$(Get-Date -Format HH:mm:ss) sent wifi=$wifi ble=$ble"
    } catch {
        Write-Host "ERROR: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 10
}
