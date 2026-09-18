# HTPCAVRVolume functional with Windows 11 24H2 and above

This is a simple program that captures your HTPC's volume control buttons and instead sends the commands directly to your
home theater AVR.

The main benefit of this is that most people when watching video on their HTPC use audio bit-streaming for things like
Dolby Digital and DTS audio formats.  When you are bit-streaming, the volume control on your HTPC does not work for the
bit-streamed audio.  This means you can't use the convienent volume control buttons on your wireless keyboard or
handheld remote control to control your playback volume.

##### Supported Controls

The only supported controls are:

* Volume Up
* Volume Down
* Mute/Unmute

### Volume wheel support

A volume wheel sends a burst of key presses, and the original code opened a fresh TCP connection
per press, from inside the keyboard hook. The AVR was left grinding through a backlog long after
the wheel had stopped, and the whole system's input queue waited on every connect.

The connection to the AVR is now opened once and kept open, and the presses that pile up during a
flick are turned into a single absolute level command that lands where the wheel stopped. Forty
ticks reach a Denon as three or four commands instead of forty.

Two settings control the feel, both in the window:

* **Step** -- how much one press, one detent of a wheel or one notch of the slider moves the
  volume. 0.5 matches the AVR's own remote. A step is the same number in either unit, since dB and
  the AVR's scale differ by an offset, not a factor.
* **Max volume** -- the loudest the app will go, and the top of the slider and of the on-screen
  display.

`FlushIntervalMs` (how long ticks are gathered, default 40 ms) and `MinCommandIntervalMs` (minimum
spacing between two commands on the wire, default 60 ms) live in the config file for anyone who
wants to tune them.

### The window

The level sits above a slider, with a step button on either side and mute beside it. The slider is
notched to the configured step, so dragging it or clicking anywhere along it lands on the same
levels the volume keys produce; a click goes straight to where you clicked rather than creeping one
step at a time. There are no tick marks: the range and the notching are already known.

**Display unit** decides how levels read, both here and in the flyout: *dB* is what the AVR shows
on its own front panel, *Scale* is the 0 to 98 the protocol speaks. Only levels convert; a step is
the same number in both, so it has no unit of its own.

### On-screen volume display

Windows does not let a program write into its own volume flyout, so this one draws its own and
shows what the AVR reports rather than a percentage Windows no longer controls. It never takes the
focus or a click, so it can appear over a player without interrupting it.

It is meant to be indistinguishable from the Windows 11 flyout, so its measurements were taken off
the real one with a screen capture rather than guessed: 191x47 at 96 DPI, 8 px corners, centred on
the primary screen with a 14 px gap above the taskbar, a 110x4 bar 42 px in, the surface at
`#2C2C2C`, the unfilled track at white 54%, and the bar in the accent shade Windows keeps for dark
backgrounds (`AccentPalette` entry 1, not the raw `AccentColor`).

A level such as `-32,5 dB` does not fit in the three-digit space Windows leaves for a percentage,
so the pill grows to fit it. Set the display unit to *Scale* and the AVR's own 0-98 fits in the
same pill as Windows'.

Because the connection stays open, the AVR reports changes made from its own remote or front panel
too, and those show up in the display as well. Untick *Also when the AVR's own remote is used* if
you would rather only see your own changes.

*Max volume* sets the top of the display's scale. It is filled in automatically from what the AVR
reports (`MVMAX`) when the session opens, which is the ceiling the receiver is configured for and
not always the 98 a Denon leaves the factory with.

That first answer is then kept for the rest of the run, on purpose. Receivers repeat `MVMAX` as the
volume moves and not all of them repeat the same number; taking each one would rescale the bar
under the user while they are still turning the wheel. Type a value yourself and the AVR's answer
stops being applied at all.

What the box says is what the app enforces and what the slider runs to, applied as you type rather
than on Save. The AVR's reported maximum only supplies the starting value: where the two disagree
the box wins, and a receiver that will not go that loud simply answers with where it stopped, which
is then what the app shows.

The level is only available for Denon and Marantz; on StormAudio the display only reports mute.

### Supported AVRs

* Denon
* Marantz
* StormAudio

Support for other AVRs could be pretty easily added to the existing code.

Original autor note from [nicko88](https://github.com/nicko88) : Please contact me if you would like me to support another AVR, especially if it's one already supported by [HTWebRemote](https://github.com/nicko88/HTWebRemote)

### How To Use

Simply download the latest release, place the program .exe wherever you like, and run it.

After running it, select your AVR from the dropown and enter it's network IP address, then hit Save.

You can also add the program, or a shortcut to it to your Windows startup folder so that the program automatically starts with your PC.
To access your startup folder press Win+R and type `shell:Startup`

### Optional experimental :
`VolumUp.exe` and `VolumeDown.exe` in the project files are optional .exe shortcuts than can be placed directly as buttons in the taskbar.
Probably needs HTPCAVRVolume.exe and HTPCAVRVolumeConfig.txt to be located in `C:\ProgramData\HTPCAVRVolume.v1.1`
Honestly I don't remember how and when I built those, maybe with AutoHotkey idk.
