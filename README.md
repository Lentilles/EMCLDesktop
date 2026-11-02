# EMCL Desktop

A desktop application for working with DLMS/COSEM meters, built with Avalonia UI and the EMCL library. Read meter objects and profiles, browse a saved object catalog offline, inspect packets, and export communication traces.

The desktop project targets Windows, Linux, and macOS. Android, iOS, and Browser projects are included in the solution as part of the original template; the instructions below apply to `EMCLDesktop.Desktop`.

The application UI is currently in Russian. Control names in this guide are English translations of the corresponding UI labels.

On macOS, the **EMCL Desktop** application menu includes About and Connection settings (**⌘,**), alongside the standard system commands. The **File** menu provides Load (**⌘O**) and Save (**⌘S**), using the same configuration dialogs as the Objects tab.

## Features

- TCP and serial connections with Wrapper / HDLC framing.
- Association LN versions 1 and 3, with `None`, `Low`, and `High` authentication.
- Object catalog searchable by OBIS, class name, and class ID, including regular expressions.
- Read individual attributes, all attributes of an object, and Profile Generic tables.
- Save connection settings and a catalog snapshot to JSON; browse the snapshot without a meter.
- Packet inspector for the current connection and text HEX logs, with linked field and byte highlighting.
- TCP/Serial traces in text and HEX formats, with clipboard copying and `.txt` export.

## Running from source

.NET SDK 10 is required. Place the `EMCLDesktop` and `EMCL` repositories next to each other: the application references `../EMCL/src/EMCL/EMCL.csproj` directly rather than using a prebuilt NuGet package.

```text
Repositories/
  EMCLDesktop/
  EMCL/
    src/EMCL/EMCL.csproj
```

Run these commands from the `EMCLDesktop` repository root:

```sh
dotnet restore EMCLDesktop.Desktop/EMCLDesktop.Desktop.csproj
dotnet run --project EMCLDesktop.Desktop/EMCLDesktop.Desktop.csproj
```

Build the Desktop project specifically. Building the entire solution also includes the mobile and browser projects and may require additional workloads.

## Release builds

Example of a self-contained build for macOS on Apple Silicon, which does not require .NET to be installed on the user's machine:

```sh
dotnet publish EMCLDesktop.Desktop/EMCLDesktop.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/osx-arm64
```

For another platform, replace the runtime identifier after `-r` and the output directory after `-o`:

| Platform | Runtime identifier |
| --- | --- |
| macOS, Apple Silicon | `osx-arm64` |
| macOS, Intel | `osx-x64` |
| Windows, x64 | `win-x64` |
| Windows, ARM64 | `win-arm64` |
| Linux, x64 | `linux-x64` |
| Linux, ARM64 | `linux-arm64` |

The command creates a publish directory containing the executable and its dependencies. Distribute the entire directory, or use a ready-made package from GitHub Releases. Self-contained builds include .NET, but system graphics libraries and serial adapter drivers must be installed separately if needed.

### CI/CD and GitHub Releases

The [Build and release](.github/workflows/release.yml) workflow runs on pushes, pull requests, and manual dispatches through Actions. For each of the six targets, it runs console checks, publishes a self-contained Release build, and packages it on a runner with the matching architecture. Regular builds are available as Actions artifacts for 14 days.

| Platform | Release package |
| --- | --- |
| Windows x64 / ARM64 | `EMCLDesktop-<version>-win-x64.zip` / `win-arm64.zip` |
| Linux x64 / ARM64 | `EMCLDesktop-<version>-linux-x64.tar.gz` / `linux-arm64.tar.gz` |
| macOS Intel / Apple Silicon | `EMCLDesktop-<version>-osx-x64.dmg` / `osx-arm64.dmg` |

To publish a release, commit your changes and push a version tag to GitHub:

```sh
git tag v1.2.3
git push origin v1.2.3
```

Once all six builds succeed, the workflow creates a GitHub Release with the packages, automatically generated release notes, and a `SHA256SUMS` file. Tags with a suffix, such as `v1.2.3-rc.1`, create a prerelease. The version number is included in the build and package name. Rerunning the workflow does not replace assets of an already published release; an unfinished draft can be completed and published.

Each package includes the README, the application's MIT license, the EMCL license, and a `build-info.json` file containing the version, architecture, and commit IDs of both repositories. On macOS, these files are located in `EMCL Desktop.app/Contents/Resources`.

CI checks out the library from `Lentilles/EMCL` into a sibling directory. Its commit is pinned in the workflow's `EMCL_REF`; update that SHA when upgrading the dependency. No additional secrets are needed if EMCL is public. If EMCL is private, add an `EMCL_READ_TOKEN` secret under **Settings → Secrets and variables → Actions**, with permission to read the EMCL repository contents. The application's default `GITHUB_TOKEN` does not grant access to another private repository, and pull requests from forks do not receive this secret either.

