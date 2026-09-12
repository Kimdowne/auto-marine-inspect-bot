$secureKey = Read-Host 'Paste the new W&B API key' -AsSecureString
$keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $env:WANDB_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
}

if (-not $env:WANDB_API_KEY.StartsWith('wandb_v1_')) {
    Remove-Item Env:WANDB_API_KEY
    throw 'The key is not a wandb_v1_ API key.'
}

Write-Host "W&B API key loaded for this PowerShell session ($($env:WANDB_API_KEY.Length) characters)."
