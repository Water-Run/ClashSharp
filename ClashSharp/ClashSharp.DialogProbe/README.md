# Native dialog probe

This developer-only WinUI executable links the production dialog coordinator, native
host, layout policy, startup dialog, and its display models. It is not part of the
product or installer payload.

Build on the pinned Windows SDK/.NET environment:

```powershell
dotnet build ClashSharp/ClashSharp.DialogProbe/ClashSharp.DialogProbe.csproj `
    -c Release -p:Platform=x64 -o artifacts/dialog-native-probe
./ClashSharp/SandboxTest/Run-DialogProbe.ps1 `
    -ProbeDirectory (Join-Path $PWD 'artifacts/dialog-native-probe')
```

The runner creates the exact developer executable suspended on an unselected private
Windows desktop. The WinUI thread verifies its desktop before opening test windows and
reports the actual name. It never switches the user's desktop, installs the
product, or starts a Sandbox viewer. The probe checks that a startup dialog exceeds a 320×240 owner,
stays inside its monitor work area, keeps native modality, closes and reopens,
propagates cancellation and presentation changes, and leaves no owned window after
owner shutdown. It uses application APIs and never injects keyboard or mouse input.
Results, executable/assembly hashes, and owned-process cleanup receipts are stored under
`artifacts/validation/dialog-probes/`.
