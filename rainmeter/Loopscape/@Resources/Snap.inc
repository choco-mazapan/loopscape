; Shared snapping for Loopscape widgets (include with @Include=#@#Snap.inc at the top of [Variables]).
; When you drop a widget near a free target, it moves into it: one of 8 screen spots (corners, top/bottom centre,
; left/right middle; bottom spots leave room for the auto-hiding taskbar), a stack spot directly above/below
; another widget, or a spot beside another widget with the top or bottom corners lined up. Farther from every
; target, it moves to the nearest grid point (or stays where it was dropped with the grid off). Stacked widgets
; follow the one they sit on, and move with it as one group.
; On/off (SnapEnabled), the snap distance and the grid live in Settings.inc. SnapAttach and SnapPlace are written
; into a widget's own [Variables] automatically.
[Variables]
; Widget size: 0 = automatic. Default only, for a Settings.inc from before the setting (Settings.inc, included after
; this file, sets it). Scale.inc turns it into #Scale#
WidgetScale=0
SnapMargin=28
; Gap under the bottom spots. Default only: the Settings panel ("Bottom snap spots") sets it in Settings.inc
SnapBottomMargin=76
; Space between stacked widgets
SnapGap=12
; Same, when the panel tint is off (PanelAlpha=0): the panels' padding (PanelPadding=14 in each widget) is invisible
; then, so they overlap by this much to keep the same 12 px between the text lines (14 + 14 - 16)
SnapGapClear=-16
; Widgets that know about each other (config names, separated by |)
SnapPeers=Loopscape\Clock|Loopscape\WiFi|Loopscape\NowShowing|Loopscape\SunMoon|Loopscape\Rain|Loopscape\System|Loopscape\Calendar
SnapAttach=
; Views (numbers, separated by |) in which the widget stands alone: it doesn't stack on other widgets, and they don't
; stack on it. Switching to one of these views detaches it from its stack. Empty: every view stacks.
SnapAloneViews=
; Where a free-standing widget is parked, written automatically: a screen spot (spot:br) or its distance from the
; nearest screen edges (free:R,40,B,120), so it keeps that place when it changes size
SnapPlace=
; Size the widget had last time, written automatically (snap.lua: used while it loads)
SnapSize=
; Which screen column the widget is in (0 left, 1 centre, 2 right), set by snap.lua: one-line views line their
; text up with it, so a stack reads as one block in every column
ColumnAlign=0
; Opens the Settings panel, or brings it back if it is already open. !ActivateConfig alone does nothing for a panel
; that is open but out of sight (behind other windows or minimized), so the panel is also shown (which restores a
; minimized window), put on top, and asked to tell Loopscape, which turns it back into a normal window in front
; (settings.lua AskLoopscape, wallpaper\Loopscape\SettingsTaskbar.cs). The tray icon sends the same bangs (Tray.cs).
OpenSettings=[!ActivateConfig "Loopscape\Settings" "Settings.ini"][!Show "Loopscape\Settings"][!ZPos 1 "Loopscape\Settings"][!CommandMeasure MeasureSettings "AskLoopscape()" "Loopscape\Settings"]
; Run by each widget as it closes ([Rainmeter] OnCloseAction): it tells the widgets that are running, which then
; check who is still there (snap.lua Closing, PeerGone)
SnapOnClose=[!CommandMeasure MeasureSnap "Closing()"]

[MeasureSnap]
Measure=Script
ScriptFile=#@#snap.lua
UpdateDivider=1