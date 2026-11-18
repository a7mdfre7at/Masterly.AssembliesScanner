# Security policy

## Supported versions

| Version | Supported |
| --- | --- |
| 2.x | Yes |
| 1.x | No. Upgrade to 2.x; the `Assemblies.Get` API from 1.x still works there unchanged. |

Fixes are released as a new 2.x version on [nuget.org](https://www.nuget.org/packages/Masterly.AssembliesScanner).

## Reporting a vulnerability

Please do not open a public issue for a security problem.

Report it privately instead: on the repository's **Security** tab, choose **Report a vulnerability**
([direct link](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/security/advisories/new)).
Only the maintainer can see the report.

Include what you can of:

- the package version and target framework (`net8.0` or `net10.0`),
- what an attacker could do, and under what conditions,
- the steps or a small project that reproduces it.

You will get a reply within a week. Once a fix is released, the advisory is published with credit to you,
unless you would rather stay anonymous.

## Scope

This library reads assembly metadata and, when asked, loads assemblies and creates types from them.
Loading an assembly runs its code, so `Load`, `LoadIsolated` and `LoadPlugins` are only as safe as the files
you point them at. Load assemblies only from directories you trust. A report that loading an untrusted
assembly runs its code is expected behaviour, not a vulnerability.

In scope, for example:

- scanning or inspection (which never loads) executing code from the scanned file,
- a crafted file that makes scanning or `CheckCompatibility` hang, crash the process, or read outside the file,
- an assembly loaded from a path other than the one scanned or configured.
