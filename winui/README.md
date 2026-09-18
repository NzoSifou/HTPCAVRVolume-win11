# HTPCAVRVolume on WinUI 3

The same app as the one at the root of this repository, with its window rebuilt on WinUI 3 and
the Fluent controls the design was drawn from. Everything below the window — the persistent link
to the AVR, the coalescing of wheel ticks, the on-screen display — is the same code.

## Building

```
dotnet publish -c Release
```

That is all: the project already carries the properties that make the output a single `.exe`.

It has to, in fact. `PublishSingleFile` only works for a WinUI 3 app that is **unpackaged** and
**self-contained** at the same time, which is why the project sets all of

```
WindowsPackageType=None    WindowsAppSDKSelfContained=true    SelfContained=true
EnableMsixTooling=true     IncludeAllContentForSelfExtract=true
```

Take any one of them away and the build either refuses or produces a folder instead of a file.

## What comes out

One `HTPCAVRVolume.exe` of about 204 MB. No installer, no runtime to install first: put it
wherever you like and run it. Its settings file is written next to it, not in the temp folder it
runs from.

The first launch takes a few seconds because the single file unpacks itself into
`%TEMP%\.net\HTPCAVRVolume\`; every launch after that is immediate. It is therefore portable in
the sense of *one file to carry*, not in the sense of *leaves no trace*.

## Do not rename the executable

An unpackaged WinUI app finds its XAML metadata and its resources through the name of the file it
was built as. Rename `HTPCAVRVolume.exe` to anything else and the XAML runtime fails inside
itself: the process dies with a stowed exception (`0xC000027B`) and no message.

`Program.cs` replaces the entry point XAML would generate purely to catch that case and say so,
before any of this can happen. The folder the file sits in does not matter — only its name.

## What is not WinUI

Two things WinUI cannot do, kept in Win32 and called through P/Invoke:

- **`Ui/VolumeFlyout.cs`** — the on-screen display. WinUI cannot draw a window with per-pixel
  transparency, so this is a layered window drawn with GDI+, holding the measurements taken off
  the real Windows 11 flyout.
- **`Ui/TrayIcon.cs`** — the notification area icon, `Shell_NotifyIcon` driven from a
  message-only window. The app refuses to hide itself when the shell turns the icon down, so it
  cannot end up running with no way back to it.
