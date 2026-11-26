; Shared snapping for Loopscape widgets (include with @Include=#@#Snap.inc at the top of [Variables]).
; After you drag a widget and let go, it moves to the nearest free target: one of 8 screen spots (corners,
; top/bottom centre, left/right middle; bottom spots leave room for the auto-hiding taskbar) or a stack spot
; directly above/below another widget. Stacked widgets follow the one they sit on.
; On/off (SnapEnabled) lives in Settings.inc. SnapAttach is written into a widget's own [Variables]
; automatically when it is stacked.
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
; Size the widget had last time, written automatically (snap.lua: used while it loads)
SnapSize=
; Which screen column the widget is in (0 left, 1 centre, 2 right), set by snap.lua: one-line views line their
; text up with it, so a stack reads as one block in every column
ColumnAlign=0
; Run by each widget as it closes ([Rainmeter] OnCloseAction): it tells the widgets that are running, which then
; check who is still there (snap.lua Closing, PeerGone)
SnapOnClose=[!CommandMeasure MeasureSnap "Closing()"]

[MeasureSnap]
Measure=Script
ScriptFile=#@#snap.lua
UpdateDivider=1