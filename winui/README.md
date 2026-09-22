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

## Zones

The app drives one zone at a time, chosen under **Volume behaviour**. Only the zones the
receiver actually answered for are offered: a zone it does not have never replies, so asking
costs a command and no waiting at all.

| | Main | Zone 2, 3, 4 |
| --- | --- | --- |
| read, set | `MV?`, `MV455` (half steps) | `Z2?`, `Z245` (whole numbers only) |
| step | `MVUP` / `MVDOWN` | `Z2UP` / `Z2DOWN` |
| mute | `MU?`, `MUON` | `Z2MU?`, `Z2MUON` |
| ceiling | `MVMAX` | `SSVCTZ2S ?` answers `SSVCTZ2SLIM 080` |

The zone menu is `SSVCTZ2S`, not `SSVCTZ2`: the latter is answered with silence. `SSVCTZ2SLIM`
is the Limit from Setup > General > Zone2 Setup, read when the session opens and written when
the user picks one of its three values -- 60, 70 or 80, which is all that menu offers.

**Writing the Limit moves the zone's volume.** Setting it to 70 on a zone sitting at 23 left
the zone playing at 70, and setting it to 60 left it at 60; going from 70 to 80 has been seen to
leave one playing at 60. In another room that is a surprise worth avoiding, and it is not
reliable enough to predict where the level will land.

So the level is read at the moment the ceiling is written, and for the next few seconds anywhere
it goes it is put back to that reading, clamped to the new ceiling. Saying it again straight away
instead would lose the race: the receiver answers first and moves the level after. A receiver
that leaves the level alone is left alone in turn, and a level the user moves themselves ends the
watch.

### Power is per zone, and PW is not the main zone

`PW` is the **whole unit**: it answers `PWON` as soon as any zone is on, and `PWSTANDBY` switches
the entire receiver off. The main zone's own power is `ZM` -- `ZM?`, `ZMON`, `ZMOFF` -- which is
easy to get wrong and was: reading `PW` as the main zone lit its button whenever zone 2 was on,
and writing `PWSTANDBY` to it turned the receiver off instead of the room. Measured with zone 2
on and the main zone off, `ZM?` answers `ZMOFF` while `PW?` still answers `PWON`.

Zones are `Z2ON` / `Z2OFF` and so on. Every zone's power is followed, not just the one being
driven, because the window shows a button for each; the match on those lines is exact, since
`Z2MUON` and `Z2SLPOFF` are about other things entirely. `PWSTANDBY` is taken to mean every zone
went off with it, while `PWON` is taken to mean nothing at all.

When every zone is off, the volume keys do nothing at all: no command goes down the wire and no
display comes up, because there is no volume anywhere to change. They are still swallowed -- that
happens in the hook, before any of this -- so Windows does not quietly take its own volume back
while the receiver is asleep.

A zone that is off cannot be selected: it stays in the list, greyed, with its button above to
switch it on. Selecting one is never quietly undone -- being unable to pick something is easier
to understand than being moved off it after the fact.

Switching off the zone being driven is the one case that does move the app, because there is
nothing left to drive: it goes to the next zone that is on, or, when none of them is, back to
main with the zone, unit, step and maximum all greyed out. The power buttons stay live there:
they are the way back.

### At the end of the range, the receiver says nothing

A zone sitting at its Limit and told to go louder is answered with **silence**: measured at the
bottom of the range, `Z2DOWN` at 0 produces no `Z2` line at all. So a volume key on the AVR's own
remote, pressed against the ceiling, cannot put the on-screen display up -- nothing arrives to
put it up with. This looks like a fault in the overlay and is not one.

The step size, the maximum and the display unit are **kept per zone**. Someone running a second
room quietly, in whole steps, with a ceiling of 70 does not want any of that when they go back to
the main zone, and does not want to set it again when they come back.

The display unit is the exception that proves it: the receiver keeps **one** for the whole box,
which was worth measuring rather than assuming -- switching the main zone to `REL` makes zone 2's
own status report `Relative` too. So the preference is remembered per zone but only sent when the
zone being entered wants the volume written differently from how it is being written now. Moving
between two zones that agree sends nothing at all.

## The display unit belongs to the receiver

Denon and Marantz receivers have a volume-display setting of their own: `ABS` shows the 0-98
scale, `REL` shows decibels. The app does not keep a preference beside it, it follows it.

