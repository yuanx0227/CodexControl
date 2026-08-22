# TLS certificates

Place production `fullchain.pem` and `privkey.pem` here through the deployment secret mechanism. PEM files are ignored by Git.

For local Docker verification only:

```powershell
& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo -NoProfile -File ".\generate-dev-cert.ps1"
```

The generated certificate is self-signed and must not be used in production.
