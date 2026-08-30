# Installer protected state root

## Scope

The C# Installer candidate stores its machine-authoritative transaction journal and certificate
ownership ledger below one fixed root:

```text
%ProgramData%\ClashSharp\Installer\v2
```

The path is derived from `CommonApplicationData`; no CLI, manifest, environment variable, or package
payload field may choose it. Only canonical local drive-qualified paths are accepted. Relative, UNC,
device, forward-slash, and dot-segment aliases fail closed.

`WindowsInstallerProtectedStateStores` composes `FileInstallerTransactionStore` and
`FileInstallerCertificateOwnershipStore` with the same root guard, so both stores share the same
ancestor handles and disposal boundary.

This composition is an elevated authority component, not a writable store for the `asInvoker` WPF
parent. The target user is intentionally read-only. Parent-side readiness/recovery inspection must use
a read-only view over the same verified root, while all create/replace/clear operations remain inside
the authenticated helper session. The current Core coordinator's direct `SaveAsync`/clear calls are a
known production-composition gap; wiring them to this store or granting the user write access would
violate the boundary rather than close it.

The code now expresses the first part of that split: `IInstallerTransactionReader` contains only
`LoadAsync`, `IInstallerTransactionStore` extends it with CAS write/clear, and
`WindowsInstallerProtectedStateStores.TransactionReader` exposes the least-authority parent view over
the same guarded lifetime. The coordinator still accepts the writer interface and must be migrated
before production composition; the new reader does not by itself make the runtime executable.

## ACL boundary

The shared `%ProgramData%\ClashSharp` product directory is a rename anchor, not an Installer-owned
exact-ACL object. Its owner must be SYSTEM, Builtin Administrators, or TrustedInstaller, and no
untrusted effective allow ACE may grant DELETE, DELETE_CHILD, WRITE_DAC, WRITE_OWNER, or GENERIC_ALL.
Windows 11 normally gives Users create-folder/append-data rights on `ProgramData`; those rights can
pre-position a name and cause a diagnosable denial of service, but cannot replace an existing protected
child. The guard therefore accepts explicit create-only anchor rights and never treats a pre-positioned
child as trusted merely because its name is correct.

`Installer` and `v2` are exact protected objects:

- owner: `S-1-5-32-544` (Builtin Administrators);
- inheritance disabled for the DACL;
- SYSTEM: inheritable FullControl;
- Builtin Administrators: inheritable FullControl;
- exact target-user SID: inheritable ReadAndExecute only;
- no fourth, inherited, callback, object-specific, deny, or unknown ACE.

Missing dedicated directories are created with that descriptor at creation time. Existing directories
are observed, not repaired. An owner or ACL mismatch stops the operation so an elevated helper cannot
“wash” an attacker-prepared object into the trusted boundary.

## Handle and reparse policy

Every existing component from the volume root through `v2` is opened with `CreateFileW` using
`FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT`. The observation must remain a directory
and must not carry `FILE_ATTRIBUTE_REPARSE_POINT`.

The handles share read and write access but deliberately do not share DELETE. Windows applies sharing
options until the handle closes, and delete access also governs rename. The guard retains all handles
until the two stores are disposed, preventing a checked directory component from being renamed or
deleted during journal access.

Owner and DACL bytes are read from the same open handle with `GetSecurityInfo`, bounded, copied, and
parsed as a `RawSecurityDescriptor`; its returned buffer is always released with `LocalFree`. Because
Microsoft documents that `GetSecurityInfo` itself does not eliminate concurrent security-descriptor
changes, initial acquisition performs a second observation of the complete locked chain. Every later
store operation re-observes the chain before touching a journal or ledger.

Primary API contracts:

- [CreateFileW sharing and directory flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [GetSecurityInfo handle and buffer ownership](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo)
- [Delete/rename and FILE_SHARE_DELETE](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-deletefilew)

## Verification matrix

The Windows-targeted test assembly defines deterministic cases for:

- missing-chain creation, full-chain pinning, repeat observation, and reverse disposal;
- exact owner/DACL generation and target-user read-only rights;
- pre-positioned exact-root ACL mismatch without automatic repair;
- Windows-compatible anchor create-only grants and untrusted DELETE_CHILD rejection;
- reparse anchor, wrong root, relative/UNC/device/dot-segment/forward-slash path rejection;
- ACL drift between calls and between the first and second initial observations;
- pre-cancellation with zero filesystem calls;
- a real temporary-directory handle proving rename remains blocked until lease disposal;
- transaction and certificate stores sharing one guard, root, and lifetime.

Linux can cross-compile these contracts but cannot execute Win32 ACL or sharing behavior. Required
Windows evidence remains: non-elevated target-user read, mutation denial, default Windows 11 ProgramData
ACL compatibility, reparse/adversarial races, power-loss behavior, and signed-helper cut points.

## Explicitly not closed here

This boundary does not authenticate the elevated helper, verify Authenticode, mutate SCM/Program
Files, or connect the WPF runtime. A deterministic pipe name is not authentication. The adjacent
machine-helper IPC checkpoint now defines a split-token/OTS-aware protected DACL, first-instance creation, and both
client/server PID query primitives, but they are not yet wired into a broker. The broker still must
bind those checks to verified executable identity, the helper process lifetime, the helper session,
and protected-store reload before this becomes the production authority. It must also own every
journal/ledger write and verified clear; the unelevated parent may only read and exact-compare.