Publishing uses the built-in `GITHUB_TOKEN`, with `contents: write` permission granted only to the release job. No separate token is needed for GitHub Releases. GitHub Actions and the referenced `actions/*` must be allowed in the repository settings.

After running `dotnet publish`, you can package a build locally using Python 3.9+:

```sh
python3 .github/scripts/package-release.py --rid osx-arm64 --version 1.2.3 --publish artifacts/osx-arm64 --output artifacts/release --emcl ../EMCL
```

DMG packaging must run on macOS. The script creates an `.app` bundle, applies and verifies a local ad-hoc signature, then creates and verifies a compressed DMG. The DMG contains the application and an `Applications` shortcut for drag-and-drop installation. Paid Developer ID signing and notarization are not configured; an ad-hoc signature does not replace them.

### macOS distribution

Paid Apple Developer Program membership is not required to build the application or share test builds. When an application without Developer ID signing and notarization is downloaded, macOS may block its first launch. The user can allow it through **System Settings → Privacy & Security → Open Anyway**. See [Apple's instructions](https://support.apple.com/102445).

Distribution outside the Mac App Store that passes the default Gatekeeper checks requires a **Developer ID Application** signature and **Apple notarization**. Obtaining the certificate requires Apple Developer Program membership. Publishing through the Mac App Store also requires membership and App Review. See [Developer ID](https://developer.apple.com/developer-id/), [notarization](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution), and [distribution options](https://help.apple.com/xcode/mac/current/en.lproj/devac02c5ab8.html).

## First connection

1. Open the **Objects** tab and click **Connection settings**.
2. Select the transport, framing, addresses, and authentication required by your meter. Close the dialog with **Done** or Escape.
3. Click **Connect** to the right of the tabs. This establishes an association without reading `object_list`.
4. Click **Read tree** to load the object catalog through the active connection.
5. Select an object and click **Read** next to an attribute, or **Read all attributes**.

Selecting an object does not read its values. A saved catalog remains visible after connecting until you explicitly read the tree, and also remains available after disconnecting or a failed reconnection. Connecting does not switch the active tab. Connection status appears next to the connection button; meter operation messages and errors appear in the Objects tab. A separate button lets you copy an error message.

## Connection settings

| Group | Settings |
| --- | --- |
| TCP | IP address or hostname, TCP port |
| Serial | Refreshable port list, BaudRate, DataBits, Parity, StopBits, Handshake, DTR, and RTS |
| Framing | Wrapper or HDLC; HDLC also supports a physical server address and extended address format |
| Association | Client and server addresses, Association LN version, association OBIS, Conformance, maximum PDU size |
| Authentication | `None`, `Low`, `High`; password as UTF-8, HEX, or an environment variable name |
| Communication | Connection timeout, No traffic interval, KeepAlive, handling of identical `object_list` entries |

`High` authentication requires a 16-byte secret. All supported Conformance flags are enabled by default and can be toggled individually; loading a JSON configuration restores them from the file. Reading the association requires `Get`; `High` authentication also requires `Action`.

Serial uses direct binary communication at the configured baud rate, without an initial IEC 62056-21 exchange or baud rate switching. Hardware handshaking controls RTS automatically. TCP and Serial settings remain in their fields when switching transports; JSON stores the settings for the selected transport.

The **No traffic** setting specifies the maximum idle interval without bytes being read or written. Each received fragment and each send resets the timer, so a complete request may take longer than this interval.

By default, **KeepAlive** reads the Association LN object's `logical_name` with a short GET request after 20 seconds of inactivity. The interval can be configured from 1 to 3600 seconds. No extra requests are sent while reading attributes or profiles. If the session is lost, the application displays an error, closes the connection, and allows you to reconnect.

## Objects and Profile Generic

The catalog is grouped by COSEM class and version. Search covers OBIS, class name, and class ID; the **Regular expression** checkbox enables pattern matching. Object details show attributes, methods, types, and access rights. Read buttons remain available regardless of the preliminary access assessment: an actual meter rejection is displayed next to the attribute. For unknown classes, the attribute type is determined from a successful response. The duplicate-handling checkbox allows fully identical entries in `object_list` to be ignored.

Profile Generic supports reading the entire buffer, the first or last N entries, an entry-number interval, or a date range. To read the last N entries, the application first reads `entries_in_use`. Date ranges use the time column from `capture_objects` and the meter's time zone. If `capture_objects` has not been retrieved yet, the application reads it automatically.

Ranges are sent to the meter using DLMS selective access, which requires the negotiated `SelectiveAccess` Conformance flag. The table displays 100 rows per page and labels columns with the source OBIS and captured attribute name. Both regular arrays and compact arrays are supported.

## Saving and loading

**Save** writes connection settings and a catalog snapshot to JSON, including OBIS, classes, attributes, methods, access rights, and text values already read. **Load** opens the snapshot without connecting to a meter. The legacy `EMCLDirector` format identifier, both `EMCL.Examples` formats, and older TCP configurations are supported.

With UTF-8 or HEX passwords, the password is stored in JSON without encryption. `Environment` mode stores the environment variable name instead of the secret's value.

## Packet inspector

The **Packet inspector** tab has two independent sources:

- **Analyze current connection** passively parses captured TCP/Serial bytes from the most recent connection. Frames are reconstructed independently of transport read boundaries. Results remain available after disconnecting; a new connection starts a separate analysis. Decoding runs in the background while the tab is open, and capture continues on every tab.
- **Analyze text** parses a pasted log when you click **Parse**. Supported input includes spaced HEX, contiguous HEX, `0xNN`, TX/RX, SEND/RECV, and direction arrows. Timestamps, sizes, and offsets from the application's own exports are excluded from the payload. Unrelated lines are skipped and counted in the results. The input field collapses after successful parsing.

Text, results, and packet/field selections are preserved when switching sources. Packets and the HDLC / Wrapper / LLC / ACSE / DLMS / A-XDR field tree appear on the left; HEX and links to related packets appear on the right. The original packet text is available at the bottom.

Hovering over a field highlights its bytes; hovering over a byte highlights the corresponding field and value. Clicking pins the selection. Offsets are zero-based, and HEX is displayed in pages of 256 bytes. Hovering over a field on another page navigates to its bytes; the **Go to field bytes** button is also available. Request context, OBIS, attribute or method, values, scaler/unit, and diagnostics appear in the structure tree. Metadata from another packet is not assigned byte ranges in the current response.

The inspector parses normal GET/SET/ACTION, GET next/block, HDLC segmentation, notifications, AARQ/AARE and other ACSE envelopes, nested arrays, structures, and compact arrays. Reassembled APDUs and values appear as separate entries with links to the original frames and blocks. Their HEX contains the original packets in sequence, and fields can span multiple disjoint ranges that exclude transport headers and CRCs. Out-of-order segments or blocks prevent a value from being marked as successfully reassembled.

A response's OBIS is determined from the request with the same Invoke ID within the same connection and address pair. No context is inferred if the request is missing, the Invoke ID is ambiguous, or the frame is damaged. Numeric values remain unscaled until the corresponding register's scaler/unit is found in an earlier exchange. Protected APDUs and unsupported variants are displayed as raw bytes with diagnostics.

Limits per analysis: `16 × 1024 × 1024` text characters, 50,000 entries, 1 MiB per reassembled message, and 4096 segments/blocks. Value field trees are limited to 10,000 nodes and a depth of 64.

## Tracing and export

The **Tracing and export** tab shows communication history since application startup. Capture begins before HDLC/association setup, continues regardless of the active tab or display format, and is preserved across reconnections. You can copy the trace or use **Save trace** to export it to `.txt`.

The selected format applies to the entire recorded history, clipboard copying, and saving:

| Format | Contents |
| --- | --- |
| Text (default) | Connection settings without the password, traffic volume, HDLC frames, UA parameters, APDU types, and AARE diagnostics; no HEX dumps |
| Text + HEX | The same descriptions plus raw TCP/Serial bytes with timestamps, direction, fragment length, and line offsets |
| HEX only TX/RX | Only `TX: 7E …` / `RX: 7E …` lines, without timestamps, sizes, offsets, or text messages; long fragments are split into 64-byte lines |

A send attempt is recorded before writing to the transport; successful completion is recorded separately. If a write fails, the prefix actually sent is unknown. Transport read boundaries may differ from packet boundaries.

Both HEX modes show the raw exchange without masking, including transmitted passwords and keys. The log remains in memory without truncation until the application closes, so memory usage grows with traffic volume.

## Limitations

- Writing attributes and invoking methods are not yet implemented in the UI. SET/ACTION parsing in the inspector does not mean the application can send these requests.
- Association LN v3 access protection bits are displayed, but protected GET requests are not performed: the current EMCL client does not support encrypted or signed communication.
- Large collections are shortened for display; decoding limits are controlled by EMCL.
- The inspector does not decrypt protected APDUs; it shows raw bytes and diagnostics.

## Checks

The repository includes a console check suite for tracing, transport idle timeouts, configurations, view models, profiles, and the packet inspector:

```sh
dotnet run --project Tests/TraceChecks/TraceChecks.csproj
```

The checks use test data and fake transports; no physical meter is required.

## Project structure

| Directory | Purpose |
| --- | --- |
| `EMCLDesktop` | Shared Avalonia views, view models, and services for connections, configuration, and packet analysis |
| `EMCLDesktop.Desktop` | Entry point for Windows, Linux, and macOS |
| `Tests/TraceChecks` | Console checks |
| `EMCLDesktop.Android`, `EMCLDesktop.iOS`, `EMCLDesktop.Browser` | Platform projects from the original template |

## License

EMCL Desktop source code is distributed under the [MIT license](LICENSE). The EMCL library and other dependencies are distributed under their own licenses.