- On connecting, the app asks `SSVCTZMA ?` and takes whatever comes back. Asking for the one
  setting with `SSVCTZMADIS ?` is answered with silence; asking for the whole main-zone volume
  menu returns four lines, one of them `SSVCTZMADIS ABS`.
- Changing **Display unit** in the window sends `SSVCTZMADIS REL` or `ABS`, so the front panel
  changes with it.
- The receiver repeats that line to **every** open session, so a change made on its own menu or
  from its remote arrives here unasked and the window follows.

The echo of a change we made ourselves is recognised and dropped, so the two ends cannot chase
each other round.

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

### What the window's own buttons do

Because closing the window is how this app frees its memory, both window buttons are settings
rather than fixed behaviour:

| Setting | Default | The other choice |
| --- | --- | --- |
| `MinimiseToTray` | minimise to the taskbar | close the window; the tray icon brings it back |
| `CloseToTray` | close the window, volume control stays | quit both processes |

"Send to the notification area" always means *close this process*. Parking a minimised window off
screen would keep a hundred megabytes of XAML resident for nobody, which is the one thing the
split was built to avoid; reopening from the tray costs about half a second.

The pipe is opened `Asynchronous` at both ends, which is not decoration. Each side reads on one
thread and writes from another, and on a handle opened for synchronous I/O a blocked read stops the
write from starting at all: the two sides wait for each other for ever.

## The volume keys have a thread of their own

The low-level keyboard hook is called on the thread that installed it, only while that thread is
pumping messages, and Windows waits at most a second for the answer (`LowLevelHooksTimeout`,
capped at one second since Windows 10 1709). Past that the key goes to Windows, and the hook can
be removed without the application ever being told.

It used to share the main thread, which is also where the collection and working-set trim run
when the settings window goes away. On a slower machine that was enough: from the moment the
window was minimised to the notification area, Windows had the volume keys back and kept them,
with the process still running. Holding the main thread still for three seconds reproduces it on
any machine -- the keys go to Windows for as long as it is held.

So the hook now lives on a thread that does nothing else, and its callback does nothing but hand
the key to the main thread and swallow it; with the main thread held for eight seconds the keys
stay ours throughout. Because a removed hook cannot be detected, it is also put back every minute,
the new one installed before the old one is taken out so nothing slips between them.

## What it costs to leave running

Measured connected to a receiver, over 90-second stretches with nobody touching anything:

| | processor | working set | committed |
| --- | --- | --- | --- |
| background, audio keep-alive **off** | 0.017-0.035% of one core | 23 MB | 10 MB |
| background, audio keep-alive **on** | 0.052% of one core | 32 MB -> **15 MB** | 13 MB |
| settings window open | — | 132 MB, plus the background | — |

Against 32 MB for the WinForms version this replaces, and 80 MB for the same app before it was
split in two. The window's cost arrives when it is opened and leaves with it.

The silent stream is the dominant resting cost: about 9 MB and roughly double the processor time,
which is what opting into it buys.

Two things are worth saying plainly about the rest:

- The working set drops to 15 MB because the collection and trim runs a second time twenty
  seconds in, once the socket, the audio device and the tray icon have finished asking for what
  they need; the first trim, at startup, gives back memory that is taken straight out again.
  **The committed memory does not change** -- 13 MB either way. Trimming makes the pages leave
  RAM, not the program need less of them, and they come back on demand, which is why idle
  processor time rises slightly when it happens.
- Reducing how often the idle threads wake up -- the receiver's socket from four times a second
  to twelve times a minute, the pipe writer from four times a second to never -- is sound, but it
  did **not** show up in the measurements. At 0.05% of a core the noise floor is larger than the
  saving. It is in because it is right, not because it is visible.


## What is not WinUI

Two things WinUI cannot do, kept in Win32 and called through P/Invoke:

- **`Ui/VolumeFlyout.cs`** — the on-screen display. WinUI cannot draw a window with per-pixel
  transparency, so this is a layered window drawn with GDI+, holding the measurements taken off
  the real Windows 11 flyout. It asks for `HWND_TOPMOST` every time it comes up, not once when it
  is created: topmost is a band, not a rank, and inside it the window shown last is the one in
  front. Overlays belonging to games or to chat apps live in that same band, so a flyout that only
  claims its place at startup quietly ends up behind them for the rest of the session.
- **`Ui/TrayIcon.cs`** — the notification area icon, `Shell_NotifyIcon` driven from a
  message-only window. The app refuses to hide itself when the shell turns the icon down, so it
  cannot end up running with no way back to it.
