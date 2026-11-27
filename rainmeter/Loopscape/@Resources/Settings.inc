; Shared preferences for the Loopscape widgets. The Settings panel (right-click a widget > Settings...) edits
; this file; both widgets include it with @Include2=#@#Settings.inc.
[Variables]
; Stacked widgets take the width of the widest one (1 = on)
MatchWidths=1
; Snap widgets to screen spots / each other after dragging (1 = on)
SnapEnabled=1
; How close (px) a dropped widget must be to a screen spot or another widget's edge to snap to it
SnapRadius=48
; Dropped farther away: move to the nearest point of a grid (1) or stay exactly where dropped (0)
SnapGrid=1
; Grid spacing (px)
SnapGridSize=24
; Height of the bottom-left / bottom-centre / bottom-right snap spots above the bottom of the screen, in pixels:
; 76 clears the taskbar, 28 sits over a transparent taskbar (same margin as the other edges), 8 hugs the bottom of
; the screen. The Settings panel also takes any number from 0 to 400.
SnapBottomMargin=76
; Widget size: 0 = automatic (bigger on screens taller than 1080 pixels: 1.25 at 1440p, 2 at 4K), or a factor:
; 1 (100%), 1.25, 1.5, 1.75, 2 or 2.5. See Scale.inc
WidgetScale=0
; Clock: 24 (14:30) or 12 (2:30 PM)
ClockFormat=12
; Clock date line, as a Rainmeter Time format: %a, %#d %B (Wed, 25 November), %a, %m/%d/%y (Wed, 11/25/26),
; %a, %d/%m/%y (Wed, 25/11/26), %a, %m/%d/%Y (Wed, 11/25/2026) or %a, %d/%m/%Y (Wed, 25/11/2026)
DateFormat=%a, %#d %B
; fahrenheit or celsius
WeatherUnit=celsius
; Weather location: city (the city below) or windows (Windows location service; Windows asks for permission)
WeatherLocation=city
; City for the weather, e.g. Paris or Paris, France
WeatherCity=
; Panel darkness: a dark rounded box behind each widget. 0 off (no box), 110 light, 150 medium, 200 dark. While it is
; on, the text is always white, so Text pills and TextColorMode are not used
PanelAlpha=0
; Effect around text: Shadow, Border (outline) or None
TextEffect=Shadow
; Text boost, readability with no backdrop: 0 off (Text effect above), 1 soft shadow, 2 drop shadow, 3 halo,
; 4 outline + halo, 5 bold + outline + halo, 6 maximum (also pure white/black text at full opacity)
TextBoost=5
; Rounded pills behind each text line when the panel tint is off (1 = on)
TextPills=1
; Animate the clock's weather icon: falling rain and snow, twinkling stars, flashing lightning (1 = on)
AnimateWeather=0
; Adaptive text colour when the panel is off: smooth (white or dark text to suit the wallpaper, checked every second,
; fades between them) or off. The Settings panel offers only these two; Loopscape also still reads the older modes
; contrast, tinted, smoothtinted and perline, and the panel shows them as On
TextColorMode=smooth
; Clock widget shown (1) or hidden (0)
ShowClock=1
; Wi-Fi widget shown (1) or hidden (0)
ShowWiFi=1
; Now Showing widget (the wallpaper on screen, with previous / pause / next buttons) shown (1) or hidden (0)
ShowNowShowing=1
; Sun & Moon widget (the sun's arc, next sunrise / sunset, moon phase) shown (1) or hidden (0)
ShowSunMoon=0
; Rain widget (rain or snow in the next 3 hours) shown (1) or hidden (0)
ShowRain=0
; System widget (processor, memory, free space on C:) shown (1) or hidden (0)
ShowSystem=0
; Calendar widget (this month, today ringed) shown (1) or hidden (0)
ShowCalendar=0
; Wallpaper: rotate (dark styles at night), or a single style name (day: spectrum, halftone, bluesky, pixelclouds,
; sunset, riso, dunes, lake, pastel, crt, shore; night: nightclouds, aurora, nebula, moon, embers, deepsea, rain, bounce;
; or gradient)
WallpaperMode=rotate
; Move the widgets to each wallpaper style's recommended spot when the style changes (1 = on)
AutoPlace=0
; Wallpaper rotation (read by Loopscape; these override the same settings in Loopscape.ini):
; styles left out of the rotation (comma-separated)
WallExclude=
; minutes between style changes
WallRotateMinutes=60
; night styles: fixed (19:00-07:00) or sun (sunset to sunrise)
WallNight=fixed
; 1 = match the weather (rain, clear night, overcast)
WallWeather=0
; on battery: pause, play or static (still image)
WallOnBattery=pause
; freeze the video while a window covers the desktop (1 = on)
WallPauseCovered=1
; keep the wallpaper moving on battery and behind full-screen windows (1 = on, 0 = off). Empty: use alwaysAnimate=
; in Loopscape.ini. The tray's Always animate sets this too
WallAlwaysAnimate=
