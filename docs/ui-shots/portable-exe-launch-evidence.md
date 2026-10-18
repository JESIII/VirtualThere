# Portable role exes - launch evidence

Evidence for the claim: *`vht-server.exe` opens the VirtualThere Server window and
`vht-client.exe` opens the VirtualThere Client window, each with no console window,
when launched with no arguments from a copied portable folder.*

The portable set itself is the one produced by the packaging work
(`dist/portable/`: `vht-server.exe`, `vht-client.exe`, `vht.exe`, `portable.marker`,
`PACKAGING.md`, `Setup-Portable.ps1`, `scripts/`, `driver/`).

## How the run was made

1. Copied the portable set to a clean folder, so nothing was launched out of the
   build tree and the config lands next to the exes:

   ```
   cp -a dist/portable/. <scratch>/vht-shots/portable/
   ```

2. Launched each role exe with **no arguments**, working directory = that folder
   (exactly what double-clicking the exe in Explorer does):

   ```
   "C:\...\vht-shots\portable\vht-server.exe"
   "C:\...\vht-shots\portable\vht-client.exe"
   ```

3. For each running process, measured the window state (title, class, visibility,
   top-level window classes of the process), the PE subsystem of the exe, and the
   process count with that image name, then captured the window.

Two kinds of capture are kept, because they answer two different questions:

| file | how it was taken | what it shows |
| --- | --- | --- |
| `vht-server-exe-window.png` | `PrintWindow(hwnd, memDC, PW_RENDERFULLCONTENT)` - a render of that one window at 1:1 | the real window of the real process: title bar, heading, toolbar, status pill |
| `vht-client-exe-window.png` | same | same |
| `vht-server-exe-desktop.png` | `BitBlt` of the 800x600 secondary monitor, window parked there | a live desktop capture: the window on screen next to the taskbar, no console window anywhere |
| `vht-client-exe-desktop.png` | same | same |

The window renders are used for the window image because the machine's primary
monitor is covered by an unrelated full-screen application that steals the
foreground back within a second; `PrintWindow` gives the window's own pixels
regardless of z-order. The desktop grabs are the "on screen" counterpart.

## Measurements

Server (`vht-server.exe`, no arguments):

```
window_title           VirtualThere Server
window class           HwndWrapper[vht-server;;...]        (WPF)
visible top-level      1 window with a title, the "VirtualThere Server" window
console window         none - no ConsoleWindowClass window exists for the process
PE subsystem           2 (WINDOWS_GUI) - the OS cannot give it a console
processes (running)    1  (only the window; pressing Start is what spawns the worker)
processes (after exit) 0
```

Client (`vht-client.exe`, no arguments):

```
window_title           VirtualThere Client
window class           HwndWrapper[vht-client;;...]        (WPF)
visible top-level      1 window with a title, the "VirtualThere Client" window
console window         none - no ConsoleWindowClass window exists for the process
PE subsystem           2 (WINDOWS_GUI)
processes (running)    1  (only the window)
processes (after exit) 0
```

Both windows came up in the state that matches the design: badge `Not running`,
`Start`/`Stop`/`Refresh`/`Minimize to tray`/`Log`/`About`/`Exit` on the server,
`Attach selected`/`Detach selected`/`Add server...`/`Refresh`/`Minimize to tray`/
`Log`/`About`/`Exit` plus the Servers list and per-device Actions column on the
client, and the message line reporting that the control pipe has no answer yet.

## Note on the Windows Firewall prompt

The first launches raised the standard Windows Firewall "allow access" prompt for
`vht-server` (publisher `VirtualThere`), which Windows shows once per executable
path. It was declined (closed) rather than allowed, so nothing was added to the
firewall configuration; the captures above were taken with no prompt on screen.

## Related evidence

- Window-lifecycle / worker behaviour (one UAC prompt on Start, hidden worker,
  detach semantics): `docs/UI.md` §1 and
  `tests/Vht.Ui.Tests/WorkerProcessIntegrationTests.cs`.
- Packaging contents and the reproducibility of the portable set:
  `installer/PACKAGING.md`.
