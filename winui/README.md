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

## Keeping the audio device awake

Windows powers an audio endpoint down between playback sessions, and waking it costs a second or
three of missing sound, sometimes with a pop. *Keep audio device alive*, off by default, holds a
silent WASAPI shared-mode stream on the endpoint so it never idles.

It stands aside rather than fighting: a player that takes the device in exclusive mode to
bit-stream TrueHD or DTS-HD disconnects our session, and `Ui/AudioKeepAlive.cs` lets go at once
and tries again ten seconds later. It follows the default endpoint, so plugging headphones in
moves it with them, and it stops when there is no active endpoint to hold.

All of it runs on its own MTA thread. WASAPI is COM, and the window's thread is not the place to
wait on it.

## Reconnecting

*Auto-reconnect* decides whether the app keeps trying on its own after the receiver fails to
answer. With it on, **attempts** is how many tries in a row may fail before it gives up and waits
for the Reconnect button, and **timeout** is how many seconds one try waits. Three tries of three
seconds is a reasonable default: enough for a receiver that is still booting, short enough that
the status line tells you the truth quickly. A connection that succeeds resets the count.

With it off the app tries once and then leaves it to you.

## Two processes

The app is two programs in one file.

Started with no argument it becomes the **background process**: a plain Win32 message loop holding
the volume keys, the link to the receiver, the on-screen display, the tray icon and the audio
keep-alive. WinUI is never loaded into it — `Program.Main` decides which half to be before any
XAML type is touched, and the window half lives in its own method so its assemblies load only in a
process that is going to show something.

Started with `--settings` it becomes the **window**, which owns nothing. Every value it shows
arrives from the background process over a named pipe, every change is a request back, and closing
it ends that process outright. There is no hiding, no anchor window, nothing left resident.

Starting the executable again while it is running does not make a second copy: it asks the one
already there to show its window, and exits.

The pipe is opened `Asynchronous` at both ends, which is not decoration. Each side reads on one
thread and writes from another, and on a handle opened for synchronous I/O a blocked read stops the
write from starting at all: the two sides wait for each other for ever.

## What it costs to leave running

| | working set |
| --- | --- |
| background alone, nothing open | **21 MB** |
| settings window open | 132 MB, plus the background |
| after the window is closed | **7 MB** |

Against 32 MB for the WinForms version this replaces, and 80 MB for the same app before it was
split in two. The window's cost arrives when it is opened and leaves with it.

Some of that is the collection and working-set trim the background process runs once it is up and
again whenever the window goes away, which is exactly when it goes back to doing almost nothing.


## What is not WinUI

Two things WinUI cannot do, kept in Win32 and called through P/Invoke:

- **`Ui/VolumeFlyout.cs`** — the on-screen display. WinUI cannot draw a window with per-pixel
  transparency, so this is a layered window drawn with GDI+, holding the measurements taken off
  the real Windows 11 flyout.
- **`Ui/TrayIcon.cs`** — the notification area icon, `Shell_NotifyIcon` driven from a
  message-only window. The app refuses to hide itself when the shell turns the icon down, so it
  cannot end up running with no way back to it.
