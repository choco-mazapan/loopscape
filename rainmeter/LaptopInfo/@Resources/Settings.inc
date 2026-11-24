; Shared preferences for the LaptopInfo widgets. The Settings panel (right-click a widget > Settings...) edits
; this file; both widgets include it with @Include2=#@#Settings.inc.
[Variables]
; Stacked widgets take the width of the widest one (1 = on)
MatchWidths=1
; Snap widgets to screen spots / each other after dragging (1 = on)
SnapEnabled=1
; Gap below the bottom-left / bottom-centre / bottom-right snap spots: 76 clears the taskbar, 28 sits over a
; transparent taskbar (same margin as the other edges), 8 hugs the bottom of the screen
SnapBottomMargin=76
; Clock: 24 (14:30) or 12 (2:30 PM)
ClockFormat=12
; fahrenheit or celsius
WeatherUnit=celsius
; Weather location: city (the city below) or windows (Windows location service; Windows asks for permission)
WeatherLocation=city
; City for the weather, e.g. Paris or Paris, France
WeatherCity=
; Panel darkness behind the text: 0 off (no tint), 110 light, 150 medium, 200 dark
PanelAlpha=0
; Effect around text: Shadow, Border (outline) or None
TextEffect=Shadow
; Text boost, readability with no backdrop: 0 off (Text effect above), 1 soft shadow, 2 drop shadow, 3 halo,
; 4 outline + halo, 5 bold + outline + halo, 6 maximum (also pure white/black text at full opacity)
TextBoost=5
; Rounded pills behind each text line when the panel tint is off (1 = on)
TextPills=1
; Adaptive text colour when the panel tint is off: off, contrast (white/dark), smooth (white/dark with fades, checked
; every second), tinted (colours taken from the wallpaper), smoothtinted, perline (each line on its own)
TextColorMode=contrast
; Clock widget shown (1) or hidden (0)
ShowClock=1
; Wi-Fi widget shown (1) or hidden (0)
ShowWiFi=1
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
