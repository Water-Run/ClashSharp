# Installer machine-helper IPC boundary

## Scope and ordering

One normal Install, Repair, or Uninstall transaction must cross UAC at most once. The unelevated
installer owns one persistent byte-mode named-pipe server and sends the existing bounded,
journal-bearing command/result frames to one elevated helper process.

The required startup order is security-significant:

1. derive the pipe name only from the random 256-bit transaction identity;
2. create the first and only pipe instance with its final DACL;
3. verify the fixed helper path and Authenticode identity;
4. launch that exact helper through `ShellExecute`/`runas` and retain its process handle;
5. accept one client, then bind the connected server handle to the retained helper PID;
6. let the helper bind its connected client handle to the expected parent PID and verified image;
7. exchange only strict 4 KiB command/result frames for the same transaction;
8. retain the release and protected-root handles until a terminal result or an independently proven
   helper termination followed by protected-store reconciliation.

A pipe name, ACL, PID, signer, or command hash is not sufficient alone. The production broker must
enforce the complete chain.

## Protected-state authority invariant

The WPF parent is `asInvoker`. The exact `%ProgramData%\ClashSharp\Installer\v2` DACL deliberately
grants the target user only `ReadAndExecute`; it must not be weakened merely to make composition
convenient. Consequently, the parent can inspect protected recovery state but cannot create,
transition, replace, or clear the machine-authoritative journal or certificate ledger.

The Core coordinator now accepts only `IInstallerTransactionReader`. It constructs `Prepared` in
memory, exact-validates each helper response, reloads the protected state through that read-only view,
and never calls the protected store's `SaveAsync` or `ClearVerifiedAsync`. The elevated-side
`InstallerMachineHelperAuthoritySession` owns first-`Prepared`, every CAS phase transition, replay
verification, and verified clear. The authenticated persistent Windows broker and helper host now
carry that session in deterministic tests; production composition remains disabled until concrete
machine operations and the WPF startup branch are wired and validated together.

The broker/helper closure must enforce this ownership split:

1. the parent constructs canonical `Prepared` bytes in memory but does not claim they are durable;
2. the first authenticated `Prepare` command makes the helper persist `Prepared` before any machine
   or current-user mutation, then commits only the allowed reservation/authorization successor;
3. every later journal and certificate-ledger transition is written exactly once by the helper;
4. after each response, the parent performs a read-only protected-store reload and exact-compares the
   authoritative bytes/hash instead of writing a mirror into the same root;
5. verified-state clear is a separate helper-authoritative terminal operation followed by a parent
   read proving absence;
6. UAC cancellation before the helper persists `Prepared` leaves no durable transaction and no side
   effect; any interruption after that cut point must reconcile from the protected store.

Over-the-shoulder elevation makes this split mandatory for the certificate ledger too: the helper may
run as a different administrator while the certificate itself belongs to the target user's
`CurrentUser/TrustedPeople` store. The existing managed adapter resolves `StoreLocation.CurrentUser`
from the process token and also requires that token SID to equal `request.TargetSid`; inside an OTS
helper it therefore fails closed with target-user mismatch. It does not silently mutate the
administrator's store, but it also cannot provide the required helper-side target-user operation.

Microsoft documents that `CERT_SYSTEM_STORE_USERS` can open a user's system store by prefixing the
store name with the textual SID. `WindowsTargetUserCertificateStoreAdapter` now implements the
bounded native CryptoAPI route and opens only `<exact-target-SID>\TrustedPeople` with `CertOpenStore`,
while preserving the thumbprint + full DER SHA-256 identity policy. Injected read/add/exact-remove,
conflict, cancellation, and ack-loss tests pass. Windows 11 x64 with a loaded standard-user profile
and alternate-administrator OTS credentials remains the required E3/E4 proof before enablement.

Named-pipe impersonation is not a shortcut in the current topology: Microsoft documents that only the
server end can impersonate the client. Here the unelevated parent owns the server and the elevated
helper is the client, so `ImpersonateNamedPipeClient` would let the parent impersonate the helper, not
the helper impersonate the target user. If target-SID TrustedPeople access cannot be proven, the
fallback design must keep certificate mutation in the parent and bracket it with helper-authoritative
ledger cut points; it must not silently write the administrator's CurrentUser store or change the
protected root to user-writable storage.

Primary certificate/impersonation contracts:

- [Windows certificate system store locations and `CERT_SYSTEM_STORE_USERS`](https://learn.microsoft.com/en-us/windows/win32/seccrypto/system-store-locations)
- [`CertOpenStore`](https://learn.microsoft.com/en-us/windows/win32/api/wincrypt/nf-wincrypt-certopenstore)
- [Per-user TrustedPeople physical-store restriction](https://learn.microsoft.com/en-us/windows/win32/seccrypto/extending-certopenstore-functionality)
- [`ImpersonateNamedPipeClient` direction and failure rule](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient)

Package verification has the analogous but already documented WinRT route. `PackageManager` accepts
`FindPackagesForUser(exactSid, exactFamilyName)` and requires administrative privileges when the SID
is not the caller. The Windows facade now takes the user SID explicitly: the parent current-user
adapter still passes `string.Empty`, while the implemented helper-only `CommitPackage` inspector
passes the journal's canonical `TargetSid` and applies the same one-registration/full-identity/health
checks. It never substitutes the OTS administrator's empty/current-user query. Real alternate-user
AppXSVC execution remains an E3/E4 requirement.

- [`PackageManager.FindPackagesForUser`](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser?view=winrt-26100)

## Pipe object policy implemented in this checkpoint

`WindowsMachineHelperPipeSecurity` creates a `NamedPipeServerStream` with:

- exactly one server instance;
- `PipeOptions.FirstPipeInstance` so a pre-existing name causes a fail-closed creation error;
- asynchronous byte mode and non-inheritable handles;
- a protected explicit DACL;
- Network denied FullControl;
- the exact `S-1-5-5-X-Y` logon SID allowed ReadWrite;
- Builtin Administrators allowed ReadWrite so a standard user can complete over-the-shoulder
  elevation with a different administrator account;
- LocalSystem allowed FullControl;
- no Everyone, Anonymous, Authenticated Users, Interactive, or target-user allow rule.

The logon SID is narrower than an account SID and is shared across the UAC-linked tokens for the same
interactive logon. It is insufficient by itself for over-the-shoulder elevation: Microsoft documents
that a standard user may enter another administrator account's credentials, so the helper can run
under that administrator rather than the parent's identity. The Administrators ACE is therefore a
connectivity gate, not authentication; the retained helper PID must still match the connected client
handle, and the helper must still match the expected parent PID. `PipeOptions.CurrentUserOnly` is
deliberately not used: .NET documents that it ignores the supplied `PipeSecurity`, which would discard
this explicit cross-integrity policy.

Microsoft also documents that named-pipe generic write includes `FILE_CREATE_PIPE_INSTANCE` because
that bit aliases `FILE_APPEND_DATA`. Consequently, an allow ACE with `ReadWrite` cannot by itself
prevent an allowed token from attempting to create a same-name server. The parent therefore creates
the server before `runas`, requires `FirstPipeInstance`, limits the name to one instance, derives it
from a random transaction identity, and treats any pre-existing object as a terminal safety failure.

The default Windows pipe descriptor is also not accepted. Microsoft documents that it grants read
access to Everyone and Anonymous in addition to broader full-control entries.

## Connected-peer identity implemented in this checkpoint

`WindowsMachineHelperPipeIdentity` queries the process attached to the exact connected handle:

- the parent uses `GetNamedPipeClientProcessId` and must match the retained `runas` process PID;
- the helper uses `GetNamedPipeServerProcessId` and must match the expected parent PID;
- zero, mismatch, invalid/closed handle, and native query failure all fail closed with stable sanitized
  installer diagnostics;
- fatal runtime failures are not converted into ordinary protocol failures.

The native methods are loaded only from System32. Checking happens after connection; access to the
predictable local namespace is not treated as authentication.

`InstallerMachineHelperBootstrap` keeps this process identity outside the durable command schema. It
wraps the existing first command with one positive canonical Int32 parent PID and accepts exactly eight
path-free arguments. The pre-WPF startup router and `runas` launcher now consume that wrapper, while
all subsequent pipe commands retain the existing journal-only identity and strict codec.

Primary API contracts:

- [NamedPipeServerStreamAcl.Create and CurrentUserOnly behavior](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0)
- [Named pipe security and access rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [ShellExecute `runas` consent or administrator-credential behavior](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutea)
- [COM elevation over-the-shoulder security model](https://learn.microsoft.com/en-us/windows/win32/com/the-com-elevation-moniker#over-the-shoulder-ots-elevation)
- [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
- [GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)

## Test boundary

The Windows-targeted source adds deterministic checks for:

- the exact four-rule protected DACL, OTS Administrators access without broad interactive access, and
  rejection of account/Admin/SYSTEM SIDs as the required logon-SID input;
- strict eight-argument bootstrap round-trip, canonical positive PID, reserved-option, reorder,
  missing, trailing, null, and path-free cases;
- one canonical logon SID on the current token;
- first-instance squatting rejection;
- real same-process client/server connections exercising both native PID APIs;
- deterministic client/server match, mismatch, invalid handle/PID, query-error sanitization, and fatal
  exception propagation through an injected native seam;
- persistent multi-command broker reuse, UAC cancel, connect/command timeout, response loss,
  termination observation, and pinned-resource lifetime;
- helper-side parent final-path/Authenticode verification, elevation check, authority creation, strict
  command loop, and fault propagation;
- target-SID certificate/package inspection and helper operation dispatch while an independent locked
  release lease remains live.

These tests must run on Windows 11 x64. Linux may cross-compile them when the standard resource gate
passes, but must not execute them or claim Win32 runtime evidence.

## Still open

The pipe, trust, broker, host, authority session, target-SID certificate adapter, and package inspector
are implemented. Before production enablement, the following remain mandatory:

- compose protected roots, profile resolution, locked payload archive/swap, SCM, association, and root
  cleanup into concrete `IWindowsMachineHelperMachineOperations` with exact replay semantics;
- prove Repair reassociation without overwriting the only evidence needed to stop the old owned SCM
  tuple, and prove Uninstall deletes association last under its durable authorization tombstone;
- wire the same-EXE helper startup branch and production parent runtime without allowing ordinary UI
  startup to acquire elevated capabilities;
- run standard-user OTS certificate/package scenarios and real protected-root/SCM mutations in an
  isolated Windows 11 x64 VM;
- run signed Windows VM tests covering pipe squatting, wrong peer, UAC cancel, parent/helper crash, PID
  lifecycle, split-token consent, another logon session, reboot, and every durable mutation cut point.

Until those items close, `MigrationPreviewInstallerRuntime` must continue returning `CanExecute=false`.
